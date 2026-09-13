using System.IO;
using System.Runtime.InteropServices;
using NotchPeninsula.Plugins;
using SkiaSharp;
using Windows.Media.Control;

namespace SystemPlugins;

/// <summary>内置系统组件插件：时间日期 / 系统资源 / 媒体。</summary>
public sealed class SystemPluginsPlugin : INotchPlugin
{
    public string Id => "system";
    public string DisplayName => "系统组件";
    public string Version => "1.0.0";

    public void Initialize(IPluginHost host)
    {
        _ = new MediaController(host); // 启动媒体引擎单例（SMTC + 歌词 + 频谱）
        host.RegisterWidget(new ClockWidget());
        host.RegisterWidget(new HardwareWidget(host));
        host.RegisterWidget(new MediaWidget(host));
        host.RegisterSettingsPage(new SystemSettingsPage());
    }
}

/// <summary>系统组件插件的设置页（含媒体设置，全部走声明式控件）。</summary>
public sealed class SystemSettingsPage : ISettingsPage
{
    public string Title => "系统组件";
    public IReadOnlyList<SettingControl> Controls { get; } = new SettingControl[]
    {
        new NumberSetting("HardwareInterval", "硬件采样间隔(秒)", 1f, 10f, 1f, 1f),
        new NumberSetting("MediaInterval", "媒体刷新间隔(秒)", 1f, 10f, 1f, 2f),
        new ToggleSetting("MediaControlEnabled", "媒体控制", true),
        new ChoiceSetting("TargetPlatform", "目标媒体平台", new[] { "通用媒体", "网易云音乐", "QQ音乐", "酷狗音乐", "Spotify", "Apple Music", "Echo Music", "LX Music" }, 0),
        new ToggleSetting("LyricsEnabled", "在刘海中显示歌词", true),
        new ToggleSetting("KaraokeEnabled", "开启卡拉OK动效", true),
        new NumberSetting("LyricDelayOffset", "歌词延迟补偿(秒)", -5f, 5f, 0.1f, 0f),
    };
}

/// <summary>时间日期组件（自包含）。</summary>
public sealed class ClockWidget : IWidget
{
    private static readonly SKPaint _timePaint = new()
    {
        TextSize = 14.5f, IsAntialias = true,
        Typeface = SKTypeface.FromFamilyName("Microsoft YaHei UI", SKFontStyleWeight.Bold, SKFontStyleWidth.Normal, SKFontStyleSlant.Upright)
    };
    private static readonly SKPaint _datePaint = new()
    {
        TextSize = 14.5f, IsAntialias = true,
        Typeface = SKTypeface.FromFamilyName("Microsoft YaHei UI")
    };
    private int _lastMinute = -1;
    private string _timeStr = "", _dateStr = "";
    private float _timeWidth, _dateWidth;
    private readonly ClockDetailPage _detailPage = new();

    public string Id => "builtin.clock";
    public string DisplayName => "时间日期";
    public IDetailPage? DetailPage => _detailPage;

    public float MeasureWidth(float availableHeight) { Update(); return _timeWidth + 12f + _dateWidth; }
    public void Draw(SKCanvas canvas, SKRect rect, WidgetFrame frame)
    {
        Update();
        _timePaint.Color = frame.Theme.TextColor.WithAlpha(frame.Alpha);
        _datePaint.Color = frame.Theme.SubTextColor.WithAlpha(frame.Alpha);
        float baseline = rect.MidY + 5f;
        canvas.DrawText(_timeStr, rect.Left, baseline, _timePaint);
        canvas.DrawText(_dateStr, rect.Left + _timeWidth + 12f, baseline, _datePaint);
    }

    private void Update()
    {
        var now = DateTime.Now;
        if (_lastMinute == now.Minute) return;
        _lastMinute = now.Minute;
        _timeStr = now.ToString("HH:mm");
        _dateStr = now.ToString("MM/dd");
        _timeWidth = _timePaint.MeasureText(_timeStr);
        _dateWidth = _datePaint.MeasureText(_dateStr);
    }

