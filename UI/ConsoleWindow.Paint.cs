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
        // ----  ----
        //  明暗外观（跟随系统「应用模式」自动切换）
        //
        //  设置窗口的底是系统材质（亚克力 / 云母）或自绘实色，标题栏 / 材质本来就跟着系统
        //  深浅走，所以这里也只认系统「应用模式」（HKCU\...\Themes\Personalize\AppsUseLightTheme），
        //  不去看岛体的 ThemeMode —— 否则会出现「浅色窗口配深色亚克力」这种不自洽的观感。
        //
        //  中性色的取反规则只有两条，别各写各的：
        //    · Neutral(v)  —— 不透明中性灰：浅色外观 = 255 - v（灰度取反）。深色一列与历史硬编码
        //                     值逐位一致，所以老外观一点没变。white(255) 取反就是纯黑，
        //                     与岛体浅色主题的「黑字 + 80 灰副标题」也正好对得上。
        //    · Overlay(a)  —— 半透明叠加层（卡片底 / 分隔线 / 悬停底 / 滚动条）：深色 = 白叠加，
        //                     浅色 = 黑叠加，alpha 保持不变。白 a% 叠深底与黑 a% 叠浅底在亮度上
        //                     是对称的，所以两套观感一致。
        //
        //  这两个是「基准色」的来源：任何地方临时改了共享画笔的颜色，用完必须恢复到
        //     Neutral / Overlay / _fgColor 的当前值，不能写死 SKColors.White（见 Render 里的
        //     「恢复基准色」注释）—— 否则浅色外观下会残留白字 / 白线。
        // ----  ----

        private static bool _isLightAppearance;

        /// <summary>主前景色（文字 / 图标 / 图形描边）。深色=白，浅色=纯黑。</summary>
        private static SKColor _fgColor = SKColors.White;

        /// <summary>不透明中性灰在当前外观下的同义色。</summary>
        private static SKColor Neutral(byte v)
            => _isLightAppearance
                ? new SKColor((byte)(255 - v), (byte)(255 - v), (byte)(255 - v))
                : new SKColor(v, v, v);

        /// <summary>半透明叠加层在当前外观下的同义色（深色=白叠加，浅色=黑叠加）。</summary>
        private static SKColor Overlay(byte alpha)
            => _isLightAppearance
                ? new SKColor(0, 0, 0, alpha)
                : new SKColor(255, 255, 255, alpha);

        /// <summary>系统「应用模式」是不是浅色。走进程级缓存（Renderer.SystemIsLightTheme）——
        /// 系统主题变更 / 用户改「主题模式」时缓存会被作废，这里不承担失效职责。</summary>
        private static bool IsSystemLightAppearance()
        {
            return Renderer.SystemIsLightTheme;
        }

        /// <summary>
        /// 「显示设置」页里那些胶囊示意图（显示形态 / 显示模式 / 待机场景）该用白底还是黑底。
        /// 
        /// 唯一的真源。这三处以前各自抄了一遍「ThemeMode==1 则白、==2 则读注册表」的逻辑，
        /// 等于每次渲染显示设置页要踩 8 次注册表（每帧），滚轮翻页时就是每秒几十次。
        /// 现在统一收口到这里，「跟随系统」读 Renderer.SystemIsLightTheme 缓存。
        /// 
        /// 注意：它看的是 ThemeMode（用户在「主题设置」页选的岛体主题），
        /// 而不是 _isLightAppearance（系统「应用模式」，管的是设置窗口自身的底色）——
        /// 两者在 ThemeMode 取 0 / 1（手动黑 / 手动白）时会不一致，别合并。
        /// </summary>
        private static bool IsLightPreviewCapsule()
            => Renderer.ThemeMode == 2 ? Renderer.SystemIsLightTheme : Renderer.ThemeMode == 1;

        // 极致内存优化：全局复用画笔缓存
        private static readonly SKPaint _bgPaint = new SKPaint { Color = new SKColor(32, 32, 32), IsAntialias = true };

        private static readonly SKPaint _titleBarPaint = new SKPaint { Color = new SKColor(40, 40, 40) };

        private static readonly SKPaint _uiTextPaint = new SKPaint { Color = SKColors.White, TextSize = 13.5f, IsAntialias = true, Typeface = SKTypeface.FromFamilyName("Microsoft YaHei UI") };

        private static readonly SKPaint _subTextPaint = new SKPaint { Color = new SKColor(170, 170, 170), TextSize = 12f, IsAntialias = true, Typeface = SKTypeface.FromFamilyName("Microsoft YaHei UI") };

        // ---- 卡片右上角的「实验性」小标签（见 DrawCornerBadge）----
        // 颜色每次画的时候现设：琥珀底 + 深棕字，深色 / 浅色两套外观下都读得清，
        // 所以不必像 _cardBg 那样跟着 Neutral() 走。阴影单独一支，不和别的共享。
        private static readonly SKPaint _badgeTextPaint = new SKPaint { TextSize = 10.5f, IsAntialias = true, Typeface = SKTypeface.FromFamilyName("Microsoft YaHei UI") };
        private static readonly SKPaint _badgeBgPaint = new SKPaint { IsAntialias = true };
        private static readonly SKPaint _badgeShadowPaint = new SKPaint { IsAntialias = true };

        /// <summary>
        /// 插件市场顶部两行（分类 / 搜索框 / 刷新 / 只看已安装 / 计数）专用画笔。
        /// 
        /// 为什么要单开一支：这两行里原本混用了 _uiTextPaint(13.5) 与 _subTextPaint(12)，
        /// 同一行内字号不一致、视觉上「大小不一」。改 _uiTextPaint / _subTextPaint 的字号会波及
        /// 全窗口其它页面，所以市场顶栏收口到这支 13px 画笔上。
        /// 
        /// ⚠️ 截断 / 宽度测量必须与绘制用**同一支画笔**，否则算出的可用宽度与实际字形对不上。
        /// </summary>
        private static readonly SKPaint _marketTextPaint = new SKPaint { Color = new SKColor(170, 170, 170), TextSize = 13f, IsAntialias = true, Typeface = SKTypeface.FromFamilyName("Microsoft YaHei UI") };

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

        // 下拉浮层：不透明纯色面板（alpha 固定 255）。色值由 ApplyBackdropPalette() 按明暗收口，
        // 这里的初值只是窗口创建到首次刷调色板之间的兜底，不要再给它们加 alpha。
        private static readonly SKPaint _menuBg = new SKPaint { Color = new SKColor(48, 48, 48), IsAntialias = true };

        private static readonly SKPaint _menuBorder = new SKPaint { Color = new SKColor(88, 88, 88), Style = SKPaintStyle.Stroke, StrokeWidth = 1, IsAntialias = true };

        private static readonly SKPaint _globalBorderPaint = new SKPaint { Color = new SKColor(60, 60, 60), Style = SKPaintStyle.Stroke, StrokeWidth = 1, IsAntialias = true };

        private static readonly SKPaint _toggleCirclePaint = new SKPaint { Color = SKColors.White, IsAntialias = true };

        // 动态状态画笔

        private static readonly SKPaint _dynamicFillPaint = new SKPaint { IsAntialias = true };

        private static readonly SKPaint _dynamicStrokePaint = new SKPaint { Style = SKPaintStyle.Stroke, StrokeWidth = 1.5f, IsAntialias = true };

        private static readonly SKPaint _dynamicTextPaint = new SKPaint { TextSize = 13f, IsAntialias = true, Typeface = SKTypeface.FromFamilyName("Microsoft YaHei UI") };

        // 窗口圆角裁剪路径：只取决于编译期常量 WIDTH / HEIGHT 与固定圆角 8（DPI 缩放由画布矩阵负责），
        // 所以整个进程只需要这一条。原实现是每次 Render 都 new 一条 SKPath 再 ClipPath。
        private static readonly SKPath WindowClipPath = CreateWindowClipPath();

        private static SKPath CreateWindowClipPath()
        {
            var path = new SKPath();
            path.AddRoundRect(new SKRect(0, 0, WIDTH, HEIGHT), 8f, 8f);
            return path;
        }

        // 音量档位下拉的标签（"0%" … "100%"）：来源是固定的 ToastSoundConfig.VolumeOptions，
        // 进程内建一次即可。原来每次渲染展开的音量下拉都新建 string[11] 并逐项插值。
        private static readonly string[] VolumeOptionLabels = BuildVolumeOptionLabels();

        private static string[] BuildVolumeOptionLabels()
        {
            var options = ToastSoundConfig.VolumeOptions;
            var labels = new string[options.Length];
            for (int i = 0; i < labels.Length; i++) labels[i] = $"{options[i]}%";
            return labels;
        }

        // 背景透明度滑轨的 5 个刻度文案（0% / 25% / 50% / 75% / 100%），同样只建一次。
        private static readonly string[] OpacityStopLabels = ["0%", "25%", "50%", "75%", "100%"];
    }
}
