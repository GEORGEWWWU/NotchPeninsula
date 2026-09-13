using NotchPeninsula.Plugins;
using SkiaSharp;
using ThuInfoLib;

namespace ThuCourse;

/// <summary>课表插件：登录清华 INFO，展示下一节课 + 今日课表。</summary>
public sealed class CoursePlugin : INotchPlugin
{
    public string Id => "course";
    public string DisplayName => "清华课程表";
    public string Version => "1.0.0";

    public void Initialize(IPluginHost host)
    {
        var client = new ThuClient
        {
            Fingerprint = EnsureFingerprint(host),
            TrustFingerprintSelector = () => Task.FromResult(true),
            TrustFingerprintNameProvider = () => Task.FromResult("清华课程表"),
            DebugLog = msg => NotchPeninsula.Logger.Info(msg),
        };
        var widget = new CourseWidget(host, client);
        host.RegisterWidget(widget);
        host.RegisterSettingsPage(new CourseSettingsPage(host, client, widget.ReloginAsync));
    }

    /// <summary>获取（并持久化）本机唯一设备指纹，用于「标记常用设备」。移植自 thu-info-app 的 uuidv4。</summary>
    private static string EnsureFingerprint(IPluginHost host)
    {
        string fp = host.GetSetting("Fingerprint", "");
        if (fp.Length != 32)
        {
            fp = Guid.NewGuid().ToString("N");
            host.SetSetting("Fingerprint", fp);
        }
        return fp;
    }
}

/// <summary>课表组件：显示下一节课。</summary>
public sealed class CourseWidget : IWidget
{
    private readonly IPluginHost _host;
    private readonly ThuClient _client;
    private readonly CourseDetailPage _detailPage;
    private volatile List<Schedule> _schedules = new();
    private volatile int _state = 0; // 0=未登录, 1=加载中, 2=已就绪
    private volatile string _error = "";

    private static readonly SKPaint _paint = new()
    {
        Color = SKColors.White, TextSize = 12.5f, IsAntialias = true,
        Typeface = SKTypeface.FromFamilyName("Microsoft YaHei UI", SKFontStyleWeight.SemiBold, SKFontStyleWidth.Normal, SKFontStyleSlant.Upright)
    };

    public CourseWidget(IPluginHost host, ThuClient client)
    {
        _host = host;
        _client = client;
        _detailPage = new CourseDetailPage(this);
        _ = InitAsync(host);
    }

    public string Id => "course.next";
    public string DisplayName => "清华课程表";
    public IDetailPage? DetailPage => _detailPage;

    /// <summary>今日课表（按上课时间排序）。</summary>
    public List<(string Name, string Location, DateTime Begin, DateTime End)> TodaySchedules()
    {
        var now = DateTime.Now;
        return _schedules
            .SelectMany(s => s.ActiveTime.Where(t => t.BeginTime.Date == now.Date).Select(t => (s.Name, s.Location, t.BeginTime, t.EndTime)))
            .OrderBy(x => x.BeginTime)
            .ToList();
    }

    /// <summary>某一天的课表（按上课时间排序）。</summary>
    public List<(string Name, string Location, DateTime Begin, DateTime End)> DaySchedules(DateTime day)
    {
        return _schedules
            .SelectMany(s => s.ActiveTime.Where(t => t.BeginTime.Date == day.Date).Select(t => (s.Name, s.Location, t.BeginTime, t.EndTime)))
            .OrderBy(x => x.BeginTime)
            .ToList();
    }

    private async Task InitAsync(IPluginHost host)
    {
        string userId = host.GetSetting("UserId", "");
        string password = host.GetSetting("Password", "");
        if (userId == "" || password == "") { _state = 0; return; }

        await LoginAndFetchAsync(userId, password);

        double interval = double.TryParse(host.GetSetting("RefreshInterval", "5"), out var iv) ? Math.Clamp(iv, 5, 60) : 5;
        host.ScheduleRefresh(TimeSpan.FromMinutes(interval), () => _ = RefreshAsync());
    }

    /// <summary>登录 + 拉取课表。返回登录是否成功（拉取失败不视为登录失败）。</summary>
    public async Task<bool> ReloginAsync()
    {
        string userId = _host.GetSetting("UserId", "");
        string password = _host.GetSetting("Password", "");
        if (userId == "" || password == "") { _state = 0; return false; }
        return await LoginAndFetchAsync(userId, password);
    }

