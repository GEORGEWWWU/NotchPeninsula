using SkiaSharp;

namespace NotchPeninsula
{
    public partial class ConsoleWindow
    {
        internal const string SidebarCollapsedSetting = "SettingsSidebarCollapsed";
        internal static bool SidebarCollapsed { get; set; }

        // ---- 侧栏宽度动画（折叠不再是瞬切）----
        // 宽度 160 ⇄ 60 在 SIDEBAR_ANIM_MS 内走完；卡片左缘 CONTENT_L 直接读 SidebarWidth，
        // 所以内容区跟着一起滑，不会「侧栏一跳、卡片瞬间换宽」。
        // 参数口径见 Core/LiveWindowHost.cs 顶部的「动画总表」（侧栏折叠 200ms / TabEase）。
        private const float SIDEBAR_EXPANDED_W = 160f;
        private const float SIDEBAR_COLLAPSED_W = 60f;
        private const float SIDEBAR_ANIM_MS = 200f;

        // 初值给展开态：真正该多少由首帧 TickSidebarWidthAnim 按设置落到目标（拍一帧即可，不播动画）。
        private float _sidebarW = SIDEBAR_EXPANDED_W;
        private float _sidebarWFrom = SIDEBAR_EXPANDED_W;
        private float _sidebarWTo = SIDEBAR_EXPANDED_W;
        private long _sidebarWStart;
        private bool _sidebarWInit;

        private float SidebarWidth => _sidebarW;

        /// <summary>折叠进度：0 = 完全展开、1 = 完全收起。所有随宽度变化的几何都从它推，
        /// 避免「一部分判布尔、一部分判宽度」导致动画途中两套坐标打架。</summary>
        private float SidebarCollapseT => Math.Clamp(
            (SIDEBAR_EXPANDED_W - _sidebarW) / (SIDEBAR_EXPANDED_W - SIDEBAR_COLLAPSED_W), 0f, 1f);

        /// <summary>标签 / 文字淡出进度：比宽度稍快一点，避免和「卡片左缘扫过来」撞在一起。</summary>
        private float SidebarLabelAlpha => 1f - Math.Clamp(SidebarCollapseT * 1.35f, 0f, 1f);

        private bool SidebarAnimating => Math.Abs(_sidebarW - _sidebarWTo) > 0.01f;

        /// <summary>每帧推进宽度动画。在 RenderCore 入口调用，位移永远随时钟走，掉帧只会少画几帧。</summary>
        private void TickSidebarWidthAnim()
        {
            float target = SidebarCollapsed ? SIDEBAR_COLLAPSED_W : SIDEBAR_EXPANDED_W;

            if (!_sidebarWInit)
            {
                // 首帧（含启动时读回设置）：直接落到目标宽度，不播动画。
                _sidebarWInit = true;
                _sidebarW = _sidebarWFrom = _sidebarWTo = target;
                _sidebarWStart = Environment.TickCount64;
                return;
            }

            if (Math.Abs(target - _sidebarWTo) > 0.001f)
            {
                _sidebarWFrom = _sidebarW;
                _sidebarWTo = target;
                _sidebarWStart = Environment.TickCount64;
            }

            if (!SidebarAnimating) { _sidebarW = _sidebarWTo; return; }

            float t = Math.Clamp((Environment.TickCount64 - _sidebarWStart) / SIDEBAR_ANIM_MS, 0f, 1f);
            _sidebarW = _sidebarWFrom + (_sidebarWTo - _sidebarWFrom) * TabEase(t);
            if (t >= 1f) _sidebarW = _sidebarWTo;
            else StartDisplayHoverAnim();   // 还没走完：保证节拍在跑（幂等）
        }

        // 展开 / 收起两端各自的几何，按进度插值（动画途中就是中间态）。
        private float SidebarExpandedTabLeft => SidebarButtonInset;
        private float SidebarCollapsedTabLeft => SidebarIconCenterX - TAB_ROW_H / 2f;

