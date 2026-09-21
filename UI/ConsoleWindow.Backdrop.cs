using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using SkiaSharp;
using System.Diagnostics;
using NotchPeninsula.Plugins;

namespace NotchPeninsula
{
    public partial class ConsoleWindow
    {
        private IntPtr _backdropHwnd;

        // 「显示 / 激活之后延迟补一次材质」用的一次性定时器 id（只在本窗口内用，取个不会撞号的值）

        private static readonly IntPtr BACKDROP_REFRESH_TIMER_ID = new IntPtr(0x4E50); // "NP"

        private enum BackdropMaterialMode
        {
            SolidDark,
            Acrylic,
            Mica
        }

        private BackdropMaterialMode _backdropMode = BackdropMaterialMode.SolidDark;

        private void ApplyRoundedRegion(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero)
                return;

            int radius = Math.Max(12, (int)MathF.Round(8f * _dpiScale * 2f));
            IntPtr region = Win32.CreateRoundRectRgn(0, 0, _scaledWidth + 1, _scaledHeight + 1, radius, radius);
            if (region != IntPtr.Zero)
            {
                _ = Win32.SetWindowRgn(hwnd, region, true);
            }
        }

        private void SyncBackdropToContent()
        {
            if (_backdropHwnd == IntPtr.Zero || _hwnd == IntPtr.Zero)
                return;

            if (!Win32.GetWindowRect(_hwnd, out var rect))
                return;

            int width = rect.Right - rect.Left;
            int height = rect.Bottom - rect.Top;
            // 背景窗必须永远压在内容窗后面；之前传 IntPtr.Zero 会把 backdrop 提到 Z 序顶部，
            // 拖动时就只剩一块亚克力空板把内容盖住。
            _ = Win32.SetWindowPos(_backdropHwnd, _hwnd, rect.Left, rect.Top, width, height, Win32.SWP_NOACTIVATE);
        }

        // 材质窗只能「藏」，不能「最小化」。
        // 它是个无标题 WS_POPUP：走 SW_MINIMIZE 会被 Windows 当成普通窗口最小化，
        // 于是在桌面左下角任务栏之上留一条小标题条；还原之后这条标题条还会糊在窗口左上角。

        private void HideBackdrop()
        {
            if (_backdropHwnd == IntPtr.Zero) return;
            Win32.ShowWindow(_backdropHwnd, Win32.SW_HIDE);
        }

        // 还原 / 唤醒时把材质窗重新亮出来，并压回内容窗正下方（不能激活，否则会抢焦点）

        private void ShowBackdrop()
        {
            if (_backdropHwnd == IntPtr.Zero || _hwnd == IntPtr.Zero) return;
            Win32.ShowWindow(_backdropHwnd, Win32.SW_SHOWNOACTIVATE);
            SyncBackdropToContent();
        }

        // 最小化 = 藏材质窗 + 内容窗进任务栏（内容窗是 WS_EX_APPWINDOW，所以有任务栏按钮）

        private void MinimizeToTaskbar()
        {
            if (_hwnd == IntPtr.Zero) return;
            HideBackdrop();
            Win32.ShowWindow(_hwnd, Win32.SW_MINIMIZE);
        }

