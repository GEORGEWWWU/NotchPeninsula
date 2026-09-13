using NotchPeninsula.Plugins;
using SkiaSharp;
using ThuInfoLib;

namespace ThuCourse;

/// <summary>课表设置页：登录按钮 + 状态 + 刷新间隔。</summary>
public sealed class CourseSettingsPage : ISettingsPage, ICustomSettingsPage
{
    private readonly IPluginHost _host;
    private readonly ThuClient _client;
    private readonly Func<Task<bool>> _relogin;
    private bool _btnHover;

    public CourseSettingsPage(IPluginHost host, ThuClient client, Func<Task<bool>> relogin)
    {
        _host = host;
        _client = client;
        _relogin = relogin;
    }

    public string Title => "清华课程表";
    public IReadOnlyList<SettingControl> Controls { get; } = new SettingControl[]
    {
        new NumberSetting("RefreshInterval", "刷新间隔(分钟)", 5f, 60f, 5f, 5f),
    };

    public float MeasureHeight() => 76f;

    public void Draw(SKCanvas canvas, SKRect rect, RenderTheme theme)
    {
        var statusPaint = new SKPaint { Color = theme.SubTextColor, TextSize = 12f, IsAntialias = true, Typeface = SKTypeface.FromFamilyName("Microsoft YaHei UI") };
        string userId = _host.GetSetting("UserId", "");
        canvas.DrawText(userId == "" ? "未登录" : $"已登录：{userId}", rect.Left, rect.Top + 18f, statusPaint);

        var btn = new SKRect(rect.Left, rect.Top + 30f, rect.Left + 120f, rect.Top + 62f);
        var btnPaint = new SKPaint { Color = _btnHover ? new SKColor(0, 140, 240) : new SKColor(0, 120, 212), IsAntialias = true };
        canvas.DrawRoundRect(btn, 6, 6, btnPaint);
        var textPaint = new SKPaint { Color = SKColors.White, TextSize = 13f, IsAntialias = true, Typeface = SKTypeface.FromFamilyName("Microsoft YaHei UI") };
        canvas.DrawText("登录 / 重新登录", btn.Left + 12f, btn.Top + 22f, textPaint);
    }

    public void OnMouseDown(float x, float y)
    {
        if (x >= 0 && x <= 120f && y >= 30f && y <= 62f)
        {
            _ = new LoginWindow(_host, _client, _relogin);
        }
    }

    public void OnMouseMove(float x, float y) => _btnHover = x >= 0 && x <= 120f && y >= 30f && y <= 62f;
    public void OnMouseUp(float x, float y) { }
}

/// <summary>登录窗口：学号 + 密码 + 登录按钮，支持键盘输入与二次认证。</summary>
public sealed class LoginWindow
{
    private readonly IPluginHost _host;
    private readonly ThuClient _client;
    private readonly Func<Task<bool>> _relogin;
    private readonly IPluginWindow _win;

    private string _userId = "";
    private string _password = "";
    private string _code = "";
    private int _focus; // 0=学号, 1=密码, 2=验证码
    private int _mode;   // 0=登录, 1=选验证方式, 2=输验证码
    private volatile bool _hasWeChat, _hasPhone, _hasTotp;
    private TaskCompletionSource<string?>? _methodTcs;
    private TaskCompletionSource<string?>? _codeTcs;
    private bool _loginHover;
    private string _status = "";

    public LoginWindow(IPluginHost host, ThuClient client, Func<Task<bool>> relogin)
    {
        _host = host;
        _client = client;
        _relogin = relogin;
        _userId = host.GetSetting("UserId", "");

        _win = host.CreateWindow("清华课程表登录", 320, 220);
        _win.SetDraw(Draw);
        _win.SetMouse(OnDown, OnMove, null);
        _win.SetKey(OnKey);

        // 二次认证回调
        _client.TwoFactorMethodSelector = async (hasWeChat, phone, hasTotp) =>
        {
            _mode = 1;
            _hasWeChat = hasWeChat;
            _hasPhone = phone != null;
            _hasTotp = hasTotp;
            _methodTcs = new TaskCompletionSource<string?>();
            _win.RequestRedraw();
            return await _methodTcs.Task;
        };
        _client.TwoFactorCodeProvider = async () =>
        {
            _mode = 2;
            _code = "";
            _focus = 2;
            _codeTcs = new TaskCompletionSource<string?>();
            _win.RequestRedraw();
            return await _codeTcs.Task;
        };
    }