    public WidgetHit HitTest(float x, float y, SKRect rect) => WidgetHit.None;
    public void OnLeftClick(string? action, float x, float y) { }
    public void OnRightClick() { }
    public void OnActivate(IPluginHost host) { }
    public void OnDeactivate() { }
}

/// <summary>时间日期详情页：模拟钟表 + 日期。</summary>
public sealed class ClockDetailPage : IDetailPage
{
    private static readonly SKPaint _titlePaint = new()
    {
        TextSize = 13f, IsAntialias = true,
        Typeface = SKTypeface.FromFamilyName("Microsoft YaHei UI", SKFontStyleWeight.Bold, SKFontStyleWidth.Normal, SKFontStyleSlant.Upright)
    };
    private static readonly SKPaint _subPaint = new()
    {
        TextSize = 12f, IsAntialias = true,
        Typeface = SKTypeface.FromFamilyName("Microsoft YaHei UI")
    };
    private static readonly SKPaint _timePaint = new()
    {
        TextSize = 18f, IsAntialias = true,
        Typeface = SKTypeface.FromFamilyName("Microsoft YaHei UI", SKFontStyleWeight.Bold, SKFontStyleWidth.Normal, SKFontStyleSlant.Upright)
    };
    private static readonly SKPaint _facePaint = new() { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 2f };
    private static readonly SKPaint _tickPaint = new() { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1.5f };
    private static readonly SKPaint _handPaint = new() { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeCap = SKStrokeCap.Round };
    private static readonly SKPaint _secPaint = new() { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeCap = SKStrokeCap.Round };
    private static readonly string[] _weekdays = new[] { "日", "一", "二", "三", "四", "五", "六" };

    public float MeasureWidth() => 320f;
    public float MeasureHeight() => 130f;

    public void Draw(SKCanvas canvas, SKRect rect, WidgetFrame frame)
    {
        var now = DateTime.Now;
        byte alpha = frame.Alpha;

        // 表盘
        float cx = rect.Left + 62f;
        float cy = rect.Top + 62f;
        float radius = 44f;
        _facePaint.Color = frame.Theme.TextColor.WithAlpha(alpha);
        canvas.DrawCircle(cx, cy, radius, _facePaint);
        _tickPaint.Color = frame.Theme.TextColor.WithAlpha(alpha);
        for (int i = 0; i < 12; i++)
        {
            double ang = i * Math.PI / 6.0;
            float r1 = (i % 3 == 0) ? radius - 9f : radius - 6f;
            float x1 = cx + (float)Math.Sin(ang) * r1;
            float y1 = cy - (float)Math.Cos(ang) * r1;
            float x2 = cx + (float)Math.Sin(ang) * radius;
            float y2 = cy - (float)Math.Cos(ang) * radius;
            canvas.DrawLine(x1, y1, x2, y2, _tickPaint);
        }

        // 指针
        _handPaint.Color = frame.Theme.TextColor.WithAlpha(alpha);
        double hourAng = ((now.Hour % 12) + now.Minute / 60.0) * Math.PI / 6.0;
        DrawHand(canvas, cx, cy, hourAng, radius * 0.5f, 3.5f, _handPaint);
        double minAng = now.Minute * Math.PI / 30.0;
        DrawHand(canvas, cx, cy, minAng, radius * 0.75f, 2.5f, _handPaint);
        _secPaint.Color = new SKColor(0, 140, 240).WithAlpha(alpha);
        double secAng = now.Second * Math.PI / 30.0;
        DrawHand(canvas, cx, cy, secAng, radius * 0.85f, 1.2f, _secPaint);
        canvas.DrawCircle(cx, cy, 3f, _secPaint);

        // 右侧：时间 + 日期
        float tx = rect.Left + 122f;
        _timePaint.Color = frame.Theme.TextColor.WithAlpha(alpha);
        canvas.DrawText($"{now.Hour:00}:{now.Minute:00}:{now.Second:00}", tx, rect.Top + 58f, _timePaint);
        _titlePaint.Color = frame.Theme.TextColor.WithAlpha(alpha);
        canvas.DrawText($"{now.Year}年{now.Month}月{now.Day}日", tx, rect.Top + 82f, _titlePaint);
        _subPaint.Color = frame.Theme.SubTextColor.WithAlpha(alpha);
        canvas.DrawText($"星期{_weekdays[(int)now.DayOfWeek]}", tx, rect.Top + 104f, _subPaint);
    }