        private void ApplyBackdropPalette()
        {
            if (_backdropMode == BackdropMaterialMode.SolidDark)
            {
                _bgPaint.Color = new SKColor(32, 32, 32);
                _titleBarPaint.Color = new SKColor(40, 40, 40);
                _cardBg.Color = new SKColor(255, 255, 255, 8);
                _cardBorder.Color = new SKColor(255, 255, 255, 15);
                _menuBg.Color = new SKColor(40, 40, 40);
                _menuBorder.Color = new SKColor(80, 80, 80);
                _globalBorderPaint.Color = new SKColor(60, 60, 60);
                return;
            }

            bool mica = _backdropMode == BackdropMaterialMode.Mica;
            _bgPaint.Color = mica ? new SKColor(22, 22, 22, 164) : new SKColor(18, 18, 18, 112);
            // 标题栏不再单独盖一层深色底，否则顶栏会像“第二块面板”把亚克力吃掉。
            _titleBarPaint.Color = SKColors.Transparent;
            _cardBg.Color = new SKColor(255, 255, 255, mica ? (byte)18 : (byte)22);
            _cardBorder.Color = new SKColor(255, 255, 255, mica ? (byte)30 : (byte)38);
            _menuBg.Color = mica ? new SKColor(26, 26, 26, 210) : new SKColor(22, 22, 22, 172);
            _menuBorder.Color = new SKColor(255, 255, 255, mica ? (byte)28 : (byte)34);
            _globalBorderPaint.Color = new SKColor(255, 255, 255, mica ? (byte)34 : (byte)40);
        }