    private void Draw(SKCanvas canvas, int w, int h)
    {
        var label = new SKPaint { Color = SKColors.White, TextSize = 13f, IsAntialias = true, Typeface = SKTypeface.FromFamilyName("Microsoft YaHei UI") };
        var text = new SKPaint { Color = SKColors.Black, TextSize = 14f, IsAntialias = true, Typeface = SKTypeface.FromFamilyName("Microsoft YaHei UI") };
        var boxFill = new SKPaint { Color = SKColors.White, IsAntialias = true };
        var boxFocus = new SKPaint { Color = new SKColor(0, 140, 240), IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1.5f };
        var btn = new SKPaint { Color = _loginHover ? new SKColor(0, 140, 240) : new SKColor(0, 120, 212), IsAntialias = true };
        var sub = new SKPaint { Color = new SKColor(200, 200, 200), TextSize = 12f, IsAntialias = true, Typeface = SKTypeface.FromFamilyName("Microsoft YaHei UI") };

        if (_mode == 0)
        {
            canvas.DrawText("清华课程表登录", 20, 32, label);

            canvas.DrawText("学号", 20, 62, label);
            var idBox = new SKRect(80, 44, 300, 76);
            canvas.DrawRoundRect(idBox, 4, 4, boxFill);
            if (_focus == 0) canvas.DrawRoundRect(idBox, 4, 4, boxFocus);
            canvas.DrawText(_userId, 88, 67, text);

            canvas.DrawText("密码", 20, 102, label);
            var pwBox = new SKRect(80, 84, 300, 116);
            canvas.DrawRoundRect(pwBox, 4, 4, boxFill);
            if (_focus == 1) canvas.DrawRoundRect(pwBox, 4, 4, boxFocus);
            canvas.DrawText(new string('*', _password.Length), 88, 107, text);

            var loginBtn = new SKRect(80, 132, 300, 164);
            canvas.DrawRoundRect(loginBtn, 6, 6, btn);
            canvas.DrawText("登录", 80 + 110, 154, label);
        }
        else if (_mode == 1)
        {
            canvas.DrawText("请选择二次认证方式", 20, 40, label);
            float y = 70f;
            if (_hasWeChat) { DrawMethodBtn(canvas, "微信验证", y, btn, label); y += 46f; }
            if (_hasPhone) { DrawMethodBtn(canvas, "短信验证", y, btn, label); y += 46f; }
            if (_hasTotp) { DrawMethodBtn(canvas, "验证器(TOTP)", y, btn, label); y += 46f; }
        }
        else
        {
            canvas.DrawText("输入验证码", 20, 62, label);
            var codeBox = new SKRect(80, 44, 300, 76);
            canvas.DrawRoundRect(codeBox, 4, 4, boxFill);
            if (_focus == 2) canvas.DrawRoundRect(codeBox, 4, 4, boxFocus);
            canvas.DrawText(_code, 88, 67, text);

            var okBtn = new SKRect(80, 100, 300, 132);
            canvas.DrawRoundRect(okBtn, 6, 6, btn);
            canvas.DrawText("确认", 80 + 100, 122, label);
        }

        if (_status != "") canvas.DrawText(_status, 20, 200, sub);
    }

    private void DrawMethodBtn(SKCanvas canvas, string name, float y, SKPaint btn, SKPaint label)
    {
        var rect = new SKRect(80, y, 300, y + 36);
        canvas.DrawRoundRect(rect, 6, 6, btn);
        canvas.DrawText(name, rect.Left + 12, rect.Top + 24, label);
    }

    private void OnDown(float x, float y)
    {
        if (_mode == 0)
        {
            if (y >= 44 && y <= 76) { _focus = 0; _win.RequestRedraw(); }
            else if (y >= 84 && y <= 116) { _focus = 1; _win.RequestRedraw(); }
            else if (y >= 132 && y <= 164) DoLogin();
        }
        else if (_mode == 1)
        {
            string? method = null;
            float yy = 70f;
            if (_hasWeChat) { if (y >= yy && y <= yy + 36) method = "wechat"; yy += 46f; }
            if (_hasPhone) { if (y >= yy && y <= yy + 36) method = "mobile"; yy += 46f; }
            if (_hasTotp) { if (y >= yy && y <= yy + 36) method = "totp"; }
            if (method != null) _methodTcs?.TrySetResult(method);
        }
        else
        {
            if (y >= 44 && y <= 76) { _focus = 2; _win.RequestRedraw(); }
            else if (y >= 100 && y <= 132) _codeTcs?.TrySetResult(_code);
        }
    }

    private void OnMove(float x, float y)
    {
        _loginHover = (_mode == 0 && y >= 132 && y <= 164) || (_mode == 2 && y >= 100 && y <= 132);
    }

    private void OnKey(char c)
    {
        if (c == '\b')
        {
            if (_focus == 0 && _userId.Length > 0) _userId = _userId[..^1];
            else if (_focus == 1 && _password.Length > 0) _password = _password[..^1];
            else if (_focus == 2 && _code.Length > 0) _code = _code[..^1];
        }
        else if (c >= ' ' && c != '\r')
        {
            if (_focus == 0 && _userId.Length < 20) _userId += c;
            else if (_focus == 1 && _password.Length < 40) _password += c;
            else if (_focus == 2 && _code.Length < 12) _code += c;
        }
        _win.RequestRedraw();
    }

    private void DoLogin()
    {
        if (_userId == "" || _password == "") { _status = "请输入学号和密码"; _win.RequestRedraw(); return; }
        _status = "登录中...";
        _win.RequestRedraw();
        _ = Task.Run(async () =>
        {
            try
            {
                // 先持久化，再交给 ReloginAsync 做「登录 + 拉取课表」，避免重复登录触发多次二次认证。
                _host.SetSetting("UserId", _userId);
                _host.SetSetting("Password", _password);
                if (await _relogin())
                    _win.Close();
                else
                {
                    _mode = 0;
                    _status = "登录失败，请检查学号密码";
                    _win.RequestRedraw();
                }
            }
            catch (Exception ex)
            {
                _mode = 0;
                _status = ex.Message;
                NotchPeninsula.Logger.Error("[课表] 登录窗口登录失败", ex);
                _win.RequestRedraw();
            }
        });
    }
}
