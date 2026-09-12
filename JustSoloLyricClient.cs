using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace NotchPeninsula
{
    /// <summary>
    /// Just Solo LyricServer（ws://127.0.0.1:47290）客户端。
    /// 单向接收 init / progress / playback 推送，供本地歌词高亮使用。
    /// 协议文档：Just-Solo-LyricServer.md
    /// </summary>
    internal sealed class JustSoloLyricClient
    {
        private const string ServerUrl = "ws://127.0.0.1:47290";
        private const int InitialReconnectDelayMs = 1000;
        private const int MaxReconnectDelayMs = 30000;
        // Just Solo 自身高亮使用的默认歌词预读偏移（client 需自行补偿才能与其同步）
        private const float DefaultLyricPreReadSeconds = 0.13f;

        private readonly record struct LyricLine(int Time, string Text, string Translation);

        private readonly object _lock = new();
        private LyricLine[] _lyrics = Array.Empty<LyricLine>();
        private int _position;
        private DateTime _positionStamp = DateTime.UtcNow;
        private bool _isPlaying;

        private volatile bool _running;   // 是否期望保持连接
        private volatile bool _connected; // 当前是否已连接
        private CancellationTokenSource? _cts;
        private Task? _loopTask;

        public bool IsRunning => _running;
        public bool IsConnected => _connected;

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
        /// </summary>
        public bool TryGetCurrentLyric(float userOffsetSeconds, out string text, out float progress)
        {
            text = "";
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
            text = string.IsNullOrEmpty(line.Text) ? line.Translation : line.Text;

            double endMs = ans < lyrics.Length - 1 ? lyrics[ans + 1].Time : line.Time + 4000d;
            double duration = endMs - line.Time;
            if (duration > 0)
                progress = (float)Math.Clamp((currentMs - line.Time) / duration, 0d, 1d);

            return true;
        }

        private void ResetState()
        {
            lock (_lock)
            {
                _lyrics = Array.Empty<LyricLine>();
                _position = 0;
                _positionStamp = DateTime.UtcNow;
                _isPlaying = false;
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
            var sb = new StringBuilder();

            while (ws.State == WebSocketState.Open && !token.IsCancellationRequested)
            {
                var result = await ws.ReceiveAsync(buffer, token);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    await ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, token);
                    break;
                }

                sb.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
                if (!result.EndOfMessage) continue;

                HandleMessage(sb.ToString());
                sb.Clear();
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
                        }
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
                lock (_lock) _lyrics = Array.Empty<LyricLine>();
                return;
            }

            var lines = new List<LyricLine>(arr.GetArrayLength());
            foreach (var item in arr.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;

                int time = item.TryGetProperty("time", out var t) && t.TryGetInt32(out int tv) ? tv : 0;
                string text = item.TryGetProperty("text", out var tx) ? tx.GetString() ?? "" : "";
                string translation = item.TryGetProperty("translation", out var tr) ? tr.GetString() ?? "" : "";
                lines.Add(new LyricLine(time, text, translation));
            }

            lock (_lock) _lyrics = lines.ToArray();
        }
    }
}