    private async Task<bool> LoginAndFetchAsync(string userId, string password)
    {
        _state = 1;
        _error = "";
        try
        {
            await _client.LoginAsync(userId, password);
        }
        catch (Exception ex)
        {
            _state = 0;
            _error = ex.Message;
            NotchPeninsula.Logger.Error($"[课表] 登录失败: {ex.Message}", ex);
            return false;
        }

        // 登录已成功；拉取课表失败不阻断关闭登录窗（下次刷新/重启会重试）。
        try
        {
            await FetchAsync();
            _state = 2;
        }
        catch (Exception ex)
        {
            _state = 0;
            _error = ex.Message;
            NotchPeninsula.Logger.Error($"[课表] 拉取课表失败: {ex.Message}", ex);
        }
        return true;
    }

    private async Task RefreshAsync()
    {
        try { await FetchAsync(); _state = 2; }
        catch { /* 保持旧数据 */ }
    }

    private async Task FetchAsync()
    {
        var (schedule, calendar) = await _client.GetScheduleAsync();
        _schedules = schedule;
        NotchPeninsula.Logger.Info($"[课表] 学期={calendar.SemesterName} 首周周一={calendar.FirstDay} 周数={calendar.WeekCount}");
        var times = schedule.SelectMany(s => s.ActiveTime).ToList();
        if (times.Count == 0)
            NotchPeninsula.Logger.Warn("[课表] 拉取到 0 个时间片（课表为空）");
        else
            NotchPeninsula.Logger.Info($"[课表] 拉取到 {schedule.Count} 门课 / {times.Count} 个时间片，范围 {times.Min(t => t.BeginTime):yyyy-MM-dd} ~ {times.Max(t => t.BeginTime):yyyy-MM-dd}");
    }

    public float MeasureWidth(float availableHeight) => _paint.MeasureText(DisplayText()) + 32f;

    public void Draw(SKCanvas canvas, SKRect rect, WidgetFrame frame)
    {
        _paint.Color = frame.Theme.TextColor.WithAlpha(frame.Alpha);
        canvas.DrawText(DisplayText(), rect.Left + 16f, rect.MidY + 5f, _paint);
    }

    private string DisplayText()
    {
        if (_state == 0) return _error.Contains("二次认证") || _error == "" ? "课表未登录" : "课表错误";
        if (_state == 1) return "课表加载中";

        var now = DateTime.Now;
        var today = _schedules
            .SelectMany(s => s.ActiveTime.Where(t => t.BeginTime.Date == now.Date).Select(t => (s.Name, t.BeginTime, t.EndTime)))
            .OrderBy(x => x.BeginTime)
            .ToList();

        if (today.Count == 0) return "今天没有课程";
        foreach (var (name, begin, end) in today)
            if (end > now)
                return $"{name} {begin:HH:mm}-{end:HH:mm}";
        return "今天课程已结束";
    }

    public WidgetHit HitTest(float x, float y, SKRect rect) => WidgetHit.None;
    public void OnLeftClick(string? action, float x, float y) { }
    public void OnRightClick() { }
    public void OnActivate(IPluginHost host) { }
    public void OnDeactivate() { }
}

/// <summary>课表详情页：日期选择器（点击上方日期切换查看某天课表）。</summary>
public sealed class CourseDetailPage : IDetailPage
{
    private readonly CourseWidget _widget;
    private volatile int _selectedOffset = 0; // 0=今天, 1=明天 ... 6=未来第 6 天

