#if DEBUG
using NotchPeninsula.Plugins;
using SkiaSharp;

namespace NotchPeninsula;

/// <summary>
/// DEBUG 演示用的内置实况窗（环境变量 NPS_LIVE_DEMO=1 时由 Program.Main 启动）。
/// 只服务于视觉验收：模拟下载进度循环、不确定态旋转、自定义进度色和右键展开详情页。
/// Release 下整个文件不参与编译，正常用户永远看不到这些气泡。
/// </summary>
internal static class LiveWindowDemo
{
    private const string Owner = "builtin.demo";

    // 12s 一轮 = 11s 走完 0→100 + 1s 完成停留；重启后回落，能一眼看出平滑与完成脉冲。
    private const double RayProgressSeconds = 11.0;
    private const double RayHoldSeconds = 1.0;
    private const double OrangeCycleSeconds = 9.0;
    private const int TickMs = 200;

    private static readonly List<IDisposable?> Handles = new(3);

    private static System.Threading.Timer? _timer;
    private static DemoLiveWindow? _rayburst;
    private static DemoLiveWindow? _orange;

    private static double _rayElapsed;
    private static double _orangeElapsed = 3.6;   // 错开相位，两个进度窗不要同步得像一个

    public static void Start()
    {
        if (_timer != null) return;   // 只允许启动一次，否则会叠出多层定时器

        var detail = new RayburstDetailPage();
        var ray = new DemoLiveWindow(
            "builtin.demo.rayburst", "Rayburst", "downloading",
            progressColor: null, spin: false, detailPage: detail);
        detail.Source = ray;
        _rayburst = ray;

        var spinner = new DemoLiveWindow(
            "builtin.demo.spinner", "Syncing", "preparing",
            progressColor: null, spin: true, detailPage: null);
        spinner.SetIndeterminate();

        var orange = new DemoLiveWindow(
            "builtin.demo.orange", "Orange", "syncing",
            new SKColor(0xFF, 0x9F, 0x0A), spin: false, detailPage: null);
        _orange = orange;

        // 逐个注册：宿主按注册顺序分裂展开，最新的在最右边。
        Handles.Add(LiveWindowHost.Register(Owner, ray));
        Handles.Add(LiveWindowHost.Register(Owner, spinner));
        Handles.Add(LiveWindowHost.Register(Owner, orange));

        // 进度只在这里算：渲染线程的 GetVisual 只读 volatile 字段，不做任何重活。
        _timer = new System.Threading.Timer(_ => Tick(), null, TickMs, TickMs);
    }

    private static void Tick()
    {
        try
        {
            double dt = TickMs / 1000.0;

            _rayElapsed += dt;
            if (_rayElapsed >= RayProgressSeconds + RayHoldSeconds) _rayElapsed = 0d;
            float rayProgress = (float)Math.Clamp(_rayElapsed / RayProgressSeconds, 0d, 1d);
            _rayburst?.SetProgress(rayProgress);

            _orangeElapsed += dt;
            if (_orangeElapsed >= OrangeCycleSeconds) _orangeElapsed -= OrangeCycleSeconds;
            _orange?.SetProgress((float)(_orangeElapsed / OrangeCycleSeconds));
        }
        catch (Exception ex)
        {
            Logger.Error("[实况窗] 演示定时器更新失败", ex);
        }
    }

    /// <summary>演示用注册方：视觉状态全在 volatile 字段里，渲染线程只读。</summary>
    private sealed class DemoLiveWindow : ILiveWindow
    {
        private readonly string _title;
        private readonly string _status;
        private readonly SKColor? _progressColor;
        private readonly bool _spin;
        private readonly IDetailPage? _detail;

        private volatile float _progress = -1f;

        public DemoLiveWindow(string id, string title, string status, SKColor? progressColor, bool spin, IDetailPage? detailPage)
        {
            Id = id;
            _title = title;
            _status = status;
            _progressColor = progressColor;
            _spin = spin;
            _detail = detailPage;
        }

