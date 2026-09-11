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
        host.RegisterWidget(new ClockWidget());
        host.RegisterWidget(new HardwareWidget());
        host.RegisterWidget(new MediaWidget(host));
    }
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

    public string Id => "builtin.clock";
    public string DisplayName => "时间日期";
    public IDetailPage? DetailPage => null;

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

    private int _cpu, _ram;
    private float _smoothCpu, _smoothRam;
    private int _lastTick;
    private ulong _lastIdle, _lastSys;

    public string Id => "builtin.hardware";
    public string DisplayName => "系统资源";
    public IDetailPage? DetailPage => null;

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
        UpdateStats();
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

    private void UpdateStats()
    {
        int now = Environment.TickCount;
        if (now - _lastTick < 1000) return;
        _lastTick = now;

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

        _smoothCpu += (_cpu - _smoothCpu) * 0.2f;
        _smoothRam += (_ram - _smoothRam) * 0.2f;
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

/// <summary>媒体组件（自包含 SMTC 读取，基础版：标题/艺术家/播放状态）。</summary>
public sealed class MediaWidget : IWidget
{
    private GlobalSystemMediaTransportControlsSessionManager? _manager;
    private volatile bool _active;
    private volatile string _title = "";
    private volatile string _artist = "";

    private static readonly SKPaint _textPaint = new()
    {
        TextSize = 12.5f, IsAntialias = true,
        Typeface = SKTypeface.FromFamilyName("Microsoft YaHei UI", SKFontStyleWeight.SemiBold, SKFontStyleWidth.Normal, SKFontStyleSlant.Upright)
    };

    public MediaWidget(IPluginHost host)
    {
        _ = InitAsync(host);
    }

    private async Task InitAsync(IPluginHost host)
    {
        try
        {
            _manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            _manager.SessionsChanged += (s, e) => _ = RefreshAsync();
            await RefreshAsync();
            host.ScheduleRefresh(TimeSpan.FromSeconds(2), () => _ = RefreshAsync());
        }
        catch { }
    }

    private async Task RefreshAsync()
    {
        try
        {
            var session = _manager?.GetCurrentSession();
            if (session == null) { _active = false; _title = ""; _artist = ""; return; }
            var props = await session.TryGetMediaPropertiesAsync();
            _title = props?.Title ?? "";
            _artist = props?.Artist ?? "";
            _active = !string.IsNullOrEmpty(_title) || !string.IsNullOrEmpty(_artist);
        }
        catch { _active = false; }
    }

    public string Id => "builtin.media";
    public string DisplayName => "媒体";
    public IDetailPage? DetailPage => null;

    public float MeasureWidth(float availableHeight)
    {
        if (!_active) return 0f;
        return _textPaint.MeasureText(DisplayText()) + 32f;
    }

    public void Draw(SKCanvas canvas, SKRect rect, WidgetFrame frame)
    {
        if (!_active) return;
        _textPaint.Color = frame.Theme.TextColor.WithAlpha(frame.Alpha);
        canvas.DrawText(DisplayText(), rect.Left + 16f, rect.MidY + 5f, _textPaint);
    }

    private string DisplayText() => string.IsNullOrEmpty(_artist) ? _title : $"{_artist} - {_title}";

    public WidgetHit HitTest(float x, float y, SKRect rect) => WidgetHit.None;
    public void OnLeftClick(string? action, float x, float y) { }
    public void OnRightClick() { }
    public void OnActivate(IPluginHost host) { }
    public void OnDeactivate() { }
}
