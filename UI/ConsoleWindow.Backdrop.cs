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

        private bool _backdropRebuilding;

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
            if (region == IntPtr.Zero)
                return;

            if (Win32.SetWindowRgn(hwnd, region, true) == 0)
                Win32.DeleteObject(region);
        }

        private void SyncBackdropToContent()
        {
            if (_backdropHwnd == IntPtr.Zero || _hwnd == IntPtr.Zero)
                return;

            if (!Win32.GetWindowRect(_hwnd, out var rect))
                return;

            int width = rect.Right - rect.Left;
            int height = rect.Bottom - rect.Top;
            // 拖动时就只剩一块亚克力空板把内容盖住。
            _ = Win32.SetWindowPos(_backdropHwnd, _hwnd, rect.Left, rect.Top, width, height, Win32.SWP_NOACTIVATE);
        }

        // 材质窗只能「藏」，不能「最小化」。

        private void HideBackdrop()
        {
            if (_backdropHwnd == IntPtr.Zero) return;
            Win32.ShowWindow(_backdropHwnd, Win32.SW_HIDE);
        }

        private void ShowBackdrop()
        {
            if (_backdropHwnd == IntPtr.Zero || _hwnd == IntPtr.Zero) return;
            Win32.ShowWindow(_backdropHwnd, Win32.SW_SHOWNOACTIVATE);
            SyncBackdropToContent();
        }

        private void MinimizeToTaskbar()
        {
            if (_hwnd == IntPtr.Zero) return;
            HideBackdrop();
            Win32.ShowWindow(_hwnd, Win32.SW_MINIMIZE);
        }

        private void ApplyAppearance()
        {
            Renderer.InvalidateSystemThemeCache();
            _isLightAppearance = IsSystemLightAppearance();

            _fgColor = Neutral(255);   // 深色 = 白，浅色 = 纯黑

            _uiTextPaint.Color = _fgColor;
            _subTextPaint.Color = Neutral(170);
            _titleTextPaint.Color = Neutral(200);
            _iconPaint.Color = _fgColor;
            _chevronPaint.Color = Neutral(150);

            _tabBgSelected.Color = Overlay(15);
            _separatorPaint.Color = Overlay(20);
            _hoverMinPaint.Color = Overlay(20);

            _sortArrowStroke.Color = Neutral(210);
            _sortArrowHoverStroke.Color = _fgColor;
            _sortArrowDisabledStroke.Color = Neutral(130);
        }

        private void ApplyBackdropPalette()
        {
            bool light = _isLightAppearance;

            _menuBg.Color = light ? new SKColor(252, 252, 252) : new SKColor(48, 48, 48);
            _menuBorder.Color = light ? new SKColor(200, 200, 200) : new SKColor(88, 88, 88);

            if (_backdropMode == BackdropMaterialMode.SolidDark)
            {
                _bgPaint.Color = light ? new SKColor(243, 243, 243) : new SKColor(32, 32, 32);
                _titleBarPaint.Color = light ? new SKColor(235, 235, 235) : new SKColor(40, 40, 40);
                _cardBg.Color = Overlay(8);
                _cardBorder.Color = Overlay(15);
                _globalBorderPaint.Color = light ? new SKColor(205, 205, 205) : new SKColor(60, 60, 60);
                return;
            }

            bool mica = _backdropMode == BackdropMaterialMode.Mica;
            _bgPaint.Color = light
                ? (mica ? new SKColor(248, 248, 248, 164) : new SKColor(245, 245, 245, 112))
                : (mica ? new SKColor(22, 22, 22, 164) : new SKColor(18, 18, 18, 112));
            _titleBarPaint.Color = SKColors.Transparent;
            _cardBg.Color = Overlay(mica ? (byte)18 : (byte)22);
            _cardBorder.Color = Overlay(mica ? (byte)30 : (byte)38);
            _globalBorderPaint.Color = Overlay(mica ? (byte)34 : (byte)40);
        }

        private void TryEnableBackdropMaterial()
        {
            TrySetDarkMode();
            TryExtendFrameIntoClientArea();
            TrySetRoundedCornerPreference();

            // 用户这次要先看“有没有真实背景模糊”。
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
            int enabled = _isLightAppearance ? 0 : 1;
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

        private static uint AcrylicTint => _isLightAppearance
            ? ColorToAbgr(0x8C, 0xF2, 0xF2, 0xF2)   // 0x8CF2F2F2
            : ColorToAbgr(0x8C, 0x14, 0x14, 0x14);  // 0x8C141414

        private bool TryEnableAcrylicBackdrop()
            => ApplyAccentPolicy(Win32.ACCENT_ENABLE_ACRYLICBLURBEHIND, AcrylicTint);

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

        // 换肤/重贴时再调反而会把材质弄没。

        private void ReapplyBackdropMaterial()
        {
            if (_backdropHwnd == IntPtr.Zero)
                return;

            switch (_backdropMode)
            {
                case BackdropMaterialMode.Acrylic:
                    Logger.Debug($"重贴亚克力材质: {(ApplyAccentPolicy(Win32.ACCENT_ENABLE_ACRYLICBLURBEHIND, AcrylicTint) ? "ok" : "fail")}");
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

        private void RebuildBackdropWindow()
        {
            if (_backdropRebuilding || _hwnd == IntPtr.Zero)
                return;

            if (!Win32.GetWindowRect(_hwnd, out var rect))
                return;

            _backdropRebuilding = true;
            try
            {
                if (_backdropHwnd != IntPtr.Zero)
                {
                    IntPtr old = _backdropHwnd;
                    _backdropHwnd = IntPtr.Zero;
                    Win32.DestroyWindow(old);
                }

                IntPtr hInstance = System.Diagnostics.Process.GetCurrentProcess().MainModule?.BaseAddress ?? IntPtr.Zero;
                _backdropHwnd = Win32.CreateWindowEx(
                    Win32.WS_EX_TOOLWINDOW | Win32.WS_EX_NOACTIVATE,
                    "NotchConsoleClass", string.Empty,
                    Win32.WS_POPUP | Win32.WS_VISIBLE,
                    rect.Left, rect.Top,
                    rect.Right - rect.Left, rect.Bottom - rect.Top,
                    IntPtr.Zero, IntPtr.Zero, hInstance, IntPtr.Zero);

                if (_backdropHwnd == IntPtr.Zero)
                {
                    // 重建不出来就退回自绘实色外观，至少不是一块透明的洞。
                    _backdropMode = BackdropMaterialMode.SolidDark;
                    ApplyBackdropPalette();
                    Render();
                    Logger.Error("重建材质窗失败：CreateWindowEx 返回 0");
                    return;
                }

                ApplyRoundedRegion(_backdropHwnd);
                _backdropMode = BackdropMaterialMode.SolidDark;
                ApplyAppearance();
                TryEnableBackdropMaterial();
                ApplyBackdropPalette();
                SyncBackdropToContent();
                Render();

                Logger.Debug($"重建材质窗完成，材质模式 = {_backdropMode}");
            }
            finally
            {
                _backdropRebuilding = false;
            }
        }

        private void RepairBackdropAfterActivate()
        {
            if (_backdropHwnd == IntPtr.Zero)
                return;

            if (_backdropMode == BackdropMaterialMode.Acrylic && !OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000))
            {
                Logger.Debug("Win10 亚克力激活修复：重建材质窗");
                RebuildBackdropWindow();
                return;
            }

            ReapplyBackdropMaterial();
        }

        private static uint ColorToAbgr(byte a, byte r, byte g, byte b)
        {
            return ((uint)a << 24) | ((uint)b << 16) | ((uint)g << 8) | r;
        }
    }
}
