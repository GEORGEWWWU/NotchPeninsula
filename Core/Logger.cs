using System;
using System.IO;
using System.Threading;

namespace NotchPeninsula
{
    /// <summary>
    /// 极简文件日志：一个常开的写入器 + 按大小轮转 + 高频重复消息去重。
    ///
    /// 不用「每行 File.AppendAllText」：那样每写一行就要开→写→关一次文件句柄。平时一天几百行
    /// 无所谓，但出错重试路径是 1 行/秒级别（实测独占音频设备时「音频捕获仍未就绪」单次会话
    /// 刷了 890~1189 行），每秒开关一次文件纯属浪费，还会和杀软扫描叠加成卡顿。现在改为常开
    /// StreamWriter（AutoFlush，崩溃最多丢最后一行），只有轮转时才关文件、改名、重开。
    ///
    /// 为什么要轮转：日志只有「最近一段」有价值（实测 13 天攒到 838KB / 8862 行，越老的越没用，
    /// 排查问题时翻大文件很痛苦）。单文件超过 MaxBytes 就改名成带时间戳的备份，
    /// 最多保留 MaxRotatedFiles 份，更老的直接删，总占用因此有硬上限（约 3MB）。
    ///
    /// 为什么不做全局限流：INFO/WARN/ERROR 是诊断主干，量级天然有限，限流只会让真出问题时丢证据。
    /// 只有明确知道会成片刷屏的调用点才用 DebugThrottled。
    /// </summary>
    public static class Logger
    {
        private static readonly object _lock = new object();
        private static readonly string LogPath = ResolveLogPath();

        /// <summary>单个日志文件的大小上限；超过就轮转。</summary>
        private const long MaxBytes = 1024 * 1024;

        /// <summary>保留的历史日志份数（不含当前这份），更老的直接删除。</summary>
        private const int MaxRotatedFiles = 2;

        /// <summary>同一把锁名跨进程串行化轮转，避免两个实例同时改名。</summary>
        private const string RotateMutexName = @"Local\NotchPeninsula_LogRotate";

        /// <summary>常开的写入器。只在轮转时关闭重开；任何异常后置空，下次写入自愈重建。</summary>
        private static StreamWriter? _writer;

        /// <summary>已写入当前文件的近似字节数（含 UTF-8 中文的额外字节），用于免 stat 判断是否需要轮转。</summary>
        private static long _bytesWritten;

        // ---- 高频重复消息去重（只作用于 DebugThrottled） ----
        /// <summary>同一条消息在这个窗口内只写第一行，其余计数后合并成一行「重复 N 次」。</summary>
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

        /// <summary>
        /// 调试日志。始终写入（不再要求 -debug 启动）：
        /// 这些内容原本只在调试模式下才落盘，结果「用户那边到底怎么了」永远看不到 ——
        /// 而它们的量级本身很小（一次事件一行），真正的刷屏点已全部改走 DebugThrottled。
        /// </summary>
        public static void Debug(string msg) => Write("DEBUG", msg, null);

        /// <summary>
        /// 高频重复消息专用：同一条消息（按未格式化前的模板比对）在 ThrottleWindow 内只写第一行，
        /// 窗口结束时补一行「期间重复 N 次」。
        ///
        /// 用在「按秒重试」这类路径上（音频捕获重试、WebSocket 重连、材质窗重建）：既保留
        /// 「什么时候开始不行的」这一关键信息，又不会把日志刷成几千行。
        /// 模板必须是常量：动态消息（含歌名 / 路径 / 错误消息）各写各的，无法归并。
        /// </summary>
        public static void DebugThrottled(string template) => Write("DEBUG", template, template);

        public static void Info(string msg) => Write("INFO", msg, null);

        public static void Warn(string msg) => Write("WARN", msg, null);

        public static void Error(string msg, Exception? ex = null)
            // ex.ToString() 自带完整调用栈与 InnerException 链 —— 只记 Type+Message 的话，
            // 未观察的 Task 异常（外层恒为 AggregateException）会把真正的异常信息全部吞掉，事后无从定位。
            => Write("ERROR", ex == null ? msg : $"{msg} | {ex}", null);

        /// <param name="msg">最终写进日志的文本。</param>
        /// <param name="throttleKey">
        /// 非 null 时启用去重，并按这个 key 归并（传模板而不是完整消息）；null = 原样写。
        /// </param>
        private static void Write(string level, string msg, string? throttleKey)
        {
            try
            {
                lock (_lock)
                {
                    if (throttleKey != null && IsThrottled(level, throttleKey)) return;

                    _writer ??= OpenWriter();
                    if (_writer == null) return;

                    // 去重窗口跨过时，先把上一段被折叠掉的条数补一行，避免"少了多少条"无从得知
                    FlushThrottleSummary();

                    string line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [{level}] {msg}{Environment.NewLine}";
                    _writer.Write(line);
                    _bytesWritten += line.Length + System.Text.Encoding.UTF8.GetByteCount(msg) - msg.Length;

                    if (_bytesWritten >= MaxBytes) Rotate();
                }
            }
            catch
            {
                // 日志绝不能让主流程挂掉：任何异常后丢弃写入器，下次写入重建
                try { _writer = null; } catch { }
            }
        }

        /// <summary>同一条限流消息是否该被折叠掉（是则计数并返回 true）。</summary>
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

        /// <summary>把上一段被折叠掉的重复条数补写一行（没有折叠过就什么都不做）。</summary>
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

        /// <summary>
        /// 轮转：先关掉写入器（Windows 下文件被占用就改不了名），再把当前文件改名成带时间戳的备份，
        /// 顺手删掉超出 MaxRotatedFiles 的老备份。跨进程用命名互斥体串行化，拿不到锁就跳过这一轮
        ///（下次写入还会再触发，不会漏）。
        /// </summary>
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
                // 轮转失败不影响写日志：接着往原文件追加即可（大不了它再大一点）
            }
            finally
            {
                try { mutex?.ReleaseMutex(); } catch { }
                try { mutex?.Dispose(); } catch { }
            }
        }

        /// <summary>只保留最近 MaxRotatedFiles 份历史日志，更老的删掉。</summary>
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
