using System.IO;

namespace NotchPeninsula
{
    public static class Logger
    {
        private static readonly object _lock = new object();
        private static readonly string LogPath = ResolveLogPath();

        private const long MaxBytes = 1024 * 1024;

        private const int MaxRotatedFiles = 2;

        private const string RotateMutexName = @"Local\NotchPeninsula_LogRotate";

        private static StreamWriter? _writer;

        private static long _bytesWritten;

        private static readonly TimeSpan ThrottleWindow = TimeSpan.FromSeconds(10);
        private static string _lastThrottledKey = "";
        private static int _lastThrottledLevel;
        private static DateTime _lastThrottledAt = DateTime.MinValue;
        private static int _throttledSuppressed;

        private static string ResolveLogPath()
        {
            string dir;
            try
            {
                dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "NotchPeninsula");
            }
            catch
            {
                dir = Path.Combine(Path.GetTempPath(), "NotchPeninsula");
            }

            try { Directory.CreateDirectory(dir); } catch { }
            return Path.Combine(dir, "app.log");
        }

        public static void Debug(string msg) => Write("DEBUG", msg, null);

        public static void DebugThrottled(string template) => Write("DEBUG", template, template);

        public static void Info(string msg) => Write("INFO", msg, null);

        public static void Warn(string msg) => Write("WARN", msg, null);

        public static void Error(string msg, Exception? ex = null)
            => Write("ERROR", ex == null ? msg : $"{msg} | {ex}", null);

        private static void Write(string level, string msg, string? throttleKey)
        {
            try
            {
                lock (_lock)
                {
                    if (throttleKey != null && IsThrottled(level, throttleKey)) return;

                    _writer ??= OpenWriter();
                    if (_writer == null) return;

                    FlushThrottleSummary();

                    string line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [{level}] {msg}{Environment.NewLine}";
                    _writer.Write(line);
                    _bytesWritten += line.Length + System.Text.Encoding.UTF8.GetByteCount(msg) - msg.Length;

                    if (_bytesWritten >= MaxBytes) Rotate();
                }
            }
            catch
            {
                try { _writer = null; } catch { }
            }
        }

        private static bool IsThrottled(string level, string key)
        {
            var now = DateTime.Now;
            bool same = _lastThrottledKey == key && _lastThrottledLevel == level.GetHashCode();
            if (same && now - _lastThrottledAt < ThrottleWindow)
            {
                _throttledSuppressed++;
                return true;
            }

            // 换消息或过窗口：先把上一段的折叠条数补上，再记这一条
            FlushThrottleSummary();
            _lastThrottledKey = key;
            _lastThrottledLevel = level.GetHashCode();
            _lastThrottledAt = now;
            return false;
        }

        private static void FlushThrottleSummary()
        {
            if (_throttledSuppressed <= 0) return;
            int n = _throttledSuppressed;
            _throttledSuppressed = 0;
            if (_writer == null) return;
            string line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [DEBUG] …上一行同类消息在 {ThrottleWindow.TotalSeconds:F0}s 内重复 {n} 次，已折叠{Environment.NewLine}";
            _writer.Write(line);
            _bytesWritten += line.Length;
        }

        private static StreamWriter? OpenWriter()
        {
            var stream = new FileStream(LogPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
            _bytesWritten = stream.Length;
            return new StreamWriter(stream) { AutoFlush = true };
        }

        private static void Rotate()
        {
            try { _writer?.Dispose(); } catch { }
            _writer = null;
            _bytesWritten = 0;

            Mutex? mutex = null;
            try
            {
                mutex = new Mutex(false, RotateMutexName);
                try { if (!mutex.WaitOne(TimeSpan.FromMilliseconds(200))) return; }
                catch (AbandonedMutexException) { /* 上个进程没放锁：我们接手即可 */ }

                if (File.Exists(LogPath) && new FileInfo(LogPath).Length > 0)
                {
                    string dir = Path.GetDirectoryName(LogPath) ?? ".";
                    string name = Path.GetFileNameWithoutExtension(LogPath);
                    string ext = Path.GetExtension(LogPath);
                    string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
                    string target = Path.Combine(dir, $"{name}-{stamp}{ext}");

                    // 同一秒内轮转两次（极端）时避免撞名
                    for (int i = 1; File.Exists(target) && i < 100; i++)
                        target = Path.Combine(dir, $"{name}-{stamp}-{i}{ext}");

                    File.Move(LogPath, target);
                    PruneOldLogs(dir, name, ext);
                }
            }
            catch
            {
            }
            finally
            {
                try { mutex?.ReleaseMutex(); } catch { }
                try { mutex?.Dispose(); } catch { }
            }
        }

        private static void PruneOldLogs(string dir, string baseName, string ext)
        {
            try
            {
                var old = new DirectoryInfo(dir).GetFiles($"{baseName}-*{ext}");
                if (old.Length <= MaxRotatedFiles) return;

                Array.Sort(old, (a, b) => b.LastWriteTimeUtc.CompareTo(a.LastWriteTimeUtc)); // 新的在前
                for (int i = MaxRotatedFiles; i < old.Length; i++)
                {
                    try { old[i].Delete(); } catch { }
                }
            }
            catch { }
        }
    }
}