        private void TryEnableBackdropMaterial()
        {
            TrySetDarkMode();
            TryExtendFrameIntoClientArea();
            TrySetRoundedCornerPreference();

            // 用户这次要先看“有没有真实背景模糊”。
            // Mica 在很多 Win11 机器上更像带噪点的深色底，不像明显模糊；
            // 所以设置窗口实验优先尝试 Acrylic，失败再回退到 Win11 的 Mica。
            if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763) && TryEnableAcrylicBackdrop())
            {
                _backdropMode = BackdropMaterialMode.Acrylic;
                return;
            }

            if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22621) && TryEnableSystemBackdropMica())
            {
                _backdropMode = BackdropMaterialMode.Mica;
                return;
            }

            if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000) && TryEnableLegacyMica())
            {
                _backdropMode = BackdropMaterialMode.Mica;
                return;
            }

            _backdropMode = BackdropMaterialMode.SolidDark;
        }

        private void TrySetDarkMode()
        {
            int enabled = 1;
            int size = Marshal.SizeOf<int>();
            if (_backdropHwnd == IntPtr.Zero) return;
            _ = Win32.DwmSetWindowAttribute(_backdropHwnd, Win32.DWMWA_USE_IMMERSIVE_DARK_MODE, ref enabled, size);
            _ = Win32.DwmSetWindowAttribute(_backdropHwnd, Win32.DWMWA_USE_IMMERSIVE_DARK_MODE_BEFORE_20H1, ref enabled, size);
        }

        private void TrySetRoundedCornerPreference()
        {
            if (_backdropHwnd == IntPtr.Zero) return;
            int rounded = Win32.DWMWCP_ROUND;
            _ = Win32.DwmSetWindowAttribute(_backdropHwnd, Win32.DWMWA_WINDOW_CORNER_PREFERENCE, ref rounded, Marshal.SizeOf<int>());
        }

        private void TryExtendFrameIntoClientArea()
        {
            if (_backdropHwnd == IntPtr.Zero) return;
            var margins = new Win32.MARGINS { cxLeftWidth = -1, cxRightWidth = -1, cyTopHeight = -1, cyBottomHeight = -1 };
            _ = Win32.DwmExtendFrameIntoClientArea(_backdropHwnd, ref margins);
        }

        private bool TryEnableSystemBackdropMica()
        {
            if (_backdropHwnd == IntPtr.Zero) return false;
            int backdrop = Win32.DWMSBT_MAINWINDOW;
            return Win32.DwmSetWindowAttribute(_backdropHwnd, Win32.DWMWA_SYSTEMBACKDROP_TYPE, ref backdrop, Marshal.SizeOf<int>()) == 0;
        }

        private bool TryEnableLegacyMica()
        {
            if (_backdropHwnd == IntPtr.Zero) return false;
            int enabled = 1;
            return Win32.DwmSetWindowAttribute(_backdropHwnd, Win32.DWMWA_MICA_EFFECT, ref enabled, Marshal.SizeOf<int>()) == 0;
        }

        // 亚克力的 tint（ABGR）：alpha 不能为 0，否则只剩模糊没有底色。
        // 这个 tint 故意比上一版更浅：上一版 alpha 太高，视觉更像“深色底板”而不是背景模糊。

        private static readonly uint ACRYLIC_TINT = ColorToAbgr(0x8C, 0x14, 0x14, 0x14); // 0x8C141414

        private bool TryEnableAcrylicBackdrop()
            => ApplyAccentPolicy(Win32.ACCENT_ENABLE_ACRYLICBLURBEHIND, ACRYLIC_TINT);

        private bool ApplyAccentPolicy(int accentState, uint gradientColor)
        {
            if (_backdropHwnd == IntPtr.Zero) return false;

            var accent = new Win32.ACCENT_POLICY
            {
                AccentState = accentState,
                AccentFlags = 0,
                GradientColor = gradientColor,
                AnimationId = 0
            };

            IntPtr accentPtr = Marshal.AllocHGlobal(Marshal.SizeOf<Win32.ACCENT_POLICY>());
            try
            {
                Marshal.StructureToPtr(accent, accentPtr, false);
                var data = new Win32.WINDOWCOMPOSITIONATTRIBDATA
                {
                    Attribute = Win32.WCA_ACCENT_POLICY,
                    Data = accentPtr,
                    SizeOfData = Marshal.SizeOf<Win32.ACCENT_POLICY>()
                };
                return Win32.SetWindowCompositionAttribute(_backdropHwnd, ref data) != 0;
            }
            catch
            {
                return false;
            }
            finally
            {
                Marshal.FreeHGlobal(accentPtr);
            }
        }

        // ⚠️ 唤醒 / 重新激活时「重新贴一次材质」，这是本窗口最容易被忽略的一步。
        //
        // 症状：右键岛体打开设置窗口时亚克力正常；点别的窗口让设置窗口失焦，再把设置窗口
        //       唤到前台，背景直接变成全透明（前景还在，但背后能看穿到桌面），亚克力没了。
        //
        // 根因：Acrylic 是靠 SetWindowCompositionAttribute 把「accent 策略」挂在材质窗上的
        //       一次性状态，而不是窗口样式。窗口每经历一次 show / activate（点任务栏、
        //       Alt+Tab、从最小化还原、SetForegroundWindow 重新拉前台），DWM 都会重新
        //       初始化这扇窗口的合成，把之前贴的 accent 冲掉；而 DwmExtendFrameIntoClientArea
        //       留下的「整块客户区即玻璃」还在，于是材质窗就成了一块纯透明的洞 —— 正是看到的现象。
        //       微软文档给出的稳定时序本来就是「GlassFrame → Show → Activate → 贴 accent」，
        //       accent 排在最后正是因为前面的步骤会覆盖它。
        //
        // 所以：任何一次重新显示 / 重新激活之后，都必须补贴一次材质。
        // 注意这里只重贴材质层，不重调 DwmExtendFrameIntoClientArea —— 玻璃框属于窗口初始化，
        // 换肤/重贴时再调反而会把材质弄没。

        private void ReapplyBackdropMaterial()
        {
            if (_backdropHwnd == IntPtr.Zero)
                return;

            switch (_backdropMode)
            {
                case BackdropMaterialMode.Acrylic:
                    Logger.Debug($"重贴亚克力材质: {(ApplyAccentPolicy(Win32.ACCENT_ENABLE_ACRYLICBLURBEHIND, ACRYLIC_TINT) ? "ok" : "fail")}");
                    break;

                case BackdropMaterialMode.Mica:
                    if (!TryEnableSystemBackdropMica())
                        TryEnableLegacyMica();
                    break;

                // SolidDark 是自绘的实色外观，没有系统材质可补
                default:
                    break;
            }
        }

        private static uint ColorToAbgr(byte a, byte r, byte g, byte b)
        {
            return ((uint)a << 24) | ((uint)b << 16) | ((uint)g << 8) | r;
        }
    }
}