    private void DrawHand(SKCanvas canvas, float cx, float cy, double angle, float length, float width, SKPaint paint)
    {
        float x = cx + (float)Math.Sin(angle) * length;
        float y = cy - (float)Math.Cos(angle) * length;
        paint.StrokeWidth = width;
        canvas.DrawLine(cx, cy, x, y, paint);
    }

    public WidgetHit HitTest(float x, float y, SKRect rect) => WidgetHit.None;
    public void OnAction(string? action, float x, float y) { }
}

/// <summary>系统资源组件（CPU/RAM，自包含 Win32 读取）。</summary>
public sealed class HardwareWidget : IWidget
{
    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength; public uint dwMemoryLoad;
        public ulong ullTotalPhys; public ulong ullAvailPhys;
        public ulong ullTotalPageFile; public ulong ullAvailPageFile;
        public ulong ullTotalVirtual; public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct FILETIME { public uint dwLowDateTime; public uint dwHighDateTime; }
    [DllImport("kernel32.dll")] private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);
    [DllImport("kernel32.dll")] private static extern bool GetSystemTimes(out FILETIME lpIdleTime, out FILETIME lpKernelTime, out FILETIME lpUserTime);

    private static readonly SKPaint _tagPaint = new()
    {
        TextSize = 10.5f, IsAntialias = true,
        Typeface = SKTypeface.FromFamilyName("Microsoft YaHei UI", SKFontStyleWeight.Bold, SKFontStyleWidth.Normal, SKFontStyleSlant.Upright)
    };
    private static readonly SKPaint _tagBg = new() { IsAntialias = true, Style = SKPaintStyle.Fill };
    private static readonly SKPaint _pctPaint = new()
    {
        TextSize = 12.5f, IsAntialias = true,
        Typeface = SKTypeface.FromFamilyName("Microsoft YaHei UI", SKFontStyleWeight.SemiBold, SKFontStyleWidth.Normal, SKFontStyleSlant.Upright)
    };
    private static readonly SKPaint _barBg = new() { IsAntialias = true, Style = SKPaintStyle.Fill };
    private static readonly SKPaint _bar = new() { IsAntialias = true };
    private static readonly string[] _pctStrs = BuildPcts();

    private volatile int _cpu, _ram;
    private float _smoothCpu, _smoothRam;
    private ulong _lastIdle, _lastSys;
    private readonly object _histLock = new();
    private readonly List<float> _cpuHistory = new();
    private readonly List<float> _ramHistory = new();
    private readonly HardwareDetailPage _detailPage;

    public HardwareWidget(IPluginHost host)
    {
        _detailPage = new HardwareDetailPage(this);
        // 常驻采样：读 Win32 并推入历史，保证详情页打开时曲线连贯
        double interval = double.TryParse(host.GetSetting("HardwareInterval", "1"), out var iv) ? Math.Clamp(iv, 1, 10) : 1;
        host.ScheduleRefresh(TimeSpan.FromSeconds(interval), () => SampleOnce());
    }

    internal int CpuPercent => _cpu;
    internal int RamPercent => _ram;

    internal (float[] Cpu, float[] Ram, int Count) GetHistorySnapshot()
    {
        lock (_histLock)
        {
            return (_cpuHistory.ToArray(), _ramHistory.ToArray(), _cpuHistory.Count);
        }
    }

    public string Id => "builtin.hardware";
    public string DisplayName => "系统资源";
    public IDetailPage? DetailPage => _detailPage;

    public float MeasureWidth(float availableHeight)
    {
        float cpuLabelW = _tagPaint.MeasureText("CPU");
        float ramLabelW = _tagPaint.MeasureText("RAM");
        float pctW = _pctPaint.MeasureText("90%");
        float cpuGroupW = (cpuLabelW + 6f) + 4f + pctW;
        float ramGroupW = (ramLabelW + 6f) + 4f + pctW;
        return cpuGroupW + 16f + ramGroupW;
    }

    public void Draw(SKCanvas canvas, SKRect rect, WidgetFrame frame)
    {
        _smoothCpu += (_cpu - _smoothCpu) * 0.2f;
        _smoothRam += (_ram - _smoothRam) * 0.2f;
        byte alpha = frame.Alpha;
        _tagPaint.Color = frame.Theme.TextColor.WithAlpha(alpha);
        _tagBg.Color = frame.Theme.TextColor.WithAlpha((byte)(alpha * 0.12f));
        _pctPaint.Color = frame.Theme.TextColor.WithAlpha(alpha);
        _barBg.Color = frame.Theme.TextColor.WithAlpha((byte)(alpha * 0.20f));
        _bar.Color = frame.Theme.TextColor.WithAlpha(alpha);

        float centerY = rect.MidY;
        float textBaseline = centerY - 1f;
        float barTop = centerY + 9f;
        float barH = 3.5f;
        float tagPadX = 3f, tagPadY = 1.5f, tagRadius = 3.5f;
        float gapLabelPct = 4f, gapCpuRam = 16f;

        string cpuPct = _pctStrs[_cpu];
        string ramPct = _pctStrs[_ram];
        float cpuLabelW = _tagPaint.MeasureText("CPU");
        float ramLabelW = _tagPaint.MeasureText("RAM");
        float fixedPctW = _pctPaint.MeasureText("90%");
        float cpuTagW = cpuLabelW + tagPadX * 2f;
        float ramTagW = ramLabelW + tagPadX * 2f;
        float cpuGroupW = cpuTagW + gapLabelPct + fixedPctW;
        float ramGroupW = ramTagW + gapLabelPct + fixedPctW;

        float cpuX = rect.Left;
        var cpuTagRect = new SKRect(cpuX, textBaseline - 10f - tagPadY, cpuX + cpuTagW, textBaseline + 2.5f + tagPadY);
        canvas.DrawRoundRect(cpuTagRect, tagRadius, tagRadius, _tagBg);
        canvas.DrawText("CPU", cpuX + tagPadX, textBaseline, _tagPaint);
        canvas.DrawText(cpuPct, cpuTagRect.Right + gapLabelPct, textBaseline, _pctPaint);
        canvas.DrawRoundRect(new SKRect(cpuX, barTop, cpuX + cpuGroupW, barTop + barH), barH / 2f, barH / 2f, _barBg);
        float cpuFillW = cpuGroupW * (_smoothCpu / 100f);
        if (cpuFillW > 0.5f)
            canvas.DrawRoundRect(new SKRect(cpuX, barTop, cpuX + cpuFillW, barTop + barH), barH / 2f, barH / 2f, _bar);

        float ramX = rect.Left + cpuGroupW + gapCpuRam;
        var ramTagRect = new SKRect(ramX, textBaseline - 10f - tagPadY, ramX + ramTagW, textBaseline + 2.5f + tagPadY);
        canvas.DrawRoundRect(ramTagRect, tagRadius, tagRadius, _tagBg);
        canvas.DrawText("RAM", ramX + tagPadX, textBaseline, _tagPaint);
        canvas.DrawText(ramPct, ramTagRect.Right + gapLabelPct, textBaseline, _pctPaint);
        canvas.DrawRoundRect(new SKRect(ramX, barTop, ramX + ramGroupW, barTop + barH), barH / 2f, barH / 2f, _barBg);
        float ramFillW = ramGroupW * (_smoothRam / 100f);
        if (ramFillW > 0.5f)
            canvas.DrawRoundRect(new SKRect(ramX, barTop, ramX + ramFillW, barTop + barH), barH / 2f, barH / 2f, _bar);
    }

    private void SampleOnce()
    {
        var mem = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf(typeof(MEMORYSTATUSEX)) };
        if (GlobalMemoryStatusEx(ref mem)) _ram = (int)mem.dwMemoryLoad;

        if (GetSystemTimes(out var idle, out var kernel, out var user))
        {
            ulong idleV = ((ulong)idle.dwHighDateTime << 32) | idle.dwLowDateTime;
            ulong sysV = (((ulong)kernel.dwHighDateTime << 32) | kernel.dwLowDateTime) + (((ulong)user.dwHighDateTime << 32) | user.dwLowDateTime);
            if (_lastSys > 0)
            {
                ulong idleDiff = idleV - _lastIdle;
                ulong sysDiff = sysV - _lastSys;
                if (sysDiff > 0) _cpu = (int)((sysDiff - idleDiff) * 100 / sysDiff);
            }
            _lastIdle = idleV; _lastSys = sysV;
        }

        lock (_histLock)
        {
            _cpuHistory.Add(_cpu);
            _ramHistory.Add(_ram);
            if (_cpuHistory.Count > 60) _cpuHistory.RemoveAt(0);
            if (_ramHistory.Count > 60) _ramHistory.RemoveAt(0);
        }
    }

    private static string[] BuildPcts()
    {
        var a = new string[101];
        for (int i = 0; i <= 100; i++) a[i] = i + "%";
        return a;
    }

    public WidgetHit HitTest(float x, float y, SKRect rect) => WidgetHit.None;
    public void OnLeftClick(string? action, float x, float y) { }
    public void OnRightClick() { }
    public void OnActivate(IPluginHost host) { }
    public void OnDeactivate() { }
}