        private bool _sidebarToggleHovered;
        private bool _trackingSidebarMouse;
        private const string SidebarToggleGlyph = "\uE700";
        private static readonly SKRect SidebarToggleRect = new(10, TitleBarCenterY - 12, 50, TitleBarCenterY + 12);
        private static readonly int[] SidebarOrder = [5, 0, 1, 9, 8, 2, 3, 6, 7, 4];
        private static readonly string[] SidebarLabels =
        [
            "通用设置", "显示设置", "媒体设置", "交互设置", "关于软件",
            "个性化中心", "我的插件", "插件市场", "实况窗管理", "静默模式设置"
        ];
        private static readonly string[] SidebarIcons =
        [
            "\uE713", "\uE7F4", "\uE8D6", "\uE7C9", "\uE946",
            "\uE790", "\uE74C", "\uE719", "\uE7F8", "\uE91C"
        ];
        private static readonly SKPaint SidebarIconPaint = new()
        {
            IsAntialias = true,
            Typeface = SKTypeface.FromFamilyName("Segoe MDL2 Assets"),
            TextSize = 16,
            TextAlign = SKTextAlign.Center
        };

        private static readonly float SidebarToggleInkMidY = MeasureInkMidY(SidebarIconPaint, SidebarToggleGlyph);

        private void TrackSidebarMouseLeave(IntPtr hwnd)
        {
            if (_trackingSidebarMouse)
                return;
            var tracking = new Win32.TRACKMOUSEEVENT
            {
                cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<Win32.TRACKMOUSEEVENT>(),
                dwFlags = Win32.TME_LEAVE,
                hwndTrack = hwnd
            };
            _trackingSidebarMouse = Win32.TrackMouseEvent(ref tracking);
        }

        private void DrawSidebarIcon(SKCanvas canvas, string glyph, float x, float centerY)
        {
            SidebarIconPaint.Color = _fgColor;
            var metrics = SidebarIconPaint.FontMetrics;
            canvas.DrawText(glyph, x, centerY - (metrics.Ascent + metrics.Descent) / 2, SidebarIconPaint);
        }

        private void DrawSidebarToggle(SKCanvas canvas)
        {
            if (_sidebarToggleHovered && !IsConfirmDialogOpen)
            {
                _dynamicFillPaint.Color = Overlay(22);
                canvas.DrawRoundRect(SidebarToggleRect, 4, 4, _dynamicFillPaint);
            }
            SidebarIconPaint.Color = _fgColor;
            canvas.DrawText(SidebarToggleGlyph, SidebarToggleRect.MidX,
                TitleBarCenterY - SidebarToggleInkMidY, SidebarIconPaint);
        }

        private void DrawSidebarTooltip(SKCanvas canvas)
        {
            if (IsConfirmDialogOpen)
                return;
            string? text = _sidebarToggleHovered ? (SidebarCollapsed ? "展开侧栏" : "收起侧栏")
                : !SidebarCollapsed ? null
                : _hoveredTab >= 0 ? SidebarLabels[_hoveredTab]
                : _cacheCleanHovered ? "清理解包缓存"
                : _aboutExportHovered ? "导出诊断包" : null;
            if (text == null)
                return;

            float centerY = _sidebarToggleHovered ? TITLE_BAR_HEIGHT + 14
                : _hoveredTab >= 0 ? TITLE_BAR_HEIGHT + TabRowY(_hoveredTab) + TAB_ROW_H / 2
                : (_cacheCleanHovered ? CACHE_BTN_Y : EXPORT_BTN_Y) + EXPORT_BTN_H / 2;
            float x = _sidebarToggleHovered ? SidebarToggleRect.Right + 4 : SidebarWidth - 2;
            var bounds = new SKRect(x, centerY - 14, x + _uiTextPaint.MeasureText(text) + 20, centerY + 14);
            canvas.DrawRoundRect(bounds, 4, 4, _menuBg);
            canvas.DrawRoundRect(bounds, 4, 4, _menuBorder);
            canvas.DrawText(text, x + 10, centerY + 5, _uiTextPaint);
        }
    }
}
