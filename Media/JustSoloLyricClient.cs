using System.Globalization;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace NotchPeninsula
{
    internal sealed class JustSoloLyricClient
    {
        private const string ServerUrl = "ws://127.0.0.1:47290";
        private const int InitialReconnectDelayMs = 1000;
        private const int MaxReconnectDelayMs = 30000;
        private const float DefaultLyricPreReadSeconds = 0.13f;
        private const double SpectrumStaleMs = 500d;
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
        private float _volume;
        private bool _hasVolume;

        private volatile bool _running;   // 是否期望保持连接
        private volatile bool _connected; // 当前是否已连接
        private ClientWebSocket? _ws;
        private readonly SemaphoreSlim _sendLock = new(1, 1);
        private CancellationTokenSource? _cts;
        private Task? _loopTask;

        public bool IsRunning => _running;
        public bool IsConnected => _connected;

        public bool TryGetVolume(out float volume)
        {
            lock (_lock)
            {
                volume = _volume;
                return _hasVolume;
            }
        }

        public bool HasTranslation
        {
            get { lock (_lock) return _hasTranslation; }
        }

        public void Start()
        {
            if (_running) return;
            _running = true;
            ResetState();

            var cts = new CancellationTokenSource();
            _cts = cts;
            _loopTask = Task.Run(() => ConnectLoopAsync(cts.Token));
        }

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

            double elapsedMs = playing ? (DateTime.UtcNow - stamp).TotalMilliseconds : 0d;
            double currentMs = position + elapsedMs + (DefaultLyricPreReadSeconds + userOffsetSeconds) * 1000.0;

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

        public void SendVolume(float value)
        {
            var ws = _ws;
            if (ws == null || ws.State != WebSocketState.Open) return;

            float clamped = Math.Clamp(value, 0f, 1f);
            UpdateVolumeMirror(clamped);

            string json = "{\"type\":\"volume\",\"value\":"
                + clamped.ToString("F3", CultureInfo.InvariantCulture) + "}";
            byte[] payload = Encoding.UTF8.GetBytes(json);

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
                catch (Exception)
                {
                    Logger.DebugThrottled("Just Solo LyricServer 连接失败（按退避重连中，同类消息已折叠）");
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

        private static readonly char[] MergedLineSeparators = { '\n', '\r', '\u2028', '\u2029', '\u0085', '\u000B', '\u000C' };

        private static void SplitMergedLine(ref string text, ref string translation)
        {
            if (string.IsNullOrEmpty(text)) return;
            if (text.IndexOfAny(MergedLineSeparators) < 0) return;

            var parts = text.Split(MergedLineSeparators, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2) return;

            text = parts[0].Trim();
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