/// <summary>系统资源详情页：CPU/RAM 实时曲线（类似任务管理器性能页）。</summary>
public sealed class HardwareDetailPage : IDetailPage
{
    private readonly HardwareWidget _widget;

    private static readonly SKPaint _labelPaint = new()
    {
        TextSize = 11f, IsAntialias = true,
        Typeface = SKTypeface.FromFamilyName("Microsoft YaHei UI", SKFontStyleWeight.Bold, SKFontStyleWidth.Normal, SKFontStyleSlant.Upright)
    };
    private static readonly SKPaint _fillPaint = new() { IsAntialias = true, Style = SKPaintStyle.Fill };
    private static readonly SKPaint _linePaint = new() { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1.5f };

    public HardwareDetailPage(HardwareWidget widget) { _widget = widget; }

    public float MeasureWidth() => 320f;
    public float MeasureHeight() => 130f;

    public void Draw(SKCanvas canvas, SKRect rect, WidgetFrame frame)
    {
        var (cpu, ram, count) = _widget.GetHistorySnapshot();

        float left = rect.Left + 12f;
        float right = rect.Right - 12f;
        float cpuTop = rect.Top + 8f;
        float cpuBottom = rect.Top + 60f;
        float ramTop = rect.Top + 68f;
        float ramBottom = rect.Top + 120f;

        DrawCurve(canvas, left, right, cpuTop, cpuBottom, cpu, count, $"CPU {_widget.CpuPercent}%", new SKColor(0, 140, 240), frame);
        DrawCurve(canvas, left, right, ramTop, ramBottom, ram, count, $"RAM {_widget.RamPercent}%", new SKColor(60, 200, 120), frame);
    }

