using System.Drawing;
using System.Reflection;

namespace NotchPeninsula
{
    /// <summary>
    /// 应用身份：**窗口标题**与**窗口图标**的唯一来源。
    ///
    /// 标题带版本号（与设置中心标题栏那一串完全一致），图标取 exe 内嵌的 NPS logo。
    /// </summary>
    internal static class AppIdentity
    {
        /// <summary>基础名（无版本号）。图标资源、注册表、目录名都用它。</summary>
        public const string BaseName = "NotchPeninsula";

        private static string? _title;
        private static Icon? _icon;
        private static bool _iconTried;

        /// <summary>窗口标题：<c>NotchPeninsula 2.3.2</c>（读不到版本号时退化成基础名）。</summary>
        public static string WindowTitle => _title ??= BuildTitle();

        /// <summary>
        /// 窗口 / 任务栏图标句柄，来自 exe 的图标资源（即 NPS logo）。
        /// ⚠️ <see cref="Icon"/> 对象必须一直被静态字段持有：句柄跟着对象走，对象被回收图标就变白板。
        /// </summary>
        public static IntPtr IconHandle
        {
            get
            {
                EnsureIcon();
                return _icon?.Handle ?? IntPtr.Zero;
            }
        }

        private static string BuildTitle()
        {
            var version = Assembly.GetExecutingAssembly().GetName().Version;
            return version == null
                ? BaseName
                : $"{BaseName} {version.Major}.{version.Minor}.{version.Build}";
        }

        private static void EnsureIcon()
        {
            if (_iconTried) return;
            _iconTried = true;
            try
            {
                string? path = Environment.ProcessPath;
                if (!string.IsNullOrEmpty(path)) _icon = Icon.ExtractAssociatedIcon(path);
            }
            catch (Exception ex)
            {
                // 取不到图标不该影响窗口能不能开：系统会退回默认图标。
                Logger.Warn($"[图标] 提取应用图标失败：{ex.Message}");
            }
        }
    }
}
