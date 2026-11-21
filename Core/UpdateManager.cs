using System.Diagnostics;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.IO;
using SkiaSharp;
using System.Reflection;
using Microsoft.Win32;

namespace NotchPeninsula
{
    public class UpdateManager
    {
        private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(8) };

        private const string SkipVersionValueName = "SkippedUpdateVersion";

        public static string GetSkippedVersion()
        {
            try
            {
                using var key = Registry.CurrentUser.CreateSubKey(@"SOFTWARE\NotchPeninsula");
                return key?.GetValue(SkipVersionValueName, "") as string ?? "";
            }
            catch (Exception ex)
            {
                Logger.Error("读取已跳过的更新版本失败", ex);
                return "";
            }
        }

        public static void MarkVersionSkipped(string version)
        {
            if (string.IsNullOrEmpty(version)) return;
            Logger.Info($"[更新检测] 用户选择不再提醒 {version}");
            Program.SaveSetting(SkipVersionValueName, version);
        }

        public static void StartSilentCheck() => StartCheck(manual: false);

        // 手动检测同一时刻只允许一次：连点两下不该发两次请求、弹两个窗。
        private static int _manualCheckRunning;

        // GitHub 发布列表；第二条是用户提供的 EdgeOne 反代，主地址不通时兜底。
        private const string ReleasesApiUrl =
            "https://api.github.com/repos/GEORGEWWWU/NotchPeninsula/releases";
        private const string ReleasesApiUrlMirror = "https://edgeone.gh-proxy.org/" + ReleasesApiUrl;

        // 有新版本时统一跳这里下载（官方下载页，不走 GitHub release 页面）。
        internal const string DownloadPageUrl = "https://dlnps.georgewu.top";

        // 手动检测失败时的兜底入口：直接去 GitHub 发布页自己挑包。
        internal const string GitHubReleasesUrl = "https://github.com/GEORGEWWWU/NotchPeninsula/releases";

        /// <summary>
        /// 用户点「检测更新」触发。返回 false 表示上一次手动检测还没结束。
        /// 点下去立刻弹一个「正在检测」的窗，结论直接写回这个窗；不再在按钮下面挂小字。
        /// </summary>
        public static bool StartManualCheck()
        {
            if (Interlocked.CompareExchange(ref _manualCheckRunning, 1, 0) != 0) return false;
            StartCheck(manual: true, popupTask: ShowCheckingPopup());
            return true;
        }

        // 检测弹窗要立刻出现：建窗放独立 STA 线程，主线程不等它；结果回来时直接改写这个窗。
        private static Task<NotifyWindow?> ShowCheckingPopup()
        {
            var ready = new TaskCompletionSource<NotifyWindow?>(TaskCreationOptions.RunContinuationsAsynchronously);

            var thread = new Thread(() =>
            {
                try
                {
                    var win = NotifyWindow.CreateChecking();
                    ready.TrySetResult(win);
                    win.Run();
                }
                catch (Exception ex)
                {
                    Logger.Error("[更新检测] 打开检测弹窗失败", ex);
                    ready.TrySetResult(null);
                }
            });

            thread.SetApartmentState(ApartmentState.STA);
            thread.IsBackground = true;
            thread.Start();

            return ready.Task;
        }

        // 主地址超时/被墙时自动换反代重试一次。
        private static async Task<string> FetchReleasesJsonAsync()
        {
            Exception? firstError = null;

            foreach (var url in new[] { ReleasesApiUrl, ReleasesApiUrlMirror })
            {
                try
                {
                    var json = await _http.GetStringAsync(url);
                    Logger.Info($"[更新检测] 已从 {new Uri(url).Host} 获取发布列表");
                    return json;
                }
                catch (Exception ex)
                {
                    firstError ??= ex;
                    Logger.Info($"[更新检测] 请求 {new Uri(url).Host} 失败：{ex.Message}");
                }
            }

            throw firstError ?? new InvalidOperationException("没有可用的更新地址");
        }

        // 版本号只认发布资源里的安装包文件名（NotchPeninsula.v.2.2.5.exe 这种），
        // tag 只是兜底 —— 发布页里最高的资源版本才是「最新」。
        private static readonly Regex VersionInName = new(@"(\d+(?:\.\d+){1,3})", RegexOptions.Compiled);

        private static Version? ExtractVersion(string text)
        {
            var m = VersionInName.Match(text ?? "");
            return m.Success && Version.TryParse(m.Groups[1].Value, out var v) ? v : null;
        }

