using System.Globalization;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace NotchPeninsula
{
    /// <summary>
    /// Just Solo LyricServer（ws://127.0.0.1:47290）客户端。
    /// 接收 init / progress / playback / spectrum 推送，供本地歌词高亮与频谱显示使用；
    /// 协议 v1.3.0 起 volume 为**双向**消息：可下发指令调播放器音量，服务端也会回推音量
    /// （协议 4.5 节）。
    /// 协议文档：Just-Solo-LyricServer.md
    /// </summary>
    internal sealed class JustSoloLyricClient
    {
        private const string ServerUrl = "ws://127.0.0.1:47290";
        private const int InitialReconnectDelayMs = 1000;
        private const int MaxReconnectDelayMs = 30000;
        // Just Solo 自身高亮使用的默认歌词预读偏移（client 需自行补偿才能与其同步）
        private const float DefaultLyricPreReadSeconds = 0.13f;
        // 超过该时长未收到 spectrum 即视为无数据（服务端推送周期 100ms），回退到本地音频采集
        private const double SpectrumStaleMs = 500d;
        // 音量镜像的「视为没变」阈值（吃掉服务端与本机之间的浮点 / 取整差异）
        private const float VolumeDelta = 0.0005f;

        private readonly record struct LyricLine(int Time, string Text, string Translation);

        private readonly object _lock = new();
        private LyricLine[] _lyrics = Array.Empty<LyricLine>();
        private bool _hasTranslation;
        private int _position;
        private DateTime _positionStamp = DateTime.UtcNow;
        private bool _isPlaying;
        private float[] _spectrum = Array.Empty<float>();
        private DateTime _spectrumStamp = DateTime.MinValue;
        // Just Solo 播放器音量的镜像（与系统音量是两个互不覆盖的独立变量）
        private float _volume;
        private bool _hasVolume;

        private volatile bool _running;   // 是否期望保持连接
        private volatile bool _connected; // 当前是否已连接
        // 已建立的会话，供 SendVolume 从其他线程下发指令（接收循环由连接任务持有）
        private ClientWebSocket? _ws;
        // ClientWebSocket 不允许并发 SendAsync，多线程同时下发时用信号量串行化
        private readonly SemaphoreSlim _sendLock = new(1, 1);
        private CancellationTokenSource? _cts;
        private Task? _loopTask;

        public bool IsRunning => _running;
        public bool IsConnected => _connected;

        /// <summary>
        /// Just Solo 播放器当前音量（0.0 ~ 1.0）—— **WS 侧的独立变量**，与系统音量互不覆盖。
        /// 本机下发的值、服务端回推的值、连接时补推的当前值都会镜像进来。
        /// 返回 false 表示还没从 WS 拿到过音量（未连接 / 服务端还没推）。
        /// </summary>
        public bool TryGetVolume(out float volume)
        {
            lock (_lock)
            {
                volume = _volume;
                return _hasVolume;
            }
        }

        /// <summary>
        /// 当前这首歌的时间轴里是否存在翻译行。判定挂在「整首歌」而不是「当前这一句」上：
        /// 只有逐句判定的话，没有翻译的句子会让岛体高度一会儿高一会儿低，看起来像在抽搐。
        /// </summary>
        public bool HasTranslation
        {
            get { lock (_lock) return _hasTranslation; }
        }

        /// <summary>开始连接（幂等）。</summary>
        public void Start()
        {
            if (_running) return;
            _running = true;
            ResetState();

            var cts = new CancellationTokenSource();
            _cts = cts;
            _loopTask = Task.Run(() => ConnectLoopAsync(cts.Token));
        }

        /// <summary>停止连接并清空状态。</summary>
        public void Stop()
        {
            if (!_running) return;
            _running = false;

            var cts = _cts;
            _cts = null;
            var task = _loopTask;
            _loopTask = null;

            try { cts?.Cancel(); } catch { }

            if (task != null) _ = task.ContinueWith(_ => cts?.Dispose(), TaskScheduler.Default);
            else cts?.Dispose();

            _connected = false;
            ResetState();
        }

        /// <summary>
        /// 读取当前应显示的歌词行。返回 false 表示 LyricServer 不可用，调用方应回退到其他歌词来源。
        /// 已连接但无歌词（纯音乐）时返回 true 且 text 为空。
        /// <paramref name="translation"/> 是协议里同一时间戳的第二行（可选），无译文时为空串。
        /// </summary>
        public bool TryGetCurrentLyric(float userOffsetSeconds, out string text, out string translation, out float progress)
        {
            text = "";
            translation = "";
            progress = 0f;

            LyricLine[] lyrics;
            int position;
            bool playing;
            DateTime stamp;
            lock (_lock)
            {
                if (!_connected) return false;
                lyrics = _lyrics;
                position = _position;
                playing = _isPlaying;
                stamp = _positionStamp;
            }

            if (lyrics.Length == 0) return true;

            // 进度每 400ms 才推送一次，用经过时间做线性插值保证高亮平滑
            double elapsedMs = playing ? (DateTime.UtcNow - stamp).TotalMilliseconds : 0d;
            double currentMs = position + elapsedMs + (DefaultLyricPreReadSeconds + userOffsetSeconds) * 1000.0;

            // 二分查找最后一个 time <= currentMs 的歌词行
            int lo = 0, hi = lyrics.Length - 1, ans = -1;
            while (lo <= hi)
            {
                int mid = (lo + hi) >> 1;
                if (lyrics[mid].Time <= currentMs) { ans = mid; lo = mid + 1; }
                else hi = mid - 1;
            }
            if (ans < 0) return true;

            var line = lyrics[ans];
            text = line.Text;
            translation = line.Translation;

            // 只有翻译、没有原文的行（典型的「间奏行只给了译文」）：把翻译当正文显示，
            // 否则同一句话会被上下画两遍。
            if (string.IsNullOrEmpty(text))
            {
                text = translation;
                translation = "";
            }

            double endMs = ans < lyrics.Length - 1 ? lyrics[ans + 1].Time : line.Time + 4000d;
            double duration = endMs - line.Time;
            if (duration > 0)
                progress = (float)Math.Clamp((currentMs - line.Time) / duration, 0d, 1d);

            return true;
        }

        /// <summary>
        /// 读取 LyricServer 推送的实时频谱（协议 v1.2.0+，12 频段，0.0~1.0，低频→高频）。
        /// 返回 false 表示未连接、服务端不支持（v1.2.0 之前）或当前无频谱数据，调用方应回退到本地音频采集。
        /// </summary>
        public bool TryGetSpectrum(out float[] bands)
        {
            bands = Array.Empty<float>();

            lock (_lock)
            {
                if (!_connected) return false;
                if (_spectrum.Length == 0) return false;
                if ((DateTime.UtcNow - _spectrumStamp).TotalMilliseconds > SpectrumStaleMs) return false;

                bands = _spectrum;
                return true;
            }
        }

        /// <summary>
        /// 下发 volume 指令（协议 v1.3.0，0.0~1.0，越界自动裁剪），调节 Just Solo 播放器音量。
        /// 未连接时静默丢弃；发出的值会镜像进 <see cref="TryGetVolume"/> 的 Just Solo 音量变量。
        /// 可高频调用，服务端按到达顺序依次应用。
        /// </summary>
        public void SendVolume(float value)
        {
            var ws = _ws;
            if (ws == null || ws.State != WebSocketState.Open) return;

            float clamped = Math.Clamp(value, 0f, 1f);
            UpdateVolumeMirror(clamped);

            string json = "{\"type\":\"volume\",\"value\":"
                + clamped.ToString("F3", CultureInfo.InvariantCulture) + "}";
            byte[] payload = Encoding.UTF8.GetBytes(json);

            // 不阻塞调用线程（渲染线程会走这条路）：发送在本任务内排队，锁只保证不并发 SendAsync。
            _ = Task.Run(async () =>
            {
                await _sendLock.WaitAsync();
                try
                {
                    await ws.SendAsync(payload, WebSocketMessageType.Text, true, CancellationToken.None);
                }
                catch (Exception ex)
                {
                    Logger.Debug($"Just Solo LyricServer 音量指令下发失败: {ex.Message}");
                }
                finally
                {
                    _sendLock.Release();
                }
            });
        }

        /// <summary>
        /// 处理服务端回推的音量：只镜像进 Just Solo 音量变量，不做任何反向动作。
        /// 因此「收到服务端 volume 又回发」的来回抖动在本设计里根本不存在（协议 4.5 明确要求不要回发）；
        /// 连接时补推的当前音量同样走这里 —— 它本来就代表播放器真实音量，不必特殊对待。
        /// </summary>
        private void ApplyVolume(JsonElement root)
        {
            if (!root.TryGetProperty("value", out var valueEl) || !valueEl.TryGetSingle(out float value))
            {
                Logger.Warn("Just Solo LyricServer 音量消息缺少合法 value，已忽略");
                return;
            }

            UpdateVolumeMirror(Math.Clamp(value, 0f, 1f));
        }

        private void UpdateVolumeMirror(float value)
        {
            lock (_lock)
            {
                if (_hasVolume && Math.Abs(value - _volume) < VolumeDelta) return;
                _volume = value;
                _hasVolume = true;
            }
            Logger.Debug($"Just Solo 音量镜像：{value:F2}");
        }

        private void ResetState()
        {
            lock (_lock)
            {
                _lyrics = Array.Empty<LyricLine>();
                _hasTranslation = false;
                _position = 0;
                _positionStamp = DateTime.UtcNow;
                _isPlaying = false;
                _spectrum = Array.Empty<float>();
                _spectrumStamp = DateTime.MinValue;
            }
        }

        private async Task ConnectLoopAsync(CancellationToken token)
        {
            int delayMs = InitialReconnectDelayMs;

            while (!token.IsCancellationRequested)
            {
                try
                {
                    using var ws = new ClientWebSocket();
                    await ws.ConnectAsync(new Uri(ServerUrl), token);

                    _connected = true;
                    _ws = ws;
                    delayMs = InitialReconnectDelayMs;
                    Logger.Info("Just Solo LyricServer 已连接");

                    await SendHelloAsync(ws, token);
                    await ReceiveLoopAsync(ws, token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Logger.Debug($"Just Solo LyricServer 连接失败: {ex.Message}");
                }
                finally
                {
                    _connected = false;
                    _ws = null;
                }

                if (token.IsCancellationRequested) break;

                // 指数退避：1s → 2s → … → 30s
                try { await Task.Delay(delayMs, token); }
                catch (OperationCanceledException) { break; }
                delayMs = Math.Min(delayMs * 2, MaxReconnectDelayMs);
            }

            _connected = false;
        }

        private static async Task SendHelloAsync(ClientWebSocket ws, CancellationToken token)
        {
            var payload = Encoding.UTF8.GetBytes("{\"type\":\"hello\",\"client\":\"Notch Peninsula\"}");
            await ws.SendAsync(payload, WebSocketMessageType.Text, true, token);
        }

        private async Task ReceiveLoopAsync(ClientWebSocket ws, CancellationToken token)
        {
            var buffer = new byte[4096];

            // ⚠️ 必须「先攒原始字节、整条消息到齐了再解码」，不能按帧各自 UTF8.GetString：
            //    服务端把整首歌的歌词一次性发出来（实测 init 单条消息 5565 字节），超过这个 4KB 缓冲后
            //    会被切成多帧；逐帧解码时，落在帧边界上的中文（3 字节 UTF-8）会被解成 U+FFFD「�」——
            //    用户 2026-09-24 反馈的「译文里显示一个乱码」就是这么来的（实测 4096 边界处恰好切在
            //    一句译文中间：「渐渐开始怀疑你是否会如期出���在我面前」，整条解码则完好）。
            //    MemoryStream 循环复用，稳态不产生额外分配。
            using var payload = new MemoryStream();

            while (ws.State == WebSocketState.Open && !token.IsCancellationRequested)
            {
                var result = await ws.ReceiveAsync(buffer, token);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    await ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, token);
                    break;
                }

                payload.Write(buffer, 0, result.Count);
                if (!result.EndOfMessage) continue;

                HandleMessage(Encoding.UTF8.GetString(payload.GetBuffer(), 0, (int)payload.Length));
                payload.SetLength(0);
            }
        }

        private void HandleMessage(string raw)
        {
            try
            {
                using var doc = JsonDocument.Parse(raw);
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object) return;
                if (!root.TryGetProperty("type", out var typeEl)) return;

                switch (typeEl.GetString())
                {
                    case "init":
                        ApplyLyrics(root);
                        break;

                    case "progress":
                        if (root.TryGetProperty("position", out var posEl) && posEl.TryGetInt32(out int pos))
                        {
                            lock (_lock)
                            {
                                _position = pos;
                                _positionStamp = DateTime.UtcNow;
                            }
                        }
                        break;

                    case "playback":
                        bool playing = root.TryGetProperty("status", out var statusEl) && statusEl.GetString() == "playing";
                        lock (_lock)
                        {
                            _isPlaying = playing;
                            _positionStamp = DateTime.UtcNow; // 冻结/恢复时重置插值基准，避免把暂停时长算进进度
                            if (!playing)
                            {
                                // 暂停/停止后服务端不再推送频谱，直接失效以免残留最后一帧
                                _spectrum = Array.Empty<float>();
                                _spectrumStamp = DateTime.MinValue;
                            }
                        }
                        break;

                    case "spectrum":
                        ApplySpectrum(root);
                        break;

                    case "volume":
                        ApplyVolume(root);
                        break;
                }
            }
            catch (Exception ex)
            {
                Logger.Warn($"Just Solo LyricServer 消息解析失败: {ex.Message}");
            }
        }

        private void ApplyLyrics(JsonElement root)
        {
            if (!root.TryGetProperty("lyrics", out var arr) || arr.ValueKind != JsonValueKind.Array)
            {
                lock (_lock)
                {
                    _lyrics = Array.Empty<LyricLine>();
                    _hasTranslation = false;
                }
                return;
            }

            var lines = new List<LyricLine>(arr.GetArrayLength());
            bool hasTranslation = false;
            foreach (var item in arr.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;

                int time = item.TryGetProperty("time", out var t) && t.TryGetInt32(out int tv) ? tv : 0;
                string text = item.TryGetProperty("text", out var tx) ? tx.GetString() ?? "" : "";
                string translation = item.TryGetProperty("translation", out var tr) ? tr.GetString() ?? "" : "";
                SplitMergedLine(ref text, ref translation);
                if (!string.IsNullOrEmpty(translation)) hasTranslation = true;
                lines.Add(new LyricLine(time, text, translation));
            }

            lock (_lock)
            {
                _lyrics = lines.ToArray();
                _hasTranslation = hasTranslation;
            }
        }

        // 同一时间戳的多行歌词，LyricServer 有两种下发形态：
        //   ① 第二行被判定为译文 → 单独放进 translation 字段（协议文档里的标准形态）；
        //   ② 判定不出译文（中日以外的同语种对照、粤语夹普通话等）→ 两行合并进 text，用换行符分隔。
        // 形态 ② 若照原样画出来，换行符会在 Skia 里变成一个「豆腐块」乱码，两行也挤在同一行上。
        // 这里在加载时就地拆开：第一行仍是原文，其余行合并为一句话当译文 —— 之后渲染层只管两行。
        // 注：实测本机 LyricServer（2026-09-24）走的就是形态 ②：整首歌 translation 字段恒为空、
        //     47/54 行的 text 里是「原文\n译文」。
        private static readonly char[] MergedLineSeparators = { '\n', '\r', '\u2028', '\u2029', '\u0085', '\u000B', '\u000C' };

        private static void SplitMergedLine(ref string text, ref string translation)
        {
            if (string.IsNullOrEmpty(text)) return;
            if (text.IndexOfAny(MergedLineSeparators) < 0) return;

            var parts = text.Split(MergedLineSeparators, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2) return;

            text = parts[0].Trim();
            // 已经有 translation 字段时以它为准，合并进来的第二行直接丢掉，避免同一句译文重复
            if (string.IsNullOrEmpty(translation))
                translation = string.Join(" ", parts.Skip(1)).Trim();
        }

        private void ApplySpectrum(JsonElement root)
        {
            // 空数组表示刚恢复播放、缓冲未填满，按"暂无数据"处理
            if (!root.TryGetProperty("bands", out var arr) || arr.ValueKind != JsonValueKind.Array || arr.GetArrayLength() == 0)
            {
                lock (_lock)
                {
                    _spectrum = Array.Empty<float>();
                    _spectrumStamp = DateTime.MinValue;
                }
                return;
            }

            var bands = new float[arr.GetArrayLength()];
            int i = 0;
            foreach (var el in arr.EnumerateArray())
                bands[i++] = el.ValueKind == JsonValueKind.Number && el.TryGetSingle(out float v) ? v : 0f;

            lock (_lock)
            {
                _spectrum = bands;
                _spectrumStamp = DateTime.UtcNow;
            }
        }
    }
}