    private static readonly SKPaint _titlePaint = new()
    {
        Color = SKColors.White, TextSize = 13f, IsAntialias = true,
        Typeface = SKTypeface.FromFamilyName("Microsoft YaHei UI", SKFontStyleWeight.Bold, SKFontStyleWidth.Normal, SKFontStyleSlant.Upright)
    };
    private static readonly SKPaint _chipPaint = new()
    {
        Color = SKColors.White, TextSize = 11.5f, IsAntialias = true,
        Typeface = SKTypeface.FromFamilyName("Microsoft YaHei UI", SKFontStyleWeight.Medium, SKFontStyleWidth.Normal, SKFontStyleSlant.Upright)
    };
    private static readonly SKPaint _chipSelPaint = new()
    {
        Color = SKColors.White, TextSize = 11.5f, IsAntialias = true,
        Typeface = SKTypeface.FromFamilyName("Microsoft YaHei UI", SKFontStyleWeight.Bold, SKFontStyleWidth.Normal, SKFontStyleSlant.Upright)
    };
    private static readonly SKPaint _chipBgPaint = new() { IsAntialias = true };
    private static readonly SKPaint _bodyPaint = new()
    {
        Color = new SKColor(200, 200, 200), TextSize = 12f, IsAntialias = true,
        Typeface = SKTypeface.FromFamilyName("Microsoft YaHei UI")
    };

    private static readonly string[] WeekCn = ["周一", "周二", "周三", "周四", "周五", "周六", "周日"];

    private const float ChipX = 20f, ChipY = 40f, ChipW = 42f, ChipH = 22f, ChipGap = 2f;

    public CourseDetailPage(CourseWidget widget) { _widget = widget; }

    public float MeasureWidth() => 340f;

    public float MeasureHeight()
    {
        int n = _widget.DaySchedules(DateTime.Today.AddDays(_selectedOffset)).Count;
        return Math.Clamp(78f + n * 18f, 110f, 200f);
    }

    public void Draw(SKCanvas canvas, SKRect rect, WidgetFrame frame)
    {
        _titlePaint.Color = frame.Theme.TextColor.WithAlpha(frame.Alpha);
        _chipPaint.Color = frame.Theme.SubTextColor.WithAlpha(frame.Alpha);
        _chipSelPaint.Color = frame.Theme.TextColor.WithAlpha(frame.Alpha);
        _bodyPaint.Color = frame.Theme.SubTextColor.WithAlpha(frame.Alpha);

        canvas.DrawText("课程安排", rect.Left + 20f, rect.Top + 22f, _titlePaint);

        var today = DateTime.Today;
        for (int i = 0; i < 7; i++)
        {
            float x = rect.Left + ChipX + i * (ChipW + ChipGap);
            bool sel = i == _selectedOffset;
            if (sel)
            {
                _chipBgPaint.Color = new SKColor(0, 120, 212).WithAlpha(frame.Alpha);
                canvas.DrawRoundRect(new SKRect(x, rect.Top + ChipY, x + ChipW, rect.Top + ChipY + ChipH), 5, 5, _chipBgPaint);
            }
            string label = ChipLabel(today.AddDays(i));
            var paint = sel ? _chipSelPaint : _chipPaint;
            canvas.DrawText(label, x + (ChipW - paint.MeasureText(label)) / 2f, rect.Top + ChipY + 15f, paint);
        }

        var courses = _widget.DaySchedules(today.AddDays(_selectedOffset));
        float y = rect.Top + 78f;
        if (courses.Count == 0)
        {
            canvas.DrawText("这天没有课", rect.Left + 20f, y, _bodyPaint);
            return;
        }
        foreach (var (name, location, begin, end) in courses)
        {
            canvas.DrawText($"{begin:HH:mm}-{end:HH:mm}  {name}  {location}", rect.Left + 20f, y, _bodyPaint);
            y += 18f;
        }
    }

    private static string ChipLabel(DateTime day)
    {
        var today = DateTime.Today;
        if (day == today) return "今天";
        if (day == today.AddDays(1)) return "明天";
        return WeekCn[((int)day.DayOfWeek + 6) % 7];
    }

    public WidgetHit HitTest(float x, float y, SKRect rect)
    {
        // 命中顶部日期条（坐标与 Draw 中的 rect.Top + offset 一致）
        if (y >= ChipY && y <= ChipY + ChipH)
        {
            int i = (int)((x - ChipX) / (ChipW + ChipGap));
            if (i >= 0 && i < 7)
            {
                float x0 = ChipX + i * (ChipW + ChipGap);
                if (x >= x0 && x <= x0 + ChipW)
                    return new WidgetHit($"day:{i}");
            }
        }
        return WidgetHit.None;
    }

    public void OnAction(string? action, float x, float y)
    {
        if (action != null && action.StartsWith("day:"))
            _selectedOffset = Math.Clamp(int.Parse(action[4..]), 0, 6);
    }
}