    private void DrawCurve(SKCanvas canvas, float left, float right, float top, float bottom, float[] data, int count, string label, SKColor color, WidgetFrame frame)
    {
        _labelPaint.Color = frame.Theme.TextColor.WithAlpha(frame.Alpha);
        canvas.DrawText(label, left, top + 8f, _labelPaint);

        float chartW = right - left;
        float chartH = bottom - top;
        if (count < 2) return;

        float stepX = chartW / 59f;
        var line = new SKPath();
        var area = new SKPath();
        for (int i = 0; i < count; i++)
        {
            float x = right - (count - 1 - i) * stepX;
            float v = Math.Clamp(data[i], 0f, 100f);
            float y = bottom - (v / 100f) * chartH;
            if (i == 0) { line.MoveTo(x, y); area.MoveTo(x, bottom); area.LineTo(x, y); }
            else { line.LineTo(x, y); area.LineTo(x, y); }
        }
        area.LineTo(right, bottom);
        area.Close();

        _fillPaint.Color = color.WithAlpha(50);
        canvas.DrawPath(area, _fillPaint);
        _linePaint.Color = color.WithAlpha(frame.Alpha);
        canvas.DrawPath(line, _linePaint);
    }

    public WidgetHit HitTest(float x, float y, SKRect rect) => WidgetHit.None;
    public void OnAction(string? action, float x, float y) { }
}

