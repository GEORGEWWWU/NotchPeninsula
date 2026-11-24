using System.Runtime.InteropServices;
using SkiaSharp;

namespace NotchPeninsula
{
    public partial class ConsoleWindow
    {
        private IntPtr _backdropHwnd;
        private NativeAcrylic? _nativeConsoleBackdrop;
        // 持有 WinRT 控制器，确保设置窗口的 Composition 调度队列保持有效。
        private static Windows.System.DispatcherQueueController? _consoleBackdropDispatcher;
        private bool _nativeConsoleBackdropFailed;
        private bool? _consoleAdvancedEffectsEnabled;

        private bool _backdropRebuilding;

        // 系统拖动 / 缩放循环中（WM_ENTERSIZEMOVE ~ WM_EXITSIZEMOVE）。
        private bool _dragMoving;

        private static readonly IntPtr BACKDROP_REFRESH_TIMER_ID = new IntPtr(0x4E50); // "NP"

        private enum BackdropMaterialMode
        {
            Solid,
            Acrylic
        }

        private BackdropMaterialMode _backdropMode = BackdropMaterialMode.Solid;

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

        private void SyncBackdropToContent(bool keepZOrder = false)
        {
            if (NativeAcrylic.IsSupported)
            {
                SyncNativeConsoleBackdrop();
                return;
            }
            if (_backdropHwnd == IntPtr.Zero || _hwnd == IntPtr.Zero)
                return;

            if (!Win32.GetWindowRect(_hwnd, out var rect))
                return;

            int width = rect.Right - rect.Left;
            int height = rect.Bottom - rect.Top;
            // 拖动中不再重申 Z 序：每次重排都要 DWM 重新合成一遍，
            // 亚克力窗会有一帧「重新贴材质」的空档，看起来就是窗口中间劈开、露出下面。
            uint flags = Win32.SWP_NOACTIVATE | (keepZOrder ? Win32.SWP_NOZORDER : 0u);
            _ = Win32.SetWindowPos(_backdropHwnd, keepZOrder ? IntPtr.Zero : _hwnd,
                rect.Left, rect.Top, width, height, flags);
        }

        private void SyncNativeConsoleBackdrop()
        {
            if (_hwnd == IntPtr.Zero || !Win32.IsWindowVisible(_hwnd) || Win32.IsIconic(_hwnd)) return;
            if (!Win32.GetWindowRect(_hwnd, out var rect)) return;
            int width = rect.Right - rect.Left;
            int height = rect.Bottom - rect.Top;
            if (width <= 0 || height <= 0) return;

            var previousMode = _backdropMode;
            if (!_nativeConsoleBackdropFailed)
            {
                try
                {
                    _consoleAdvancedEffectsEnabled ??= !System.Windows.SystemParameters.HighContrast
                        && new Windows.UI.ViewManagement.UISettings().AdvancedEffectsEnabled;
                    if (!_consoleAdvancedEffectsEnabled.Value)
                    {
                        _nativeConsoleBackdrop?.Hide();
                        _backdropMode = BackdropMaterialMode.Solid;
                    }
                    else
                    {
                        if (_nativeConsoleBackdrop == null)
                        {
                            if (Windows.System.DispatcherQueue.GetForCurrentThread() == null)
                                _consoleBackdropDispatcher = NativeAcrylic.CreateDispatcher();
                            _nativeConsoleBackdrop = new NativeAcrylic(_hwnd);
                        }

                        float radius = 8f * _dpiScale;
                        _nativeConsoleBackdrop.Present(_hwnd, rect.Left, rect.Top, width, height,
                            default, new System.Numerics.Vector2(width, height), new IslandOutline(radius, radius, radius),
                            capsule: true, opacity: 1f);
                        _backdropMode = BackdropMaterialMode.Acrylic;
                    }
                }
                catch (Exception ex)
                {
                    _nativeConsoleBackdropFailed = true;
                    _backdropMode = BackdropMaterialMode.Solid;
                    Logger.Error("[设置窗口] Windows Composition 背板失败，使用实色背景", ex);
                    DisposeNativeConsoleBackdrop();
                }
            }

            if (previousMode != _backdropMode)
            {
                ApplyBackdropPalette();
                Render();
            }
        }

        private void DisposeNativeConsoleBackdrop()
        {
            var backdrop = _nativeConsoleBackdrop;
            _nativeConsoleBackdrop = null;
            try { backdrop?.Dispose(); }
            catch (Exception ex) { Logger.Error("[设置窗口] 释放原生背板失败", ex); }
        }

        // 拖动 / 缩放期间：材质窗继续跟随，但不做任何视觉切换（不藏、不实心化、不重绘）。
        // 拖动循环里插一次 UpdateLayeredWindow 就是撕裂的根 —— 位图上传和窗口移动是两件事，
        // 赶在一起就会看到半张窗口是空的。所以拖动期间只有位置同步这一件事。
        private void BeginDragMove()
        {
            _dragMoving = true;
        }