        public string Id { get; }

        public IDetailPage? DetailPage => _detail;

        public LiveWindowVisual GetVisual()
        {
            float p = _progress;
            return new LiveWindowVisual
            {
                Progress = p,
                Status = _status,
                Title = _title,
                ProgressColor = _progressColor,
                SpinInner = _spin,
                Completed = p >= 1f
            };
        }

        public void DrawInner(SKCanvas canvas, SKRect rect, WidgetFrame frame)
        {
            if (_spin) Spinner.Draw(canvas, rect, frame);
            else LiveWindowDefaults.DrawInnerText(canvas, rect, frame, GetVisual());
        }

        /// <summary>定时器线程调用；负值 = 不确定态。</summary>
        public void SetProgress(float progress) => _progress = Math.Clamp(progress, 0f, 1f);

        public void SetIndeterminate() => _progress = -1f;
    }

    /// <summary>不确定态示例的内圈：环形图标；宿主按 SpinInner 旋转整块内容。</summary>
    private static class Spinner
    {
        private static readonly SKPaint RingPaint = new()
        {
            IsAntialias = true,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 1.1f
        };

        private static readonly SKPaint ArcPaint = new()
        {
            IsAntialias = true,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 1.8f,
            StrokeCap = SKStrokeCap.Round
        };

        private static readonly SKPaint DotPaint = new()
        {
            IsAntialias = true,
            Style = SKPaintStyle.Fill
        };

        public static void Draw(SKCanvas canvas, SKRect rect, WidgetFrame frame)
        {
            float cx = rect.MidX;
            float cy = rect.MidY;
            float radius = MathF.Min(rect.Width, rect.Height) * 0.40f;
            var ring = new SKRect(cx - radius, cy - radius, cx + radius, cy + radius);

            RingPaint.Color = frame.Theme.TextColor.WithAlpha(70);
            canvas.DrawCircle(cx, cy, radius, RingPaint);

            // 单段弧 + 端点小圆点：不对称形状被宿主旋转后，静止截图里也能看出这是个转圈图标。
            ArcPaint.Color = frame.Theme.TextColor.WithAlpha(220);
            canvas.DrawArc(ring, -90f, 130f, false, ArcPaint);

            DotPaint.Color = frame.Theme.TextColor.WithAlpha(220);
            float endAngle = (-90f + 130f) * MathF.PI / 180f;
            canvas.DrawCircle(cx + MathF.Cos(endAngle) * radius, cy + MathF.Sin(endAngle) * radius, 1.5f, DotPaint);
        }
    }

    /// <summary>
    /// Rayburst 的右键展开页（验收「原有展开态」：面板从气泡长出来，主岛不变形）。
    /// 只展示 + 一个关闭按钮；按钮命中 action "close"，但本页不负责关面板 —— 收起由宿主右键处理。
    /// </summary>
    private sealed class RayburstDetailPage : IDetailPage
    {
        private static readonly SKPaint TitlePaint = new() { IsAntialias = true, TextAlign = SKTextAlign.Left };
        private static readonly SKPaint BodyPaint = new() { IsAntialias = true, TextAlign = SKTextAlign.Left };
        private static readonly SKPaint PercentPaint = new() { IsAntialias = true, TextAlign = SKTextAlign.Right };
        private static readonly SKPaint BarBgPaint = new() { IsAntialias = true, Style = SKPaintStyle.Fill };
        private static readonly SKPaint BarFgPaint = new() { IsAntialias = true, Style = SKPaintStyle.Fill };
        private static readonly SKPaint ButtonBgPaint = new() { IsAntialias = true, Style = SKPaintStyle.Fill };
        private static readonly SKPaint ButtonTextPaint = new() { IsAntialias = true, TextAlign = SKTextAlign.Center };

        private static readonly SKColor AccentColor = new(0x32, 0xD7, 0x4B);

        private int _lastPercent = -1;
        private string _percentText = "";