        /// <summary>
        /// 从 /releases 列表（也兼容单个 release 对象）里挑出最高的安装包版本。
        /// arm64-beta 这类测试包跳过，避免把测试版当正式版推给用户。
        /// </summary>
        private static (Version? Version, string Text) ParseLatestRelease(string json)
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            Version? best = null;
            string bestText = "";

            var releases = root.ValueKind == JsonValueKind.Array
                ? root.EnumerateArray().ToArray()
                : new[] { root };

            foreach (var release in releases)
            {
                if (release.TryGetProperty("draft", out var draft) && draft.ValueKind == JsonValueKind.True) continue;

                if (release.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
                {
                    foreach (var asset in assets.EnumerateArray())
                    {
                        string name = asset.TryGetProperty("name", out var nameEl) && nameEl.ValueKind == JsonValueKind.String
                            ? nameEl.GetString() ?? "" : "";
                        if (name.Length == 0) continue;
                        if (name.Contains("beta", StringComparison.OrdinalIgnoreCase)) continue;

                        var v = ExtractVersion(name);
                        if (v != null && (best == null || v > best))
                        {
                            best = v;
                            bestText = v.ToString(3);
                        }
                    }
                }

                // 老发布可能没有资源，只有 tag：NPS-v2.2.3
                if (release.TryGetProperty("tag_name", out var tagEl) && tagEl.ValueKind == JsonValueKind.String)
                {
                    var v = ExtractVersion(tagEl.GetString() ?? "");
                    if (v != null && (best == null || v > best))
                    {
                        best = v;
                        bestText = v.ToString(3);
                    }
                }
            }

            return (best, bestText);
        }

        private static void StartCheck(bool manual, Task<NotifyWindow?>? popupTask = null)
        {
            Task.Run(async () =>
            {
                // 检测窗可能还没建好（或者没建出来），先拿到它的引用。
                NotifyWindow? popup = null;
                if (popupTask != null)
                {
                    try { popup = await popupTask; } catch { }
                }

                // 结论统一走这里：检测窗在就就地改写，没开出来才另起一个。
                // 结论卡的那个按钮一律是超链接：已最新 → 下载站；失败 → GitHub 发布页。
                void Conclude(string title, string message, string actionLabel, string actionUrl)
                {
                    if (popup != null) popup.SetResult(title, message, actionLabel, actionUrl);
                    else ShowNotice(title, message, actionLabel, actionUrl);
                }

                try
                {
                    if (!_http.DefaultRequestHeaders.Contains("User-Agent"))
                    {
                        _http.DefaultRequestHeaders.Add("User-Agent", "NotchPeninsula-UpdateChecker");
                        _http.DefaultRequestHeaders.Add("Accept", "application/vnd.github.v3+json");
                    }

                    Logger.Info("[更新检测] 正在请求最新版本信息...");

                    var response = await FetchReleasesJsonAsync();

                    var (latestVersion, latestVersionStr) = ParseLatestRelease(response);
                    var currentVersion = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(1, 0, 0);

                    if (latestVersion == null)
                    {
                        Logger.Info("[更新检测] 发布列表里没有可识别的安装包版本");
                        if (manual) Conclude("检查更新失败", "发布页里没有可识别的安装包版本，请稍后重试。",
                            "手动前往GitHub下载", GitHubReleasesUrl);
                        return;
                    }

                    Logger.Info($"[更新检测] 发布页最新安装包 = v{latestVersionStr}，当前 = v{currentVersion.ToString(3)}");

                    if (latestVersion > currentVersion)
                    {
                        if (!manual)
                        {
                            var skipped = GetSkippedVersion();
                            if (!string.IsNullOrEmpty(skipped) && skipped == latestVersionStr)
                            {
                                Logger.Info($"[更新检测] 用户已选择不再提醒 {latestVersionStr}，本次跳过弹窗");
                                return;
                            }
                        }

                        Logger.Info("[更新检测] 发现新版本！拉起全局弹窗...");
                        // 「发现新版本」是更高、带三个按钮的窗，换一个窗显示；先把检测窗收掉。
                        popup?.Close();
                        ShowUpdateNotice(latestVersionStr);
                    }
                    else if (manual)
                    {
                        Logger.Info($"[更新检测] 已是最新版本（当前 {currentVersion}，远端 {latestVersionStr}）");
                        Conclude("已是最新版本",
                            $"当前版本 v{currentVersion.ToString(3)} 已是最新版本，无需更新。",
                            "前往下载站", DownloadPageUrl);
                    }
                }
                catch (Exception ex)
                {
                    // 静默检测失败不打扰用户；手动检测必须给结论，否则就是「点了没反应」
                    Logger.Info($"[更新检测] 请求失败：{ex.Message}");
                    if (manual) Conclude("检查更新失败", "无法连接到 GitHub，请检查网络后重试。",
                        "手动前往GitHub下载", GitHubReleasesUrl);
                }
                finally
                {
                    if (manual) Interlocked.Exchange(ref _manualCheckRunning, 0);
                }
            });
        }