        private void EndDragMove()
        {
            if (!_dragMoving) return;
            _dragMoving = false;
            SyncBackdropToContent();   // 收尾对齐一次（带上 Z 序）
            Render();                  // 拖动期间被挡下的重绘在这里补一帧（窗口已停，不会撕裂）
        }

        // 材质窗只能「藏」，不能「最小化」。

        private void HideBackdrop()
        {
            _nativeConsoleBackdrop?.Hide();
            if (_backdropHwnd == IntPtr.Zero) return;
            Win32.ShowWindow(_backdropHwnd, Win32.SW_HIDE);
        }

        private void ShowBackdrop()
        {
            if (_hwnd == IntPtr.Zero) return;
            if (_backdropHwnd != IntPtr.Zero) Win32.ShowWindow(_backdropHwnd, Win32.SW_SHOWNOACTIVATE);
            SyncBackdropToContent();
        }

        private void MinimizeToTaskbar()
        {
            if (_hwnd == IntPtr.Zero) return;
            HideBackdrop();
            Win32.ShowWindow(_hwnd, Win32.SW_MINIMIZE);
        }

        private void ApplyAppearance(bool light)
        {
            _isLightAppearance = light;
            _fgColor = Neutral(255);
            _uiTextPaint.Color = _fgColor;
            _subTextPaint.Color = SecondaryText;
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
            bool acrylic = _backdropMode == BackdropMaterialMode.Acrylic;
            if (!acrylic)
                _bgPaint.Color = light ? new SKColor(243, 243, 243) : new SKColor(32, 32, 32);
            else if (_nativeConsoleBackdrop != null)
                _bgPaint.Color = light ? new SKColor(244, 244, 244, 191) : new SKColor(19, 19, 19, 191);
            else
                _bgPaint.Color = light ? new SKColor(245, 245, 245, 112) : new SKColor(18, 18, 18, 112);
            _cardBg.Color = light
                ? new SKColor(255, 255, 255, acrylic ? (byte)194 : (byte)255)
                : new SKColor(48, 48, 51, acrylic ? (byte)176 : (byte)255);
            _globalBorderPaint.Color = acrylic ? Overlay(40)
                : light ? new SKColor(205, 205, 205) : new SKColor(60, 60, 60);
        }

        private void TryEnableBackdropMaterial()
        {
            ApplyWindowTheme(_backdropHwnd);
            TryExtendFrameIntoClientArea();
            TrySetRoundedCornerPreference();

            // 用户这次要先看“有没有真实背景模糊”。
            if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763) && TryEnableAcrylicBackdrop())
            {
                _backdropMode = BackdropMaterialMode.Acrylic;
                return;
            }

            _backdropMode = BackdropMaterialMode.Solid;
        }

        private static void ApplyWindowTheme(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return;
            int enabled = _isLightAppearance ? 0 : 1;
            int size = sizeof(int);
            _ = Win32.DwmSetWindowAttribute(hwnd, Win32.DWMWA_USE_IMMERSIVE_DARK_MODE, ref enabled, size);
            _ = Win32.DwmSetWindowAttribute(hwnd, Win32.DWMWA_USE_IMMERSIVE_DARK_MODE_BEFORE_20H1, ref enabled, size);
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

        private static uint AcrylicTint => _isLightAppearance ? 0x8CF2F2F2u : 0x8C141414u;

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
            // 拖动中重贴材质等于把亚克力板拆了重建：闪一帧是很明显的「窗口中间空了一块」。
            // 材质属性此刻没变，跳过不损失；下次激活会照常补。
            if (_backdropHwnd == IntPtr.Zero || _dragMoving)
                return;

            ApplyWindowTheme(_backdropHwnd);
            if (_backdropMode == BackdropMaterialMode.Acrylic && !TryEnableAcrylicBackdrop())
            {
                Logger.Warn("[设置窗口] 恢复亚克力失败，重新选择可用背板");
                TryEnableBackdropMaterial();
                ApplyBackdropPalette();
                Render();
            }
        }

        private void RebuildBackdropWindow()
        {
            // 销毁重建材质窗在拖动中做，等于当着用户的面把背景铲掉重刷。
            if (_backdropRebuilding || _hwnd == IntPtr.Zero || _dragMoving)
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
                    _backdropMode = BackdropMaterialMode.Solid;
                    ApplyBackdropPalette();
                    Render();
                    Logger.Error("重建材质窗失败：CreateWindowEx 返回 0");
                    return;
                }

                ApplyRoundedRegion(_backdropHwnd);
                _backdropMode = BackdropMaterialMode.Solid;
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

    }
}