/// <summary>媒体组件（委托给 MediaController 引擎：标题/艺术家/封面/播放状态 + 播放控制）。</summary>
public sealed class MediaWidget : IWidget
{
    private readonly MediaDetailPage _detailPage;

    private static readonly SKPaint _textPaint = new()
    {
        TextSize = 12.5f, IsAntialias = true,
        Typeface = SKTypeface.FromFamilyName("Microsoft YaHei UI", SKFontStyleWeight.SemiBold, SKFontStyleWidth.Normal, SKFontStyleSlant.Upright)
    };
    private static readonly SKPaint _iconPaint = new() { IsAntialias = true, Style = SKPaintStyle.Fill };
    private static readonly SKPaint _barPaint = new() { IsAntialias = true };
    private static readonly SKPath _playPath = CreatePlayPath();
    private static readonly SKPath _pausePath = CreatePausePath();
    private static readonly SKPath _prevPath = CreatePrevPath();
    private static readonly SKPath _nextPath = CreateNextPath();

    private static SKPath CreatePlayPath() { var p = new SKPath(); p.MoveTo(0, 0); p.LineTo(10, 6); p.LineTo(0, 12); p.Close(); return p; }
    private static SKPath CreatePausePath() { var p = new SKPath(); p.AddRect(new SKRect(0, 0, 3, 12)); p.AddRect(new SKRect(6, 0, 9, 12)); return p; }
    private static SKPath CreatePrevPath() { var p = new SKPath(); p.AddRect(new SKRect(0, 0, 2, 10)); p.MoveTo(8, 0); p.LineTo(2, 5); p.LineTo(8, 10); p.Close(); return p; }
    private static SKPath CreateNextPath() { var p = new SKPath(); p.MoveTo(0, 0); p.LineTo(6, 5); p.LineTo(0, 10); p.Close(); p.AddRect(new SKRect(6, 0, 8, 10)); return p; }

    public MediaWidget(IPluginHost host)
    {
        _detailPage = new MediaDetailPage(this);
        // 每秒同步一次媒体状态 + 歌词进度，并回写平台层的 MediaActive 标志
        host.ScheduleRefresh(TimeSpan.FromSeconds(1), () =>
        {
            var c = MediaController.Instance;
            if (c == null) return;
            c.UpdateLyrics();
            c.UpdateBars();
            NotchPeninsula.Renderer.MediaActive = c.IsActive;
        });
    }

    private static MediaController? Ctl => MediaController.Instance;

    internal string Title => Ctl?.Title ?? "";
    internal string Artist => Ctl?.Artist ?? "";
    internal bool Playing => Ctl?.IsPlaying ?? false;
    internal SKBitmap? Thumbnail => Ctl?.Thumbnail;

    internal void TogglePlayPause() => Ctl?.TogglePlayPause();
    internal void Next() => Ctl?.Next();
    internal void Previous() => Ctl?.Previous();

    public string Id => "builtin.media";
    public string DisplayName => "媒体";
    public IDetailPage? DetailPage => _detailPage;

    public float MeasureWidth(float availableHeight)
    {
        if (Ctl?.IsActive != true) return 0f;
        return Math.Min(_textPaint.MeasureText(DisplayText()) + 32f + 124f, 480f); // 上限 480，防止超长标题撑爆
    }