        // 「发现新版本」弹窗：独立顶层窗，必须在 STA 线程上跑自己的消息循环。
        private static void ShowUpdateNotice(string version)
        {
            var notifyThread = new Thread(() =>
            {
                try
                {
                    var notifyWin = NotifyWindow.CreateUpdateNotice(version);
                    notifyWin.Run();
                }
                catch (Exception ex)
                {
                    Logger.Error("[更新检测] 弹窗渲染失败", ex);
                }
            });

            notifyThread.SetApartmentState(ApartmentState.STA);
            notifyThread.IsBackground = true;
            notifyThread.Start();
        }

        // 同款样式的小弹窗，只用来给手动检测的结论（已最新 / 检测失败）。
        private static void ShowNotice(string title, string message, string actionLabel, string actionUrl)
        {
            var notifyThread = new Thread(() =>
            {
                try
                {
                    var notifyWin = NotifyWindow.CreateResultNotice(title, message, actionLabel, actionUrl);
                    notifyWin.Run();
                }
                catch (Exception ex)
                {
                    Logger.Error("[更新检测] 结果弹窗渲染失败", ex);
                }
            });

            notifyThread.SetApartmentState(ApartmentState.STA);
            notifyThread.IsBackground = true;
            notifyThread.Start();
        }
    }

    public class NotifyWindow
    {
        private readonly IntPtr _hwnd;
        private readonly string _version;   // 归一化版本号（形如 2.2.7），弹窗显示与「不再提醒」都用它

        // ⚠️ 窗口类只注册一次，而交给系统的 WndProc 委托必须活到进程结束。
        //    以前这里给每个弹窗各存一个实例委托：第一次弹窗注册完类后对象被回收，
        //    第二次弹窗复用同一个已注册的类 ⇒ 系统回调到已 GC 的委托 ⇒ 进程 FailFast。
        //    改成静态委托 + hwnd→实例表，实例只负责状态，不再承担委托的生存期。
        private static readonly Win32.WndProc _staticWndProc = StaticWndProc;
        private static readonly Dictionary<IntPtr, NotifyWindow> _liveWindows = new();
        private static readonly object _liveWindowsLock = new();
        private static bool _classRegistered;
        private static IntPtr _classInstance;

        private const string ClassName = "NpsNotifyClass";

        private const int WIDTH = 460;
        private const int NOTICE_HEIGHT = 300;   // 检测中 / 结论 / 发现新版本：三种卡片统一这个高度
        private const int HEIGHT = NOTICE_HEIGHT;

        // 结论弹窗（已最新 / 检测失败 / 正在检测）与「发现新版本」共用同一套外观，
        // 只差高度、正文排版和按钮数量 —— 用户点完「检测更新」看到的是同一种窗。
        private readonly bool _notice;
        private string _noticeTitle;
        private readonly int _logicalHeight;

        // 「正在检测」阶段：没有结论，按钮画成「取消」，结论回来后原地改写成结果。
        private bool _checking;
        private readonly object _resultLock = new();
        private string? _pendingTitle;
        private string? _pendingMessage;

        // 结论卡唯一的那个按钮：文案 + 点击后要打开的链接（null = 只关窗，比如检测中的「取消」）。
        // 已最新 → 前往下载站；查不到 → 手动前往GitHub下载。
        private string _actionLabel = "取消";
        private string? _actionUrl;
        private string? _pendingActionLabel;
        private string? _pendingActionUrl;

        // 自定义消息：结果写回窗口 / 请求关窗。必须在窗口线程上处理，不能跨线程碰控件状态。
        private const uint WM_APP_SET_RESULT = 0x8000 + 0x11;
        private const uint WM_APP_CLOSE = 0x8000 + 0x12;

        // 窗口已销毁后不再往这个句柄投消息（句柄会被系统复用）。
        private volatile bool _closed;

        private int _scaledW, _scaledH;
        private float _dpiScale;

        // UI 交互状态
        private int _hoveredButton = -1;    // 更新弹窗：0=前往下载 1=不再提醒 2=取消；结论弹窗：0=好的；-1=无
        private bool _hoveredClose;         // 右上角关闭键悬停
        private readonly List<string> _wrappedLines = new();

        // 右上角关闭键：所有更新 / 结论 / 检测中弹窗共用同一个位置。
        // 往里收一点（距边 16px）：卡片圆角半径 10，贴边放的话叉的上半截会被圆角切掉。
        private static SKRect GetCloseRect() => new(WIDTH - 40f, 12f, WIDTH - 16f, 36f);

        // 底部按钮组几何（绘制与命中同源，改布局只动这里）
        private const float BTN_HEIGHT = 40f;
        private const float BTN_WIDTH = 120f;
        private const float BTN_GAP = 12f;
        private const int BTN_COUNT = 3;

        private int ButtonCount => _notice ? 1 : BTN_COUNT;
        private float ButtonY => _logicalHeight - 74f;

        // 结论卡的按钮文案是可变的（前往下载站 / 手动前往GitHub下载），宽度跟着文案走。
        private float NoticeButtonWidth =>
            Math.Max(BTN_WIDTH, _btnTextPaint.MeasureText(_actionLabel) + 36f);

        private SKRect GetButtonRect(int index)
        {
            int count = ButtonCount;
            float btnW = count == 1 ? NoticeButtonWidth : BTN_WIDTH;
            float total = btnW * count + BTN_GAP * (count - 1);
            float left = (WIDTH - total) / 2f + index * (btnW + BTN_GAP);
            return new SKRect(left, ButtonY, left + btnW, ButtonY + BTN_HEIGHT);
        }

        // 静态复用资源
        private static SKBitmap? _iconBitmap;
        private static readonly SKPaint _bgPaint = new() { Color = new SKColor(32, 32, 32), IsAntialias = true };
        private static readonly SKPaint _borderPaint = new() { Color = new SKColor(60, 60, 60), Style = SKPaintStyle.Stroke, StrokeWidth = 1f, IsAntialias = true };
        private static readonly SKPaint _dynamicFill = new() { IsAntialias = true };
        private static readonly SKPaint _hqSamplingOpts = new() { FilterQuality = SKFilterQuality.High, IsAntialias = true };

        // 字体画笔
        private static readonly SKPaint _title1Paint = new() { Color = SKColors.White, TextSize = 22f, IsAntialias = true, TextAlign = SKTextAlign.Center, Typeface = SKTypeface.FromFamilyName("Microsoft YaHei UI", SKFontStyleWeight.Bold, SKFontStyleWidth.Normal, SKFontStyleSlant.Upright) };
        private static readonly SKPaint _title2Paint = new() { Color = new SKColor(200, 200, 200), TextSize = 15f, IsAntialias = true, TextAlign = SKTextAlign.Center, Typeface = SKTypeface.FromFamilyName("Microsoft YaHei UI", SKFontStyleWeight.SemiBold, SKFontStyleWidth.Normal, SKFontStyleSlant.Upright) };
        private static readonly SKPaint _contentPaint = new() { Color = new SKColor(170, 170, 170), TextSize = 13.5f, IsAntialias = true, Typeface = SKTypeface.FromFamilyName("Microsoft YaHei UI") };
        private static readonly SKPaint _btnTextPaint = new() { Color = SKColors.White, TextSize = 14f, IsAntialias = true, TextAlign = SKTextAlign.Center, Typeface = SKTypeface.FromFamilyName("Microsoft YaHei UI") };
        private static readonly SKPaint _hintPaint = new() { Color = new SKColor(120, 120, 120), TextSize = 11.5f, IsAntialias = true, TextAlign = SKTextAlign.Center, Typeface = SKTypeface.FromFamilyName("Microsoft YaHei UI") };

        // 「发现新版本」弹窗：不再铺更新日志，图标 + 一行版本提示 + 三个按钮
        public static NotifyWindow CreateUpdateNotice(string version)
            => new(version, "", notice: false, noticeTitle: "", checking: false,
                   actionLabel: "", actionUrl: null);

        // 检测结论弹窗（已最新 / 检测失败）：同款外观，更矮，只有一个按钮（超链接）
        public static NotifyWindow CreateResultNotice(string title, string message, string actionLabel, string actionUrl)
            => new("", message, notice: true, noticeTitle: title, checking: false,
                   actionLabel: actionLabel, actionUrl: actionUrl);

        // 「正在检测更新」：点下按钮立刻弹出的那一版，结论会原地改写进来（见 SetResult）。
        public static NotifyWindow CreateChecking()
            => new("", "正在从 GitHub 获取最新版本信息，请稍候…",
                   notice: true, noticeTitle: "正在检测更新", checking: true,
                   actionLabel: "取消", actionUrl: null);

        private NotifyWindow(string version, string content,
                             bool notice, string noticeTitle, bool checking,
                             string actionLabel, string? actionUrl)
        {
            _version = version;
            _notice = notice;
            _noticeTitle = noticeTitle;
            _checking = checking;
            _actionLabel = string.IsNullOrEmpty(actionLabel) ? "好的" : actionLabel;
            _actionUrl = actionUrl;
            _logicalHeight = notice ? NOTICE_HEIGHT : HEIGHT;

            LoadAppIcon();
            BuildWrappedLines(content, 380f); // 文本最大宽度 380 (左右边距40)

            IntPtr hInstance = Process.GetCurrentProcess().MainModule?.BaseAddress ?? IntPtr.Zero;
            EnsureClassRegistered(hInstance);

            _dpiScale = Win32.GetDpiForSystem() / 96f;
            _scaledW = (int)(WIDTH * _dpiScale);
            _scaledH = (int)(_logicalHeight * _dpiScale);

            int screenW = System.Windows.Forms.Screen.PrimaryScreen?.Bounds.Width ?? 1920;
            int screenH = System.Windows.Forms.Screen.PrimaryScreen?.Bounds.Height ?? 1080;

            _hwnd = Win32.CreateWindowEx(
                Win32.WS_EX_LAYERED | Win32.WS_EX_TOPMOST,
                ClassName, "Notify",
                Win32.WS_POPUP | Win32.WS_VISIBLE,
                (screenW - _scaledW) / 2, (screenH - _scaledH) / 2,
                _scaledW, _scaledH,
                IntPtr.Zero, IntPtr.Zero, _classInstance, IntPtr.Zero
            );

            if (_hwnd == IntPtr.Zero)
            {
                // 建窗失败就别再往空句柄上画：调用方会看到异常并被记录。
                throw new InvalidOperationException(
                    $"创建更新弹窗失败！错误码: {Marshal.GetLastWin32Error()}");
            }

            lock (_liveWindowsLock) _liveWindows[_hwnd] = this;

            Render();
        }

        // 类注册在整个进程里只做一次：重复 RegisterClass 会失败，而系统用的是第一次的窗口过程。
        private static void EnsureClassRegistered(IntPtr hInstance)
        {
            if (_classRegistered) return;

            lock (_liveWindowsLock)
            {
                if (_classRegistered) return;

                var wc = new Win32.WNDCLASS
                {
                    lpfnWndProc = _staticWndProc,
                    hInstance = hInstance,
                    lpszClassName = ClassName,
                    hCursor = Win32.LoadCursor(IntPtr.Zero, Win32.IDC_ARROW)
                };
                Win32.RegisterClass(ref wc);
                _classInstance = hInstance;
                _classRegistered = true;
            }
        }

        // 静态入口：按句柄找回实例，再转给实例的窗口过程。
        // 建窗过程中（WM_NCCREATE/WM_CREATE）实例还没登记，交给 DefWindowProc 即可，
        // 这些消息本来也没人处理，构造函数随后会自己 Render 一次。
        private static IntPtr StaticWndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
        {
            NotifyWindow? target;
            lock (_liveWindowsLock) _liveWindows.TryGetValue(hwnd, out target);

            if (target == null)
                return Win32.DefWindowProc(hwnd, msg, wParam, lParam);

            try
            {
                return target.WndProc(hwnd, msg, wParam, lParam);
            }
            catch (Exception ex)
            {
                Logger.Error($"更新窗口过程处理消息 0x{msg:X4} 时异常，已忽略", ex);
                return IntPtr.Zero;
            }
        }

        private static void LoadAppIcon()
        {
            if (_iconBitmap != null) return;
            try
            {
                using (var iconStream = DataResources.OpenRead("NPS_NotchPeninsula-logo.ico"))
                    if (iconStream != null) _iconBitmap = SKBitmap.Decode(iconStream);

                if (_iconBitmap == null)
                {
                    // 但解码失败时守卫不成立，会重复进入本分支）。
                    using var sysIcon = System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath!);
                    if (sysIcon != null)
                    {
                        using var bmp = sysIcon.ToBitmap();
                        using var ms = new MemoryStream();
                        bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
                        ms.Position = 0;
                        _iconBitmap = SKBitmap.Decode(ms);
                    }
                }
            }
            catch { }
        }

        private void BuildWrappedLines(string content, float maxWidth)
        {
            _wrappedLines.Clear();
            string[] lines = content.Replace("\r", "").Split('\n');
            foreach (var line in lines)
            {
                if (string.IsNullOrEmpty(line)) { _wrappedLines.Add(""); continue; }

                int start = 0;
                while (start < line.Length)
                {
                    int len = line.Length - start;
                    string sub = line.Substring(start, len);
                    while (len > 0 && _contentPaint.MeasureText(sub) > maxWidth)
                    {
                        len--;
                        if (len > 0) sub = line.Substring(start, len);
                    }
                    if (len == 0) { len = 1; sub = line.Substring(start, 1); } // 兜底防止死循环

                    _wrappedLines.Add(sub);
                    start += len;
                }
            }
        }

        public void Run()
        {
            while (Win32.GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
            {
                Win32.TranslateMessage(ref msg);
                Win32.DispatchMessage(ref msg);
            }

            // 消息循环结束（窗口已销毁）后摘掉实例，避免 hwnd 表攒住死对象。
            _closed = true;
            lock (_liveWindowsLock) _liveWindows.Remove(_hwnd);
        }

        /// <summary>
        /// 把检测结论写回这个窗（任意线程可调；真正的界面改动在窗口线程上做）。
        /// 检测窗还没建出来 / 用户已经取消时静默丢弃。
        /// </summary>
        public void SetResult(string title, string message, string actionLabel, string? actionUrl)
        {
            lock (_resultLock)
            {
                if (_closed) return;
                _pendingTitle = title;
                _pendingMessage = message;
                _pendingActionLabel = actionLabel;
                _pendingActionUrl = actionUrl;
            }
            Win32.PostMessage(_hwnd, WM_APP_SET_RESULT, IntPtr.Zero, IntPtr.Zero);
        }

        /// <summary>请求窗口自己走正常的销毁流程（WM_DESTROY → 消息循环退出）。</summary>
        public void Close()
        {
            lock (_resultLock)
            {
                if (_closed) return;
            }
            Win32.PostMessage(_hwnd, WM_APP_CLOSE, IntPtr.Zero, IntPtr.Zero);
        }

        private IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
        {
            switch (msg)
            {
                case Win32.WM_MOUSEMOVE:
                    int x = (int)((short)(Win32.Low32(lParam) & 0xFFFF) / _dpiScale);
                    int y = (int)((short)((Win32.Low32(lParam) >> 16) & 0xFFFF) / _dpiScale);

                    var closeHit = GetCloseRect();
                    closeHit.Inflate(2f, 2f);
                    bool overClose = closeHit.Contains(x, y);

                    int hit = -1;
                    if (!overClose)
                        for (int i = 0; i < ButtonCount; i++)
                            if (GetButtonRect(i).Contains(x, y)) { hit = i; break; }

                    if (overClose != _hoveredClose || hit != _hoveredButton)
                    {
                        _hoveredClose = overClose;
                        _hoveredButton = hit;
                        Render();
                    }
                    break;

                case Win32.WM_LBUTTONDOWN:
                    int clickX = (int)((short)(Win32.Low32(lParam) & 0xFFFF) / _dpiScale);
                    int clickY = (int)((short)((Win32.Low32(lParam) >> 16) & 0xFFFF) / _dpiScale);

                    // 关闭键优先于按钮与拖拽：点在叉上就只关窗，绝不触发下面的「前往下载」。
                    var closeClick = GetCloseRect();
                    closeClick.Inflate(2f, 2f);
                    if (closeClick.Contains(clickX, clickY))
                    {
                        Win32.DestroyWindow(hwnd);
                        break;
                    }

                    if (_hoveredButton == 0)
                    {
                        // 更新弹窗的 0 号按钮 = 前往下载；结论弹窗的按钮也是超链接（下载站 / GitHub）
                        string? url = _notice ? _actionUrl : UpdateManager.DownloadPageUrl;
                        if (!string.IsNullOrEmpty(url))
                        {
                            try
                            {
                                using (Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true })) { }
                            }
                            catch (Exception ex)
                            {
                                Logger.Error($"打开链接失败：{url}", ex);
                            }
                        }
                        Win32.DestroyWindow(hwnd);
                    }
                    else if (!_notice && _hoveredButton == 1)
                    {
                        UpdateManager.MarkVersionSkipped(_version);
                        Win32.DestroyWindow(hwnd);
                    }
                    else if (!_notice && _hoveredButton == 2) Win32.DestroyWindow(hwnd);
                    // 拖拽窗口
                    else if (clickY <= 150)
                    {
                        Win32.ReleaseCapture();
                        Win32.SendMessage(hwnd, Win32.WM_NCLBUTTONDOWN, Win32.HTCAPTION, 0);
                    }
                    break;

                case WM_APP_SET_RESULT:
                {
                    string title;
                    string message;
                    string? actionLabel;
                    string? actionUrl;
                    lock (_resultLock)
                    {
                        title = _pendingTitle ?? "";
                        message = _pendingMessage ?? "";
                        actionLabel = _pendingActionLabel;
                        actionUrl = _pendingActionUrl;
                        _pendingTitle = null;
                        _pendingMessage = null;
                        _pendingActionLabel = null;
                        _pendingActionUrl = null;
                    }

                    // 原地从「正在检测」切成结论：高度不变，只换标题、正文与按钮文字。
                    _checking = false;
                    _noticeTitle = title;
                    if (!string.IsNullOrEmpty(actionLabel)) _actionLabel = actionLabel!;
                    _actionUrl = actionUrl;
                    BuildWrappedLines(message, 380f);
                    Render();
                    break;
                }

                case WM_APP_CLOSE:
                    Win32.DestroyWindow(hwnd);
                    break;

                case Win32.WM_DESTROY:
                    _closed = true;
                    Win32.PostQuitMessage(0);
                    break;
            }
            return Win32.DefWindowProc(hwnd, msg, wParam, lParam);
        }

        private unsafe void Render()
        {
            var info = new SKImageInfo(_scaledW, _scaledH, SKColorType.Bgra8888, SKAlphaType.Premul);
            using var surface = SKSurface.Create(info);
            var canvas = surface.Canvas;

            canvas.Scale(_dpiScale);
            canvas.Clear(SKColors.Transparent);

            // 1. 背景与边框
            canvas.DrawRoundRect(new SKRect(0, 0, WIDTH, _logicalHeight), 10, 10, _bgPaint);
            canvas.DrawRoundRect(new SKRect(0.5f, 0.5f, WIDTH - 0.5f, _logicalHeight - 0.5f), 10, 10, _borderPaint);

            if (_notice)
            {
                RenderNotice(canvas);
                DrawCloseButton(canvas);
                UpdateWindow(surface.PeekPixels());
                return;
            }

            // 2. 顶部大 Icon（与检测中 / 结论卡同一套位置，三张卡片大小一致）
            if (_iconBitmap != null)
            {
                float iconSize = 72f;
                float iconX = (WIDTH - iconSize) / 2f;
                canvas.DrawBitmap(_iconBitmap, new SKRect(iconX, 30, iconX + iconSize, 30 + iconSize), _hqSamplingOpts);
            }

            // 3. 核心标题：不再铺更新日志，只留一句版本提示
            canvas.DrawText("NotchPeninsula", WIDTH / 2f, 148, _title1Paint);
            canvas.DrawText($"发现新版本 v{_version}！是否前往下载？", WIDTH / 2f, 182, _title2Paint);

            // 4. 底部按钮组（前往下载 / 不再提醒 / 取消）
            DrawButton(canvas, 0, "前往下载", primary: true);
            DrawButton(canvas, 1, "不再提醒", primary: false);
            DrawButton(canvas, 2, "取消", primary: false);

            // 5. 底部小字说明：让用户知道「不再提醒」不是永久静音（与底边留出余量）
            canvas.DrawText("「不再提醒」仅对当前版本有效，之后有新版本仍会提示", WIDTH / 2f, _logicalHeight - 14f, _hintPaint);

            DrawCloseButton(canvas);
            UpdateWindow(surface.PeekPixels());
        }

        // 结论弹窗（已最新 / 检测失败）与检测中的进度窗共用：
        // 图标 + 标题 + 居中正文 + 一个按钮（检测中是「取消」，出结论后变「好的」）。
        private void RenderNotice(SKCanvas canvas)
        {
            if (_iconBitmap != null)
            {
                float iconSize = 72f;
                float iconX = (WIDTH - iconSize) / 2f;
                canvas.DrawBitmap(_iconBitmap, new SKRect(iconX, 30, iconX + iconSize, 30 + iconSize), _hqSamplingOpts);
            }

            canvas.DrawText(_noticeTitle, WIDTH / 2f, 148, _title1Paint);

            var prevAlign = _contentPaint.TextAlign;
            _contentPaint.TextAlign = SKTextAlign.Center;
            for (int i = 0; i < _wrappedLines.Count; i++)
                canvas.DrawText(_wrappedLines[i], WIDTH / 2f, 182 + i * 22f, _contentPaint);
            _contentPaint.TextAlign = prevAlign;

            DrawButton(canvas, 0, _actionLabel, primary: true);
        }

        // 右上角关闭键：悬停给一点底色，叉本身在灰色与白色之间切换。
        // 三种弹窗（发现新版本 / 正在检测 / 检测结论）都画在同一位置，用户不用重新找。
        private void DrawCloseButton(SKCanvas canvas)
        {
            var r = GetCloseRect();

            // 悬停底色是方的，卡片是圆角的：不裁一下，右上角会露出一小块方形底色。
            using var card = new SKPath();
            card.AddRoundRect(new SKRect(0, 0, WIDTH, _logicalHeight), 10, 10);
            canvas.Save();
            canvas.ClipPath(card, SKClipOperation.Intersect, antialias: true);

            if (_hoveredClose)
            {
                _dynamicFill.Color = new SKColor(255, 255, 255, 30);
                canvas.DrawRoundRect(r, 4, 4, _dynamicFill);
            }

            using var cross = new SKPaint
            {
                IsAntialias = true,
                Style = SKPaintStyle.Stroke,
                StrokeWidth = _hoveredClose ? 1.9f : 1.5f,
                StrokeCap = SKStrokeCap.Round,
                Color = _hoveredClose ? SKColors.White : new SKColor(150, 150, 150)
            };

            float cx = r.MidX, cy = r.MidY, half = 5.5f;
            canvas.DrawLine(cx - half, cy - half, cx + half, cy + half, cross);
            canvas.DrawLine(cx + half, cy - half, cx - half, cy + half, cross);

            canvas.Restore();
        }

        private void DrawButton(SKCanvas canvas, int index, string text, bool primary)
        {
            var rect = GetButtonRect(index);
            bool hovered = _hoveredButton == index;

            if (primary)
                _dynamicFill.Color = hovered ? new SKColor(0, 140, 240) : new SKColor(0, 120, 212);
            else
                _dynamicFill.Color = hovered ? new SKColor(255, 255, 255, 42) : new SKColor(255, 255, 255, 10);

            canvas.DrawRoundRect(rect, 6, 6, _dynamicFill);
            canvas.DrawText(text, rect.MidX, ButtonY + 26, _btnTextPaint);
        }

        private unsafe void UpdateWindow(SKPixmap pixmap)
        {
            IntPtr screenDc = Win32.GetDC(IntPtr.Zero);
            IntPtr memDc = Win32.CreateCompatibleDC(screenDc);
            var bmi = new Win32.BITMAPINFO
            {
                bmiHeader = new Win32.BITMAPINFOHEADER { biSize = (uint)Marshal.SizeOf<Win32.BITMAPINFOHEADER>(), biWidth = _scaledW, biHeight = -_scaledH, biPlanes = 1, biBitCount = 32, biCompression = 0 }
            };

            IntPtr hBitmap = Win32.CreateDIBSection(screenDc, ref bmi, Win32.DIB_RGB_COLORS, out IntPtr pBits, IntPtr.Zero, 0);
            IntPtr hOldBitmap = Win32.SelectObject(memDc, hBitmap);
            Buffer.MemoryCopy(pixmap.GetPixels().ToPointer(), pBits.ToPointer(), (long)_scaledW * _scaledH * 4, (long)_scaledW * _scaledH * 4);

            var ptSrc = new Win32.POINT(0, 0);
            var ptDst = new Win32.POINT(0, 0);
            Win32.GetWindowRect(_hwnd, out var winRect);
            ptDst.x = winRect.Left; ptDst.y = winRect.Top;

            var size = new Win32.SIZE(_scaledW, _scaledH);
            var blend = new Win32.BLENDFUNCTION { BlendOp = Win32.AC_SRC_OVER, SourceConstantAlpha = 255, AlphaFormat = Win32.AC_SRC_ALPHA };

            Win32.UpdateLayeredWindow(_hwnd, screenDc, ref ptDst, ref size, memDc, ref ptSrc, 0, ref blend, Win32.ULW_ALPHA);
            Win32.SelectObject(memDc, hOldBitmap); Win32.DeleteObject(hBitmap); Win32.DeleteDC(memDc); Win32.ReleaseDC(IntPtr.Zero, screenDc);
        }
    }
}