        /// <summary>进度来源（只读 volatile 字段）；Start() 里构造后回填，避免循环依赖。</summary>
        public DemoLiveWindow? Source;

        public float MeasureWidth() => 280f;

        public float MeasureHeight() => 150f;

        // 演示页面要能停住让人看清：不自动收起（负值 = 宿主永不排收起计时），右键气泡 / 面板才收。
        public TimeSpan AutoCollapseDelay => TimeSpan.FromSeconds(-1);

        public void Draw(SKCanvas canvas, SKRect rect, WidgetFrame frame)
        {
            try
            {
                float x = rect.Left + 18f;
                float w = rect.Width - 36f;
                var theme = frame.Theme;

                TitlePaint.Typeface = FontConfig.Bold;
                TitlePaint.Color = theme.TextColor;
                TitlePaint.TextSize = 15f;
                canvas.DrawText("Rayburst", x, rect.Top + 34f, TitlePaint);

                BodyPaint.Typeface = FontConfig.Normal;
                BodyPaint.Color = theme.SubTextColor;
                BodyPaint.TextSize = 11f;
                canvas.DrawText("内置实况窗演示 · 下载进度 0→100 循环", x, rect.Top + 62f, BodyPaint);
                canvas.DrawText("右键气泡可再次收起 · 本页只做展示", x, rect.Top + 82f, BodyPaint);

                float p = -1f;
                var source = Source;
                if (source != null) p = source.GetVisual().Progress;
                p = Math.Clamp(p, 0f, 1f);

                float barTop = rect.Top + 96f;
                BarBgPaint.Color = theme.TextColor.WithAlpha(28);
                canvas.DrawRoundRect(new SKRect(x, barTop, x + w, barTop + 6f), 3f, 3f, BarBgPaint);
                if (p > 0f)
                {
                    BarFgPaint.Color = AccentColor;
                    canvas.DrawRoundRect(new SKRect(x, barTop, x + w * p, barTop + 6f), 3f, 3f, BarFgPaint);
                }

                int percent = Math.Clamp((int)Math.Round(p * 100.0, MidpointRounding.AwayFromZero), 0, 100);
                if (percent != _lastPercent)
                {
                    _lastPercent = percent;
                    _percentText = percent + "%";
                }
                PercentPaint.Typeface = FontConfig.SemiBold;
                PercentPaint.Color = theme.TextColor;
                PercentPaint.TextSize = 11f;
                canvas.DrawText(_percentText, x + w, rect.Top + 90f, PercentPaint);

                var button = CloseButtonRect(rect);
                ButtonBgPaint.Color = theme.TextColor.WithAlpha(26);
                canvas.DrawRoundRect(button, 8f, 8f, ButtonBgPaint);
                ButtonTextPaint.Typeface = FontConfig.Normal;
                ButtonTextPaint.Color = theme.SubTextColor;
                ButtonTextPaint.TextSize = 10.5f;
                canvas.DrawText("关闭", button.MidX, button.MidY + 3.6f, ButtonTextPaint);
            }
            catch (Exception ex)
            {
                // 演示页也不能把渲染线程带崩：宿主每帧都会调这里。
                Logger.Error("[实况窗] 演示详情页绘制异常", ex);
            }
        }

        public WidgetHit HitTest(float x, float y, SKRect rect)
            => CloseButtonRect(rect).Contains(x, y) ? new WidgetHit("close") : WidgetHit.None;

        public void OnAction(string? action, float x, float y)
        {
            // 本页不关面板：收起是宿主右键气泡 / 面板的职责，插件没有关闭别人状态机的接口。
            if (action == "close")
                Logger.Debug("[实况窗] 演示详情页收到 close 动作（面板由宿主右键收起，这里不处理）");
        }

        private static SKRect CloseButtonRect(SKRect rect)
            => new(rect.Right - 18f - 56f, rect.Bottom - 18f - 24f, rect.Right - 18f, rect.Bottom - 18f);
    }
}
#endif
