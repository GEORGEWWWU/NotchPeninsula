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
        // 极致内存优化：全局复用画笔缓存
        private static readonly SKPaint _bgPaint = new SKPaint { Color = new SKColor(32, 32, 32), IsAntialias = true };

        private static readonly SKPaint _titleBarPaint = new SKPaint { Color = new SKColor(40, 40, 40) };

        private static readonly SKPaint _uiTextPaint = new SKPaint { Color = SKColors.White, TextSize = 13.5f, IsAntialias = true, Typeface = SKTypeface.FromFamilyName("Microsoft YaHei UI") };

        private static readonly SKPaint _subTextPaint = new SKPaint { Color = new SKColor(170, 170, 170), TextSize = 12f, IsAntialias = true, Typeface = SKTypeface.FromFamilyName("Microsoft YaHei UI") };

        private static readonly SKPaint _titleTextPaint = new SKPaint { Color = new SKColor(200, 200, 200), TextSize = 12.5f, IsAntialias = true, Typeface = SKTypeface.FromFamilyName("Microsoft YaHei UI") };

        private static readonly SKPaint _hqSamplingOpts = new SKPaint { FilterQuality = SKFilterQuality.High, IsAntialias = true };

        private static readonly SKPaint _iconPaint = new SKPaint { Color = SKColors.White, Style = SKPaintStyle.Stroke, StrokeWidth = 1f, IsAntialias = true };

        // 窗口控制按钮画笔

        private static readonly SKPaint _hoverMinPaint = new SKPaint { Color = new SKColor(255, 255, 255, 20) };

        private static readonly SKPaint _hoverClosePaint = new SKPaint { Color = new SKColor(232, 17, 35) };

        // 侧边栏与卡片画笔

        private static readonly SKPaint _tabBgSelected = new SKPaint { Color = new SKColor(255, 255, 255, 15), IsAntialias = true };

        private static readonly SKPaint _tabBgHovered = new SKPaint { Color = new SKColor(255, 255, 255, 8), IsAntialias = true };

        private static readonly SKPaint _tabIndicator = new SKPaint { Color = new SKColor(0, 120, 212), IsAntialias = true };

        private static readonly SKPaint _cardBg = new SKPaint { Color = new SKColor(255, 255, 255, 8), IsAntialias = true };

        private static readonly SKPaint _cardBorder = new SKPaint { Color = new SKColor(255, 255, 255, 15), Style = SKPaintStyle.Stroke, StrokeWidth = 1, IsAntialias = true };

        private static readonly SKPaint _separatorPaint = new SKPaint { Color = new SKColor(255, 255, 255, 20), StrokeWidth = 1, IsAntialias = true };

        // UI 组件画笔

        private static readonly SKPaint _chevronPaint = new SKPaint { Color = new SKColor(150, 150, 150), Style = SKPaintStyle.Stroke, StrokeWidth = 1.5f, IsAntialias = true };

        private static readonly SKPaint _menuBg = new SKPaint { Color = new SKColor(40, 40, 40), IsAntialias = true };

        private static readonly SKPaint _menuBorder = new SKPaint { Color = new SKColor(80, 80, 80), Style = SKPaintStyle.Stroke, StrokeWidth = 1, IsAntialias = true };

        private static readonly SKPaint _globalBorderPaint = new SKPaint { Color = new SKColor(60, 60, 60), Style = SKPaintStyle.Stroke, StrokeWidth = 1, IsAntialias = true };

        private static readonly SKPaint _toggleCirclePaint = new SKPaint { Color = SKColors.White, IsAntialias = true };

        // 动态状态画笔

        private static readonly SKPaint _dynamicFillPaint = new SKPaint { IsAntialias = true };

        private static readonly SKPaint _dynamicStrokePaint = new SKPaint { Style = SKPaintStyle.Stroke, StrokeWidth = 1.5f, IsAntialias = true };

        private static readonly SKPaint _dynamicTextPaint = new SKPaint { TextSize = 13f, IsAntialias = true, Typeface = SKTypeface.FromFamilyName("Microsoft YaHei UI") };
    }
}
