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

        // 重建材质窗时的重入保护：CreateWindowEx / ShowWindow 会同步派发 WM_SIZE、WM_SHOWWINDOW，
        // 这些消息又会走到「激活修复」分支，不挡一下就会无限递归重建。
        private bool _backdropRebuilding;

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
            if (region == IntPtr.Zero)
                return;

            // SetWindowRgn 只在成功时由窗口接管 region 的所有权；
            // 失败（返回 0）时所有权仍在调用方，必须自己 DeleteObject ——
            // 否则每开一次设置窗口就永久泄漏一块 GDI region（region 是受限的系统资源）。
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

        // 明暗外观的唯一入口：读系统「应用模式」，把前景 / 叠加层的基准色刷到共享画笔上。
        // 底色与材质相关的部分（_bgPaint / _menuBg / 边框…）随材质模式变化，收口在 ApplyBackdropPalette()，
        // 所以窗口创建 / 重建 / 系统主题变化时都是「先 ApplyAppearance()，再 ApplyBackdropPalette()」。
        //
        // 必须在 TryEnableBackdropMaterial() 之前调用：那里要按明暗决定
        //    DWMWA_USE_IMMERSIVE_DARK_MODE 与亚克力 tint。
        private void ApplyAppearance()
        {
            // 先作废系统主题缓存再读：本方法就是「系统主题变更」那条路径的入口
            //    （WM_SETTINGCHANGE → ApplyAppearance），不作废的话拿到的是旧值。
            Renderer.InvalidateSystemThemeCache();
            _isLightAppearance = IsSystemLightAppearance();

            _fgColor = Neutral(255);   // 深色 = 白，浅色 = 纯黑

            _uiTextPaint.Color = _fgColor;
            _subTextPaint.Color = Neutral(170);
            _titleTextPaint.Color = Neutral(200);
            _iconPaint.Color = _fgColor;
            _chevronPaint.Color = Neutral(150);

            // 半透明叠加层：深色白叠加 / 浅色黑叠加，alpha 与原值一一对应（观感对称）
            _tabBgSelected.Color = Overlay(15);
            _tabBgHovered.Color = Overlay(8);
            _separatorPaint.Color = Overlay(20);
            _hoverMinPaint.Color = Overlay(20);

            // 「显示内容」列表的排序箭头（三支静态画笔，见 Render.cs 的 DrawSortArrow）。
            // 这三支的颜色必须在这里重绑：它们在字段初始化时写的是深色外观下的值
            // （210 灰 / 白 / 130 灰），浅色外观下会变成「浅灰画在浅底上」几乎看不见。
            // 写成 Neutral() 而不是硬编码，才能跟着 _isLightAppearance 一起翻面。
            _sortArrowStroke.Color = Neutral(210);
            _sortArrowHoverStroke.Color = _fgColor;
            _sortArrowDisabledStroke.Color = Neutral(130);
        }

        private void ApplyBackdropPalette()
        {
            bool light = _isLightAppearance;

            // 下拉浮层永远是不透明纯色面板，与窗口是玻璃还是实色无关。
            //    以前这里跟着材质模式走半透明（alpha 172 / 210），展开列表时底下的卡片和文字
            //    会透上来，「看得见底下」很影响观感 —— 浮层本来就是盖住内容的实心层。
            //    色值固定、不随材质变化：深色比卡片（≈ Overlay(22) 叠玻璃）略亮，做出「浮起」感。
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
            // 标题栏不再单独盖一层深色底，否则顶栏会像“第二块面板”把亚克力吃掉。
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
            // 沉浸式深色标题栏：跟着当前外观走，浅色外观下必须是 0，否则 DWM 会给浅色玻璃配深色边框。
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

        // 亚克力的 tint（ABGR）：alpha 不能为 0，否则只剩模糊没有底色。
        // 深色是深灰底、浅色是浅灰底，alpha 保持一致（两套透明度观感才对得上）。

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

        // 唤醒 / 重新激活时「重新贴一次材质」，这是本窗口最容易被忽略的一步。
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

        // ─────────────────────────────────────────────────────────────────────────
        // Windows 10 专用：激活 / 从任务栏还原之后「整窗重建材质窗」。
        //
        // 背景：Win11（build 22000+）上 ReapplyBackdropMaterial() 重贴一次 accent 就能恢复；
        //       但 Win10 的 DWM 把 accent 策略缓存在 HWND 上 —— 对同一个 HWND 重贴相同的
        //       accent 不会触发任何重新合成，窗口就一直是一块透明的洞（前景 UI 还在，背后直接
        //       看到桌面）。用户实测「只有重新打开设置窗口才恢复」，而重新打开 = 全新的 HWND，
        //       所以这里直接照搬那条已被验证的路径：销毁旧材质窗 → 用与构造函数完全相同的顺序重建。
        //
        // 内容窗（Skia 前景）不动，所以看不到 UI 闪断；重建后立刻 SyncBackdropToContent() 把新窗
        // 压回内容窗正下方（新建窗口默认在 Z 序顶部，不压回去会盖住前景）。
        // ─────────────────────────────────────────────────────────────────────────

        private void RebuildBackdropWindow()
        {
            if (_backdropRebuilding || _hwnd == IntPtr.Zero)
                return;

            if (!Win32.GetWindowRect(_hwnd, out var rect))
                return;

            _backdropRebuilding = true;
            try
            {
                // 先摘句柄再销毁：销毁期间若还有消息回来，StaticWndProc 不会再把它当成材质窗。
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

                // 顺序严格照抄构造函数：region → 材质 → 调色板 → 压到内容窗正下方。
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

        // 激活 / 从任务栏还原之后把材质补回来，按系统分两条路：
        //   · Win11（build 22000+）：重贴一次 accent 就够 —— 保持原有行为不变；
        //   · Win10 + 亚克力：accent 挂在 HWND 上重贴无效，必须重建材质窗（见 RebuildBackdropWindow）。

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
