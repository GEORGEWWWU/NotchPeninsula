using System.Collections.Concurrent;
using System.IO;
using System.Text;

namespace NotchPeninsula
{
    /// <summary>
    /// 全进程唯一的日志门面。
    ///
    /// 写入是**异步**的：调用方只做「节流判定 + 入队」，真正的磁盘写在一条
    /// 低优先级的后台线程上。原因很实际：渲染线程里散布着按秒触发的诊断日志
    /// （玻璃重算体检、插件测宽、背板节拍），同步落盘会把一次 0.2ms 的绘制
    /// 拖成几十毫秒 —— 日志里的 [渲染卡顿] 有相当一部分是这么来的。
    ///
    /// 代价是崩溃瞬间可能丢最后几行：所以 WARN/ERROR 入队后立即催刷，
    /// 且崩溃处理与进程退出都会显式 <see cref="Flush(int)"/>。
    /// </summary>
    public static class Logger
    {
        private static readonly object _lock = new object();
        private static readonly string LogPath = ResolveLogPath();

        /// <summary>当前日志文件（app.log）完整路径。</summary>
        public static string LogFilePath => LogPath;

        /// <summary>日志目录 —— 崩溃文件与诊断包也落在这下面，别各自再算一遍路径。</summary>
        public static string LogDirectory
        {
            get
            {
                try { return Path.GetDirectoryName(LogPath) ?? Path.GetTempPath(); }
                catch { return Path.GetTempPath(); }
            }
        }

        /// <summary>按写入时间倒序取最近若干份日志（含当前 app.log 与轮转出来的 app-*.log）。</summary>
        public static List<string> GetRecentLogFiles(int max)
        {
            var list = new List<string>();
            if (max <= 0) return list;

            try
            {
                var dir = new DirectoryInfo(LogDirectory);
                if (!dir.Exists) return list;

                var files = dir.GetFiles("app*.log");
                Array.Sort(files, (a, b) => b.LastWriteTimeUtc.CompareTo(a.LastWriteTimeUtc));
                foreach (var f in files)
                {
                    if (list.Count >= max) break;
                    list.Add(f.FullName);
                }
            }
            catch { }

            return list;
        }

        private const long MaxBytes = 1024 * 1024;

        private const int MaxRotatedFiles = 2;

        private const string RotateMutexName = @"Local\NotchPeninsula_LogRotate";

        // ---- 异步写盘 ----
        private static readonly ConcurrentQueue<string> _queue = new();
        private static readonly SemaphoreSlim _signal = new(0);
        private static Thread? _writerThread;
        private static volatile bool _writerStop;
        private static int _pending;              // 队列里未写出的行数（Flush 用）
        private static int _writerStarted;

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
                bool urgent = level is "WARN" or "ERROR";
                string prefix;
                string line;

                lock (_lock)
                {
                    if (throttleKey != null && IsThrottled(level, throttleKey)) return;
                    prefix = TakeThrottleSummary();
                }

                line = prefix + $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [{level}] {msg}{Environment.NewLine}";

                _queue.Enqueue(line);
                Interlocked.Increment(ref _pending);

                EnsureWriter();
                _signal.Release();
                if (urgent) _signal.Release();   // 立刻把 writer 叫醒，别攒批
            }
            catch
            {
                // 记日志本身绝不能再抛
            }
        }

        private static void EnsureWriter()
        {
            if (Volatile.Read(ref _writerStarted) == 1) return;
            lock (_lock)
            {
                if (_writerStarted == 1) return;
                _writerThread = new Thread(WriterLoop)
                {
                    IsBackground = true,
                    Name = "NPS-Log",
                    Priority = ThreadPriority.BelowNormal
                };
                _writerThread.Start();
                Volatile.Write(ref _writerStarted, 1);
            }
        }

        private static void WriterLoop()
        {
            var sb = new StringBuilder(8192);
            while (!_writerStop)
            {
                try { _signal.Wait(500); } catch { }

                // 攒一批再写：一次 syscall 处理多行
                while (_queue.TryDequeue(out string? line))
                {
                    sb.Append(line);
                    Interlocked.Decrement(ref _pending);
                    if (sb.Length >= 16384) break;
                }

                if (sb.Length > 0) Drain(sb);
            }

            // 退出：把剩下的写完
            while (_queue.TryDequeue(out string? line))
            {
                sb.Append(line);
                Interlocked.Decrement(ref _pending);
            }
            if (sb.Length > 0) Drain(sb);
        }

        private static void Drain(StringBuilder sb)
        {
            try
            {
                _writer ??= OpenWriter();
                if (_writer == null) { sb.Clear(); return; }

                string text = sb.ToString();
                sb.Clear();

                _writer.Write(text);
                _writer.Flush();

                _bytesWritten += Encoding.UTF8.GetByteCount(text);
                if (_bytesWritten >= MaxBytes) Rotate();
            }
            catch
            {
                try { _writer?.Dispose(); } catch { }
                _writer = null;
                sb.Clear();
            }
        }

        /// <summary>
        /// 把队列里已入队的行写出。崩溃 / 退出路径必须调它，否则最后几行会丢。
        /// 只等不丢：超时后也返回，绝不阻塞调用方更久。
        /// </summary>
        public static void Flush(int timeoutMs = 1000)
        {
            try
            {
                if (Volatile.Read(ref _writerStarted) == 0) return;

                int waited = 0;
                while (Volatile.Read(ref _pending) > 0 && waited < timeoutMs)
                {
                    _signal.Release();
                    Thread.Sleep(2);
                    waited += 2;
                }
                _writer?.Flush();
            }
            catch { }
        }

        /// <summary>进程退出时调用：停止 writer 并收尾（最多 1s）。</summary>
        public static void Shutdown()
        {
            try
            {
                if (Volatile.Read(ref _writerStarted) == 0) return;
                Flush(500);
                _writerStop = true;
                _signal.Release();
                _writerThread?.Join(800);
                try { _writer?.Dispose(); } catch { }
                _writer = null;
            }
            catch { }
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

            // 换消息或过窗口：这一条放行，上一段的折叠条数并入它前面的摘要
            _lastThrottledKey = key;
            _lastThrottledLevel = level.GetHashCode();
            _lastThrottledAt = now;
            return false;
        }

        /// <summary>取走待补的折叠摘要（调用方持 _lock）。</summary>
        private static string TakeThrottleSummary()
        {
            if (_throttledSuppressed <= 0) return "";
            int n = _throttledSuppressed;
            _throttledSuppressed = 0;
            return $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [DEBUG] …上一行同类消息在 {ThrottleWindow.TotalSeconds:F0}s 内重复 {n} 次，已折叠{Environment.NewLine}";
        }

        private static StreamWriter? OpenWriter()
        {
            var stream = new FileStream(LogPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
            _bytesWritten = stream.Length;
            // 不自动刷新：由 Drain 按批 flush，减少 syscall
            return new StreamWriter(stream) { AutoFlush = false };
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
