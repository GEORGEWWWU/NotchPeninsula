using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;
using NotchPeninsula.Plugins;

namespace NotchPeninsula
{
    /// <summary>
    /// 崩溃留档与诊断包导出。
    ///
    /// <para>
    /// 崩溃单独落盘、<b>不进 Logger 的轮转队列</b>：app.log 是 1MB × 2 份，
    /// 一次崩溃风暴（同一异常反复抛）足够把它连冲两轮，用户事后能拿到的只剩一条摘要，现场就没了。
    /// 崩一次写一个文件，谁也冲不掉谁。
    /// </para>
    ///
    /// <para>
    /// 诊断包是「用户报 bug」的最低成本通道：版本 / 系统 / 显示器拓扑 / 配置快照 / 插件清单 / 最近日志
    /// 打成一个 zip，用户点一下就生成，不用再一项项追问。**只打宿主自己的配置键，不碰用户其他数据。**
    /// </para>
    /// </summary>
    internal static class Diagnostics
    {
        private const int MaxCrashFiles = 20;

        // 崩溃文件末尾附最近若干行 app.log：异常是「果」，前因往往就在这几行里
        private const int CrashLogTailLines = 40;

        private static string CrashDir
        {
            get
            {
                try { Directory.CreateDirectory(Path.Combine(Logger.LogDirectory, "crash")); } catch { }
                return Path.Combine(Logger.LogDirectory, "crash");
            }
        }

        // ---- 崩溃留档 ----

        /// <summary>写一份独立崩溃文件。任何异常都在内部吞掉 —— 崩溃路径再抛就是二次崩溃。</summary>
        public static void WriteCrash(string context, Exception? ex, bool terminating)
        {
            try
            {
                string dir = CrashDir;
                // 带毫秒：风暴里同一秒可能来好几条，只到秒会互相覆盖
                string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss-fff");
                string path = Path.Combine(dir, $"crash-{stamp}.log");
                File.WriteAllText(path, BuildCrashText(context, ex, terminating), new UTF8Encoding(false));
                PruneCrashFiles(dir);
            }
            catch
            {
            }
        }

        private static string BuildCrashText(string context, Exception? ex, bool terminating)
        {
            var sb = new StringBuilder(2048);
            sb.AppendLine("===== NotchPeninsula 崩溃报告 =====");
            sb.AppendLine($"时间    : {DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}");
            sb.AppendLine($"致命    : {(terminating ? "是（进程即将退出）" : "否（仅记录，进程继续）")}");
            sb.AppendLine($"上下文  : {context}");
            sb.AppendLine(DescribeApp());
            sb.AppendLine();
            sb.AppendLine("----- 异常 -----");
            sb.AppendLine(ex?.ToString() ?? "(抛出对象不是 Exception)");

            try
            {
                sb.AppendLine();
                sb.AppendLine("----- 显示器 -----");
                sb.Append(BuildMonitors());
            }
            catch { }

            try
            {
                var tail = ReadLogTail(CrashLogTailLines);
                if (tail.Length > 0)
                {
                    sb.AppendLine();
                    sb.AppendLine($"----- 崩溃前 app.log 末 {CrashLogTailLines} 行 -----");
                    sb.AppendLine(tail);
                }
            }
            catch { }

            return sb.ToString();
        }

        private static void PruneCrashFiles(string dir)
        {
            try
            {
                var files = new DirectoryInfo(dir).GetFiles("crash-*.log");
                if (files.Length <= MaxCrashFiles) return;

                Array.Sort(files, (a, b) => b.LastWriteTimeUtc.CompareTo(a.LastWriteTimeUtc));   // 新的在前
                for (int i = MaxCrashFiles; i < files.Length; i++)
                {
                    try { files[i].Delete(); } catch { }
                }
            }
            catch { }
        }

        /// <summary>读 app.log 末尾若干行。文件可能正被写，用 ReadWrite 共享打开，读不到就算了。</summary>
        private static string ReadLogTail(int lines)
        {
            try
            {
                string path = Logger.LogFilePath;
                if (!File.Exists(path)) return "";

                // 日志最大 1MB，整读比按块回退简单且不可能更慢；不占共享会挡住 Logger 自己的写入
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var sr = new StreamReader(fs, Encoding.UTF8);
                string all = sr.ReadToEnd();
                if (all.Length == 0) return "";

                var rows = all.Replace("\r\n", "\n").Split('\n');
                int start = Math.Max(0, rows.Length - lines - 1);
                return string.Join(Environment.NewLine, rows, start, rows.Length - start).TrimEnd();
            }
            catch
            {
                return "";
            }
        }

        // ---- 诊断包 ----

        /// <summary>生成诊断包并返回 zip 完整路径。失败时抛给调用方去决定怎么提示。</summary>
        public static string ExportBundle()
        {
            string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            string path = Path.Combine(ResolveExportDir(), $"NPS-diagnostics-{stamp}.zip");

            using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
            {
                AddText(zip, "info.txt", BuildInfo());
                AddText(zip, "monitors.txt", BuildMonitors());
                AddText(zip, "settings.txt", BuildSettings());
                AddText(zip, "plugins.txt", BuildPlugins());

                // 最近两份 app.log（当前 + 上一份轮转）
                foreach (string log in Logger.GetRecentLogFiles(2))
                    AddFile(zip, "logs/" + Path.GetFileName(log), log);

                // 最近三份崩溃
                foreach (string crash in GetRecentCrashFiles(3))
                    AddFile(zip, "logs/crash/" + Path.GetFileName(crash), crash);
            }

            return path;
        }