    public void Draw(SKCanvas canvas, SKRect rect, WidgetFrame frame)
    {
        if (Ctl?.IsActive != true) return;
        NotchPeninsula.Renderer.MediaActive = true;
        _textPaint.Color = frame.Theme.TextColor.WithAlpha(frame.Alpha);
        canvas.DrawText(DisplayText(), rect.Left + 16f, rect.MidY + 5f, _textPaint);

        // 频谱柱（右对齐）
        var bars = Ctl?.Bars;
        if (bars != null)
        {
            float barWidth = 2f, spacing = 2.8f, maxH = 16f, totalBarWidth = 21.2f;
            float startX = rect.Right - totalBarWidth - 4f;
            _barPaint.Color = frame.Theme.TextColor.WithAlpha(frame.Alpha);
            for (int i = 0; i < 5; i++)
            {
                float h = Math.Max(2f, bars[i] * maxH);
                float y = rect.MidY - h / 2f;
                canvas.DrawRoundRect(new SKRect(startX + i * (barWidth + spacing), y, startX + i * (barWidth + spacing) + barWidth, y + h), 1.5f, 1.5f, _barPaint);
            }
        }

        if (frame.IsHovered)
        {
            float right = rect.Right;
            float cy = rect.MidY - 6f;
            DrawIcon(canvas, right - 84f, cy, _prevPath, frame);
            DrawIcon(canvas, right - 54f, cy, Playing ? _pausePath : _playPath, frame);
            DrawIcon(canvas, right - 24f, cy, _nextPath, frame);
        }
    }

    private void DrawIcon(SKCanvas canvas, float x, float y, SKPath path, WidgetFrame frame)
    {
        _iconPaint.Color = frame.Theme.TextColor.WithAlpha(frame.Alpha);
        canvas.Save();
        canvas.Translate(x, y);
        canvas.DrawPath(path, _iconPaint);
        canvas.Restore();
    }

    private string DisplayText()
    {
        string t = string.IsNullOrEmpty(Artist) ? Title : $"{Artist} - {Title}";
        return t.Length > 32 ? t[..32] + "…" : t;
    }

    public WidgetHit HitTest(float x, float y, SKRect rect)
    {
        if (Ctl?.IsActive != true) return WidgetHit.None;
        float right = rect.Right;
        if (x >= right - 90f && x <= right - 78f) return new WidgetHit("prev");
        if (x >= right - 60f && x <= right - 48f) return new WidgetHit("play");
        if (x >= right - 30f && x <= right - 18f) return new WidgetHit("next");
        return WidgetHit.None;
    }

    public void OnLeftClick(string? action, float x, float y)
    {
        switch (action)
        {
            case "prev": Ctl?.Previous(); break;
            case "play": Ctl?.TogglePlayPause(); break;
            case "next": Ctl?.Next(); break;
        }
    }
    public void OnRightClick() { }
    public void OnActivate(IPluginHost host) { }
    public void OnDeactivate() { }
}

/// <summary>媒体详情页：大封面 + 标题/艺术家 + 底部播放控制。</summary>
public sealed class MediaDetailPage : IDetailPage
{
    private readonly MediaWidget _widget;

    private static readonly SKPaint _titlePaint = new()
    {
        TextSize = 14.5f, IsAntialias = true,
        Typeface = SKTypeface.FromFamilyName("Microsoft YaHei UI", SKFontStyleWeight.Bold, SKFontStyleWidth.Normal, SKFontStyleSlant.Upright)
    };
    private static readonly SKPaint _bodyPaint = new()
    {
        TextSize = 12.5f, IsAntialias = true,
        Typeface = SKTypeface.FromFamilyName("Microsoft YaHei UI")
    };
    private static readonly SKPaint _iconPaint = new() { IsAntialias = true, Style = SKPaintStyle.Fill };
    private static readonly SKPaint _fallbackPaint = new() { Color = new SKColor(0, 120, 212), IsAntialias = true };
    private static readonly SKPaint _hoverPaint = new() { IsAntialias = true, Style = SKPaintStyle.Fill };

    private static readonly SKPath _playPath = CreatePlayPath();
    private static readonly SKPath _pausePath = CreatePausePath();
    private static readonly SKPath _prevPath = CreatePrevPath();
    private static readonly SKPath _nextPath = CreateNextPath();

    public MediaDetailPage(MediaWidget widget) { _widget = widget; }

