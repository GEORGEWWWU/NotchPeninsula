using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace NotchPeninsula
{
    public static class Logger
    {
        private static readonly object _lock = new object();
        private static readonly string LogPath = ResolveLogPath();

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

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetCurrentPackageFullName(ref uint packageFullNameLength, System.Text.StringBuilder? packageFullName);
        public static void Debug(string msg)
        {
            if (Program._isDebugMode) Write("DEBUG", msg);
        }
public static void Info(string msg) => Write("INFO", msg);
public static void Warn(string msg) => Write("WARN", msg);
public static void Error(string msg, Exception? ex = null) => Write("ERROR", ex == null ? msg : $"{msg} | {ex.GetType().Name}: {ex.Message}");

        private static void Write(string level, string msg)
        {
            try
            {
                lock (_lock)
                {
                    var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [{level}] {msg}{Environment.NewLine}";
                    File.AppendAllText(LogPath, line);
                }
            }
            catch {  }
        }
    }
}