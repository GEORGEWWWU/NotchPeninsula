using SkiaSharp;

namespace NotchPeninsula
{
    public partial class ConsoleWindow
    {
        internal const string SidebarCollapsedSetting = "SettingsSidebarCollapsed";
        internal static bool SidebarCollapsed { get; set; }
        private float SidebarWidth => SidebarCollapsed ? 60f : 160f;
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