        private static string ResolveExportDir()
        {
            // 桌面最顺手：用户点完就能看到选中，直接拖进聊天窗口发出去
            try
            {
                string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
                if (!string.IsNullOrEmpty(desktop) && Directory.Exists(desktop)) return desktop;
            }
            catch { }

            try { Directory.CreateDirectory(Logger.LogDirectory); } catch { }
            return Logger.LogDirectory;
        }

        private static List<string> GetRecentCrashFiles(int max)
        {
            var list = new List<string>();
            try
            {
                var dir = new DirectoryInfo(Path.Combine(Logger.LogDirectory, "crash"));
                if (!dir.Exists) return list;

                var files = dir.GetFiles("crash-*.log");
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

        private static void AddText(ZipArchive zip, string entryName, string text)
        {
            try
            {
                var entry = zip.CreateEntry(entryName, CompressionLevel.Optimal);
                using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
                writer.Write(text);
            }
            catch
            {
                // 单项失败不连累整包：少一个文件也比导不出来强
            }
        }

        private static void AddFile(ZipArchive zip, string entryName, string sourcePath)
        {
            try
            {
                if (!File.Exists(sourcePath)) return;
                var entry = zip.CreateEntry(entryName, CompressionLevel.Optimal);
                using var dst = entry.Open();
                using var src = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                src.CopyTo(dst);
            }
            catch { }
        }

        // ---- 快照 ----

        private static string DescribeApp()
        {
            var sb = new StringBuilder();
            sb.AppendLine($"版本    : {typeof(Diagnostics).Assembly.GetName().Version?.ToString(3) ?? "未知"}");
            sb.AppendLine($"运行时  : {RuntimeInformation.FrameworkDescription}");
            sb.AppendLine($"系统    : {RuntimeInformation.OSDescription}");
            sb.AppendLine($"架构    : 进程 {RuntimeInformation.ProcessArchitecture} / 系统 {RuntimeInformation.OSArchitecture}");

            try
            {
                using var p = System.Diagnostics.Process.GetCurrentProcess();
                sb.AppendLine($"进程    : PID {p.Id}，工作集 {p.WorkingSet64 / 1024 / 1024} MB，"
                            + $"私有内存 {p.PrivateMemorySize64 / 1024 / 1024} MB");
                sb.AppendLine($"已运行  : {DateTime.Now - p.StartTime:hh\\:mm\\:ss}");
            }
            catch { }

            return sb.ToString().TrimEnd();
        }

        private static string BuildInfo()
        {
            var sb = new StringBuilder();
            sb.AppendLine("===== NotchPeninsula 诊断信息 =====");
            sb.AppendLine($"导出时间: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine(DescribeApp());
            sb.AppendLine();
            sb.AppendLine($"命令行  : {Environment.CommandLine}");

            try { sb.AppendLine($"应用内缩放: {Renderer.GLOBAL_DPI:F2}"); } catch { }
            try { sb.AppendLine($"主题模式: {Renderer.ThemeMode}，背景材质: {(Renderer.IslandAcrylic ? "亚克力" : "实体")}"); } catch { }
            try { sb.AppendLine($"目标显示器下标: {Renderer.TargetMonitorIndex}"); } catch { }
            try { sb.AppendLine($"岛体隐藏: {Renderer.IslandHidden}"); } catch { }

            return sb.ToString();
        }

        private static string BuildMonitors()
        {
            var sb = new StringBuilder();
            try
            {
                var screens = System.Windows.Forms.Screen.AllScreens;
                sb.AppendLine($"显示器: {screens.Length} 块（Screen.AllScreens，物理像素，未排序）");
                for (int i = 0; i < screens.Length; i++)
                {
                    var s = screens[i];
                    sb.AppendLine($"#{i} {s.DeviceName} 主屏={s.Primary} "
                                + $"bounds=({s.Bounds.X},{s.Bounds.Y},{s.Bounds.Width}x{s.Bounds.Height}) "
                                + $"work=({s.WorkingArea.X},{s.WorkingArea.Y},{s.WorkingArea.Width}x{s.WorkingArea.Height})");
                }
            }
            catch (Exception ex)
            {
                sb.AppendLine("枚举显示器失败: " + ex.Message);
            }
            return sb.ToString();
        }

        private static string BuildSettings()
        {
            var sb = new StringBuilder();
            sb.AppendLine(@"===== HKEY_CURRENT_USER\SOFTWARE\NotchPeninsula =====");
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\NotchPeninsula");
                if (key == null)
                {
                    sb.AppendLine("(键不存在)");
                    return sb.ToString();
                }

                foreach (string name in key.GetValueNames())
                {
                    string kind = key.GetValueKind(name).ToString();
                    object? value = key.GetValue(name);
                    sb.AppendLine($"{name} [{kind}] = {value}");
                }
            }
            catch (Exception ex)
            {
                sb.AppendLine("读取注册表失败: " + ex.Message);
            }
            return sb.ToString();
        }

        private static string BuildPlugins()
        {
            var sb = new StringBuilder();
            sb.AppendLine("===== 已安装插件 =====");
            try
            {
                var mgr = PluginManager.Instance;
                sb.AppendLine($"插件根目录: {mgr.PluginsRoot}");
                sb.AppendLine();

                var entries = mgr.Entries;
                sb.AppendLine($"共 {entries.Count} 个");
                foreach (var e in entries)
                {
                    sb.AppendLine($"- {e.Key}");
                    sb.AppendLine($"    名称: {e.DisplayName}，版本: {e.Version}，状态: {e.State}");
                    sb.AppendLine($"    形态: {(e.IsFolderLayout ? "目录" : "单文件")}，DLL: {e.DllPath}");
                    if (!string.IsNullOrEmpty(e.Error)) sb.AppendLine($"    错误: {e.Error}");
                }
            }
            catch (Exception ex)
            {
                sb.AppendLine("读取插件清单失败: " + ex.Message);
            }
            return sb.ToString();
        }
    }
}