    public float MeasureWidth() => 320f;
    public float MeasureHeight() => 130f;

    public void Draw(SKCanvas canvas, SKRect rect, WidgetFrame frame)
    {
        float coverSize = 50f, coverX = rect.Left + 20f, coverY = rect.Top + 20f;
        var thumb = _widget.Thumbnail;
        if (thumb != null)
        {
            var coverRect = new SKRect(coverX, coverY, coverX + coverSize, coverY + coverSize);
            canvas.DrawRoundRect(coverRect, 8f, 8f, _bodyPaint);
            canvas.Save();
            var clip = new SKPath();
            clip.AddRoundRect(coverRect, 8f, 8f);
            canvas.ClipPath(clip, SKClipOperation.Intersect, true);
            canvas.DrawBitmap(thumb, coverRect, new SKPaint { FilterQuality = SKFilterQuality.High });
            canvas.Restore();
        }
        else
        {
            canvas.DrawRoundRect(new SKRect(coverX, coverY, coverX + coverSize, coverY + coverSize), 8f, 8f, _fallbackPaint);
        }

        float textX = coverX + coverSize + 12f;
        _titlePaint.Color = frame.Theme.TextColor.WithAlpha(frame.Alpha);
        _bodyPaint.Color = frame.Theme.SubTextColor.WithAlpha(frame.Alpha);
        canvas.DrawText(_widget.Title, textX, coverY + 18f, _titlePaint);
        canvas.DrawText(_widget.Artist, textX, coverY + 42f, _bodyPaint);

        // 底部播放控制
        float btnY = rect.Top + rect.Height - 34f;
        float centerX = rect.Left + rect.Width / 2f;
        _iconPaint.Color = frame.Theme.TextColor.WithAlpha(frame.Alpha);
        DrawIcon(canvas, centerX - 60f, btnY, _prevPath, 1.6f);
        DrawIcon(canvas, centerX - 7f, btnY - 1.6f, _widget.Playing ? _pausePath : _playPath, 1.6f);
        DrawIcon(canvas, centerX + 45f, btnY, _nextPath, 1.6f);
    }

    private void DrawIcon(SKCanvas canvas, float x, float y, SKPath path, float scale)
    {
        canvas.Save();
        canvas.Translate(x, y);
        canvas.Scale(scale);
        canvas.DrawPath(path, _iconPaint);
        canvas.Restore();
    }

    public WidgetHit HitTest(float x, float y, SKRect rect)
    {
        float centerX = rect.Width / 2f;
        float btnY = rect.Height - 34f;
        if (y >= btnY - 12f && y <= btnY + 30f)
        {
            if (x >= centerX - 75f && x <= centerX - 34f) return new WidgetHit("prev");
            if (x >= centerX - 20f && x <= centerX + 22f) return new WidgetHit("play");
            if (x >= centerX + 32f && x <= centerX + 75f) return new WidgetHit("next");
        }
        return WidgetHit.None;
    }

    public void OnAction(string? action, float x, float y)
    {
        switch (action)
        {
            case "prev": _widget.Previous(); break;
            case "play": _widget.TogglePlayPause(); break;
            case "next": _widget.Next(); break;
        }
    }

    private static SKPath CreatePlayPath() { var p = new SKPath(); p.MoveTo(0, 0); p.LineTo(10, 6); p.LineTo(0, 12); p.Close(); return p; }
    private static SKPath CreatePausePath() { var p = new SKPath(); p.AddRect(new SKRect(0, 0, 3, 12)); p.AddRect(new SKRect(6, 0, 9, 12)); return p; }
    private static SKPath CreatePrevPath() { var p = new SKPath(); p.AddRect(new SKRect(0, 0, 2, 10)); p.MoveTo(8, 0); p.LineTo(2, 5); p.LineTo(8, 10); p.Close(); return p; }
    private static SKPath CreateNextPath() { var p = new SKPath(); p.MoveTo(0, 0); p.LineTo(6, 5); p.LineTo(0, 10); p.Close(); p.AddRect(new SKRect(6, 0, 8, 10)); return p; }
}
