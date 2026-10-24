using Windows.UI.Notifications;
using Windows.UI.Notifications.Management;
using System.Collections.ObjectModel;
using System.Diagnostics;
using SkiaSharp;

namespace NotchPeninsula
{
    public class ToastNotificationListener : IDisposable
    {
        private UserNotificationListener? _listener;
        private uint _lastNotificationId;
        private bool _initialized;

        // 轮询诊断的"上一次状态"。
        private string _lastPollDiag = "";

        // 心跳诊断状态：用来区分两种"静默"——
        private const int HeartbeatRounds = 30;      // ≈60s（轮询 2s 一次）
        private const long SlowPollWarnMs = 3000;    // 单次取快照超过这个耗时就在日志里点出来
        private int _pollRounds;
        private int _lastSnapshotCount = -1;
        private uint _lastSnapshotMaxId;

        //                               "取不到数据"由轮询自己的诊断负责，看门狗只管"有没有在跑"）。
        private long _lastSnapshotUtcTicks;
        private long _lastPollAttemptUtcTicks;
        public long LastSnapshotUtcTicks => System.Threading.Volatile.Read(ref _lastSnapshotUtcTicks);
        public long LastPollAttemptUtcTicks => System.Threading.Volatile.Read(ref _lastPollAttemptUtcTicks);

        private const int SeenIdCapacity = 512;
        private readonly HashSet<uint> _seenIds = new();
        private readonly Queue<uint> _seenIdOrder = new();

        //       所以"超时"这一个判据只对应"调用挂死"，两者在日志里能区分开。
        private const int PollTimeoutMs = 8000;        // 单次取快照超时
        private const int RecoverRetryMs = 60_000;     // 判定失效后，每 60s 重连一次
        private const int RecoverFailLogMinutes = 10;  // 重连连续失败时，最多每 10 分钟落一条 WARN
        private int _pollInFlight;                     // 0/1：同一时刻只允许一个取快照调用在飞
        private bool _pollSuspended;                   // 通道已判死：常规轮询暂停，只试重连
        private DateTime _nextRecoverUtc;
        private DateTime _lastRecoverFailLogUtc;
        private int _recoverAttempts;

        private System.Net.HttpListener? _httpListener;
        
        public event Action<ToastData>? OnToastDetected;
        
        public async Task<(bool Success, string Message)> InitializeAsync()
        {
            StartHttpServer(); // 启动本地Http服务
            try
            {
                _listener = UserNotificationListener.Current;
                
                var accessStatus = await _listener.RequestAccessAsync();
                
                if (accessStatus == UserNotificationListenerAccessStatus.Allowed)
                {
                    var (timedOut, notifications, error) = await GetSnapshotAsync(PollTimeoutMs);
                    if (timedOut)
                    {
                        LogPollDiag($"初始化取快照超时（>{PollTimeoutMs} ms），先启动轮询，交由重连逻辑处理", true);
                    }
                    else
                    {
                        if (error != null) Logger.Warn($"[通知轮询] 初始化取快照异常：{error.GetType().Name}: {error.Message}");
                        if (notifications != null && notifications.Count > 0)
                            _lastNotificationId = notifications.Max(n => n.Id);
                        // 把启动时已存在的 ID 都记为"见过"：否则它们会被回退护栏误判成"从未见过的新 ID"。
                        RememberSeenIds(notifications);
                        _initialized = true;
                    }

                    return (true, "通知访问权限已获取");
                }
                else
                {
                    return (false, $"无法获取通知访问权限: {accessStatus}，请前往 设置 > 隐私和安全性 > 通知 允许此应用访问通知");
                }
            }
            catch (Exception ex)
            {
                return (false, $"初始化失败: {ex.Message}");
            }
        }

        // 极致性能的轻量级 HTTP 监听
        private void StartHttpServer()
        {
            System.Net.HttpListener? listener = null;
            try
            {
                listener = new System.Net.HttpListener();
                listener.Prefixes.Add("http://127.0.0.1:47300/api/activities/");
                listener.Start();

                _httpListener = listener;

                byte[] okRes = System.Text.Encoding.UTF8.GetBytes("{\"ok\":true}");

                Task.Run(async () =>
                {
                    while (listener.IsListening)
                    {
                        System.Net.HttpListenerContext? ctx = null;
                        try
                        {
                            ctx = await listener.GetContextAsync();
                            if (ctx.Request.HttpMethod == "POST")
                            {
                                string rawJson;
                                using (var reader = new System.IO.StreamReader(ctx.Request.InputStream))
                                    rawJson = await reader.ReadToEndAsync();

                                using var doc = ParseBody(rawJson);
                                var root = doc.RootElement;

                                string appName = root.TryGetProperty("kind", out var k) ? k.GetString() ?? "手机消息" : "手机消息";
                                string title = root.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "";
                                string body = root.TryGetProperty("subtitle", out var s) ? s.GetString() ?? "" : "";

                                string iconSpec = ReadStringProp(root, "icon", "iconUrl");

                                // 头像与图标是两条独立通道：有头像时头像出场，预设 icon 只在没头像时才显示。
                                string avatarSpec = ReadStringProp(root, "avatar", "avatarUrl", "avatar_url", "head");

                                Logger.Info($"[HTTP接口] 收到发送端消息推送到灵动岛 -> 类型: {appName}, 标题: {title}, 内容: {body}, "
                                          + $"头像: {ToastIconProvider.Describe(avatarSpec)}, 图标: {ToastIconProvider.Describe(iconSpec)}");

                                var toast = new ToastData
                                {
                                    AppName = appName,
                                    Title = title,
                                    Body = body,
                                    ProcessName = "PostForwarder",
                                    // 赋予动态 ID，强制让 Renderer 更新文本缓存
                                    NotificationId = (uint)Environment.TickCount
                                };

                                OnToastDetected?.Invoke(toast);

                                // 先起头像的解析：它优先级最高，让它在预览窗口里更早到位。
                                ToastIconProvider.ResolveInBackground(avatarSpec, bmp => toast.Avatar = bmp);
                                ToastIconProvider.ResolveInBackground(iconSpec, bmp => toast.CustomIcon = bmp);
                            }

                            ctx.Response.StatusCode = 200;
                            ctx.Response.ContentType = "application/json";
                            ctx.Response.ContentLength64 = okRes.Length;
                            await ctx.Response.OutputStream.WriteAsync(okRes, 0, okRes.Length);
                        }
                        catch (Exception ex)
                        {
                            if (listener.IsListening) Logger.Error("[HTTP接口] 消息解析或处理异常", ex);
                            else Logger.Debug($"[HTTP接口] 监听已停止，接收循环退出：{ex.GetType().Name}");

                            try { if (ctx != null) ctx.Response.StatusCode = 500; } catch { }
                        }
                        finally
                        {
                            try { ctx?.Response.Close(); } catch { }
                        }
                    }
                });

                Logger.Info("[HTTP接口] 本地 47300 端口监听已启动，等待接收手机消息...");
            }
            catch (Exception ex)
            {
                Logger.Error("[HTTP接口] 端口监听启动失败 (可能被占用)", ex);
                try { listener?.Close(); } catch { }
                _httpListener = null;
            }
        }

        public void Dispose()
        {
            var listener = System.Threading.Interlocked.Exchange(ref _httpListener, null);
            if (listener == null) return;

            try { listener.Stop(); } catch { }
            try { listener.Close(); } catch { }
            Logger.Info("[HTTP接口] 本地监听已停止");
        }

        // 解析 HTTP 请求体。**先按合法 JSON 直接解析**：标准转义（\uXXXX / \n / \"）必须原样交给
        // 解析器 —— 以前无条件把每个反斜杠翻倍，于是 Python json.dumps 默认转义的「MSP测试」
        // 会变成字面文本 MSP\u6d4b\u8bd5。只有解析失败（发送端发了 \N 这类非法转义）才退回
        // 原来那套暴力修复；仍然失败就往上抛记 500，与修复前对待坏输入的行为完全一致。
        private static System.Text.Json.JsonDocument ParseBody(string rawJson)
        {
            try
            {
                return System.Text.Json.JsonDocument.Parse(rawJson);
            }
            catch (System.Text.Json.JsonException)
            {
                string repaired = rawJson.Replace("\\", "\\\\").Replace("\\\\\"", "\\\"");
                return System.Text.Json.JsonDocument.Parse(repaired);
            }
        }

        private static string ReadStringProp(System.Text.Json.JsonElement root, params string[] names)
        {
            try
            {
                foreach (string name in names)
                {
                    if (root.TryGetProperty(name, out var v) && v.ValueKind == System.Text.Json.JsonValueKind.String
                        && !string.IsNullOrWhiteSpace(v.GetString()))
                        return v.GetString()!;
                }
                return string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }

        private void LogPollDiag(string diag, bool isError = false)
        {
            if (diag == _lastPollDiag) return;
            _lastPollDiag = diag;
            if (isError) Logger.Warn($"[通知轮询] {diag}");
            else Logger.Info($"[通知轮询] {diag}");
        }

        private void RememberSeenIds(IReadOnlyList<UserNotification>? list)
        {
            if (list == null) return;
            foreach (var notification in list)
            {
                if (!_seenIds.Add(notification.Id)) continue;
                _seenIdOrder.Enqueue(notification.Id);
                while (_seenIdOrder.Count > SeenIdCapacity) _seenIds.Remove(_seenIdOrder.Dequeue());
            }
        }

        private async Task<(bool TimedOut, IReadOnlyList<UserNotification>? Result, Exception? Error)> GetSnapshotAsync(int timeoutMs)
        {
            var listener = _listener;
            if (listener == null) return (false, null, null);

            try
            {
                var opTask = listener.GetNotificationsAsync(NotificationKinds.Toast).AsTask();
                var finished = await Task.WhenAny(opTask, Task.Delay(timeoutMs));
                if (finished != opTask)
                {
                    // 超时：这个调用会被永久搁置，不再等它。
                    _ = opTask.ContinueWith(static t => _ = t.Exception,
                        CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                    return (true, null, null);
                }
                return (false, await opTask, null);
            }
            catch (Exception ex)
            {
                return (false, null, ex);
            }
        }

        private async Task TryRecoverAsync()
        {
            _nextRecoverUtc = DateTime.UtcNow.AddMilliseconds(RecoverRetryMs);
            _recoverAttempts++;

            var listener = _listener;
            if (listener == null) return;

            UserNotificationListenerAccessStatus access;
            try { access = listener.GetAccessStatus(); }
            catch (Exception ex)
            {
                access = UserNotificationListenerAccessStatus.Unspecified;
                Logger.Warn($"[通知轮询] 重连前读取访问权限失败：{ex.GetType().Name}: {ex.Message}");
            }

            if (access != UserNotificationListenerAccessStatus.Allowed)
            {
                try
                {
                    var accessTask = listener.RequestAccessAsync().AsTask();
                    var done = await Task.WhenAny(accessTask, Task.Delay(PollTimeoutMs));
                    if (done != accessTask) { LogRecoverFailure($"重新申请访问权限超时（>{PollTimeoutMs} ms）"); return; }
                    access = await accessTask;
                }
                catch (Exception ex)
                {
                    LogRecoverFailure($"重新申请访问权限异常：{ex.GetType().Name}: {ex.Message}");
                    return;
                }
            }

            var (timedOut, notifications, error) = await GetSnapshotAsync(PollTimeoutMs);
            if (timedOut) { LogRecoverFailure($"重连后取快照仍超时（>{PollTimeoutMs} ms）"); return; }
            if (error != null) { LogRecoverFailure($"重连后取快照异常：{error.GetType().Name}: {error.Message}"); return; }
            if (notifications == null) { LogRecoverFailure("重连后仍未取到快照"); return; }

            uint maxId = notifications.Count > 0 ? notifications.Max(n => n.Id) : 0;
            if (maxId > 0) _lastNotificationId = maxId;
            _initialized = true;

            _lastSnapshotCount = notifications.Count;
            _lastSnapshotMaxId = maxId;
            System.Threading.Volatile.Write(ref _lastSnapshotUtcTicks, DateTime.UtcNow.Ticks);
            RememberSeenIds(notifications);

            _pollSuspended = false;
            _lastPollDiag = "";   // 恢复后允许再落一条诊断（正常运行时的"快照为空/未前进"）
            Logger.Info($"[通知轮询] 重连成功（access={access}，第 {_recoverAttempts} 次尝试），水位线重置为 {_lastNotificationId}，恢复轮询");
            _recoverAttempts = 0;
        }

        private void LogRecoverFailure(string reason)
        {
            var now = DateTime.UtcNow;
            if (now - _lastRecoverFailLogUtc < TimeSpan.FromMinutes(RecoverFailLogMinutes))
            {
                Logger.Debug($"[通知轮询] 重连失败：{reason}");
                return;
            }
            _lastRecoverFailLogUtc = now;
            Logger.Warn($"[通知轮询] 重连失败（第 {_recoverAttempts} 次）：{reason}；通道仍不可用，{RecoverRetryMs / 1000}s 后重试");
        }

        public async Task<(ToastData? Data, string? Message)> FetchLatestNotificationAsync()
        {
            // 先记"发起时刻"：渲染循环里的看门狗靠它判"轮询还在不在跑"（哪怕这一轮取不到数据）。
            System.Threading.Volatile.Write(ref _lastPollAttemptUtcTicks, DateTime.UtcNow.Ticks);

            if (_listener == null)
            {
                LogPollDiag("监听器未初始化，本轮没有取通知", true);
                return (null, "监听器未初始化");
            }
            if (NotchWindow.IsToastEnabled == false)
            {
                LogPollDiag("通知监听已被禁用（设置里关掉了系统消息通知）");
                return (null, "通知监听已被禁用");
            }
            if (++_pollRounds % HeartbeatRounds == 0)
            {
                var snapshotTicks = LastSnapshotUtcTicks;
                var agoText = snapshotTicks == 0
                    ? "从未"
                    : $"{(DateTime.UtcNow - new DateTime(snapshotTicks, DateTimeKind.Utc)).TotalSeconds:F0} 秒前";
                Logger.Info($"[通知轮询] 心跳：上次读到快照 {agoText}，count={_lastSnapshotCount}，maxId={_lastSnapshotMaxId}，"
                          + $"通道状态={(_pollSuspended ? $"已失效(重连中，第 {_recoverAttempts} 次)" : "正常")}");
            }

            if (_pollSuspended)
            {
                if (DateTime.UtcNow >= _nextRecoverUtc) await TryRecoverAsync();
                return (null, "通知通道疑似失效，等待重连");
            }

            if (System.Threading.Interlocked.CompareExchange(ref _pollInFlight, 1, 0) != 0)
            {
                LogPollDiag("上一次取快照仍未返回，本轮跳过");
                return (null, null);
            }

            try
            {
                long startedTicks = Environment.TickCount64;
                var (timedOut, notifications, error) = await GetSnapshotAsync(PollTimeoutMs);

                if (timedOut)
                {
                    LogPollDiag($"取快照超时（>{PollTimeoutMs} ms）：判定通知通道失效，转入重连", true);
                    _pollSuspended = true;
                    await TryRecoverAsync();
                    return (null, "取快照超时");
                }
                if (error != null) throw error;

                _lastSnapshotCount = notifications?.Count ?? -1;
                _lastSnapshotMaxId = notifications is { Count: > 0 } ? notifications.Max(n => n.Id) : 0;
                System.Threading.Volatile.Write(ref _lastSnapshotUtcTicks, DateTime.UtcNow.Ticks);

                long cost = Environment.TickCount64 - startedTicks;
                if (cost > SlowPollWarnMs)
                    Logger.Warn($"[通知轮询] 本次取快照耗时 {cost} ms，明显偏慢");
                
                if (notifications == null || notifications.Count == 0)
                {
                    // 这里必须区分两种"空"：通知中心本来就空（正常），
                    UserNotificationListenerAccessStatus access;
                    try { access = _listener.GetAccessStatus(); }
                    catch (Exception ex) { access = UserNotificationListenerAccessStatus.Unspecified; Logger.Warn($"[通知轮询] 读取访问权限失败：{ex.GetType().Name}: {ex.Message}"); }

                    LogPollDiag(
                        access == UserNotificationListenerAccessStatus.Allowed
                            ? "快照为空（通知中心当前没有通知），访问权限正常"
                            : $"快照为空且访问权限异常（GetAccessStatus={access}），通知会被静默丢弃",
                        access != UserNotificationListenerAccessStatus.Allowed);
                    return (null, null);
                }
                
                var latestNotif = notifications.OrderByDescending(n => n.Id).First();
                uint maxId = latestNotif.Id;
                
                if (maxId == 0)
                {
                    LogPollDiag("快照里最大通知 ID 为 0，本轮忽略", true);
                    return (null, null);
                }
                
                if (!_initialized)
                {
                    _lastNotificationId = maxId;
                    _initialized = true;
                    RememberSeenIds(notifications);   // 首次拿到的这批 ID 必须记为"见过"，否则会被回退护栏误判
                    return (null, "首次初始化完成");
                }

                if (maxId < _lastNotificationId && !_seenIds.Contains(maxId))
                {
                    Logger.Warn($"[通知轮询] 检测到通知 ID 回退（快照最大 ID={maxId} < 水位线={_lastNotificationId}，"
                              + "且该 ID 从未出现过），判定计数器被重置，水位线已重置，后续通知恢复捕获");
                    _lastNotificationId = maxId;
                    RememberSeenIds(notifications);
                    return (null, "通知 ID 回退，水位线已重置");
                }
                RememberSeenIds(notifications);

                if (maxId > _lastNotificationId)
                {
                    _lastNotificationId = maxId;
                    
                    var toastData = ExtractToastData(latestNotif);
                    
                    if (toastData != null)
                    {
                        toastData.NotificationId = latestNotif.Id;
                        toastData.InternalNotification = latestNotif;
                        OnToastDetected?.Invoke(toastData);
                        return (toastData, null);
                    }
                }
                else
                {
                    LogPollDiag($"快照未前进：maxId={maxId}，已记录水位线={_lastNotificationId}，本轮忽略");
                }
                
                return (null, null);
            }
            catch (Exception ex)
            {
                LogPollDiag($"获取通知异常：{ex.GetType().Name}: {ex.Message}", true);
                return (null, $"获取通知失败: {ex.Message}");
            }
            finally
            {
                System.Threading.Interlocked.Exchange(ref _pollInFlight, 0);
            }
        }
        
        private ToastData? ExtractToastData(UserNotification notification)
        {
            // WinRT 投影访问器会对个别通知抛 TargetInvocationException（常见：通知在读到它之前
            // 已被系统/应用撤走）。外层 Message 永远是那句模板话，真因全在 InnerException ——
            // 逐段取数、逐段兜底，一个访问器炸掉不再连带丢掉整条通知可用的部分。
            string appName = "系统通知";
            string aumid = "";

            try
            {
                var appInfo = notification.AppInfo;
                appName = appInfo?.DisplayInfo?.DisplayName ?? appName;
                aumid = appInfo?.AppUserModelId ?? "";
            }
            catch (Exception ex)
            {
                Logger.Warn($"[Toast] 读应用信息失败：{DescribeException(ex)}（ID: {notification.Id}），应用名用占位");
            }

            ToastData toast;
            try
            {
                var visual = notification.Notification?.Visual;

                if (visual == null)
                {
                    Logger.Warn($"[Toast] 提取失败：通知没有 Visual 内容（应用: {appName}, ID: {notification.Id}）");
                    return null;
                }

                var textElements = visual.GetBinding("ToastGeneric")?.GetTextElements();

                if (textElements == null || textElements.Count == 0)
                {
                    Logger.Warn($"[Toast] 提取失败：ToastGeneric 里没有文本元素（应用: {appName}, ID: {notification.Id}）");
                    return null;
                }

                toast = new ToastData
                {
                    AppName = appName,
                    Title = textElements[0]?.Text ?? "",
                    Body = string.Join(" ", textElements.Skip(1).Select(t => t.Text)),
                    Aumid = aumid,
                    InternalNotification = notification,
                    NotificationId = notification.Id,
                    ProcessName = aumid.Length > 0 ? aumid : appName
                };
            }
            catch (Exception ex)
            {
                Logger.Warn($"[Toast] 提取异常：{DescribeException(ex)}（ID: {notification.Id}）");
                return null;
            }

            if (toast.Title.Contains("微信") || toast.Title.Contains("WeChat") ||
                toast.Body.Contains("微信") || toast.Body.Contains("WeChat"))
            {
                Logger.Debug($"[Toast] 已过滤微信通知 -> 应用: {appName}, 标题: {toast.Title}, ID: {notification.Id}");
                return null;
            }

            Logger.Info($"[Toast] 捕获系统通知 -> 应用: {appName} ({aumid}), 标题: {toast.Title}, 内容: {toast.Body}, ID: {notification.Id}");

            // 应用图标必须趁快照新鲜时就地取：InternalNotification 随时可能被系统撤走，
            // 拖到后台再读 AppInfo 大概率扑空 —— 这就是「部分应用图标读不到」的来源。
            try
            {
                var logo = Plugins.PluginDataBridge.TryGetAppLogo(toast);
                if (logo != null)
                {
                    toast.CustomIcon = logo;
                    toast.OwnsCustomIcon = true;   // 就地解码的私有位图：退场由宿主 Dispose
                }
                else
                {
                    // DisplayInfo 没给 logo（不少应用如此，尤其 AUMID 不带「!」段的）：
                    // 退到 AppIconProvider 的四级解析（shell:AppsFolder / 进程 exe 图标）。
                    ToastAppIconFallback.ResolveInBackground(toast);
                }
            }
            catch { }

            // toast 里的头像（QQ 聊天通知带的好友/群头像）WinRT 读不到，去通知库里取证；后台做，不挡轮询。
            ToastAvatarProbe.ResolveInBackground(toast);

            return toast;
        }

        // 把 TargetInvocationException 这类包装异常剥到内层真因，附上传入侧最上面几帧堆栈。
        private static string DescribeException(Exception ex)
        {
            var parts = new List<string>();
            for (var e = (Exception?)ex; e != null && parts.Count < 5; e = e.InnerException)
                parts.Add($"{e.GetType().Name}: {e.Message}");

            string frames = ex.InnerException?.StackTrace ?? ex.StackTrace ?? "";
            string top = string.Join(" | ", frames.Split('\n')
                .Select(l => l.Trim())
                .Where(l => l.StartsWith("   于 ") || l.StartsWith("at "))
                .Take(3));

            return top.Length > 0
                ? string.Join(" <- ", parts) + " @ " + top
                : string.Join(" <- ", parts);
        }

        public async Task ClearAllNotificationsAsync()
        {
            try
            {
                if (_listener != null)
                {
                    var notifications = await _listener.GetNotificationsAsync(NotificationKinds.Toast);
                    if (notifications != null)
                    {
                        foreach (var notif in notifications)
                        {
                            try
                            {
                                _listener.RemoveNotification(notif.Id);
                            }
                            catch { }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"ClearNotifications error: {ex.Message}");
            }
        }

        public void RemoveNotificationById(uint notificationId)
        {
            if (_listener != null)
            {
                try
                {
                    _listener.RemoveNotification(notificationId);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"删除通知失败: {ex.Message}");
                }
            }
        }
    }
    
    public class ToastData
    {
        private string _appName = "";
        private string _title = "";
        private string _body = "";

        public string AppName { get => _appName; set => _appName = NormalizeText(value); }
        public string Title { get => _title; set => _title = NormalizeText(value); }
        public string Body { get => _body; set => _body = NormalizeText(value); }
        public string Aumid { get; set; } = "";
        public uint NotificationId { get; set; }
        public UserNotification? InternalNotification { get; set; }
        public string ProcessName { get; set; } = "";

        public TimeSpan Duration { get; set; } = TimeSpan.FromSeconds(4);

        public Action? OnClick { get; set; }

        public static string NormalizeText(string? text)
        {
            if (string.IsNullOrEmpty(text)) return "";
            var sb = new System.Text.StringBuilder(text.Length);
            bool pendingSpace = false;
            foreach (char c in text)
            {
                if (char.IsControl(c) || char.IsWhiteSpace(c))
                {
                    pendingSpace = sb.Length > 0;
                    continue;
                }
                if (pendingSpace) { sb.Append(' '); pendingSpace = false; }
                sb.Append(c);
            }
            return sb.ToString();
        }

        private SKBitmap? _customIcon;

        public SKBitmap? CustomIcon
        {
            get => Volatile.Read(ref _customIcon);
            set => Volatile.Write(ref _customIcon, value);
        }

        /// <summary>
        /// true = CustomIcon 是这条通知私有的就地解码 logo，展示结束由宿主 Dispose；
        /// false = 来自 ToastIconProvider 共享缓存（或插件 / HTTP 通道）——那些位图
        /// 归缓存统一管，按单条通知的生命周期去删会把别的通知还在用的图删掉，只断引用。
        /// </summary>
        public bool OwnsCustomIcon { get; set; }

        private SKBitmap? _avatar;

        /// <summary>
        /// 消息头像（如 QQ 消息带的好友 / 群头像）。优先级高于 <see cref="CustomIcon"/>：
        /// 只要有头像就画头像，预设图标只在没有头像时才出场。渲染线程无锁读。
        /// </summary>
        public SKBitmap? Avatar
        {
            get => Volatile.Read(ref _avatar);
            set => Volatile.Write(ref _avatar, value);
        }
    }
    
    /// <summary>
    /// DisplayInfo 没有 logo 时的兜底：借 <see cref="AppIconProvider"/> 拿应用图标。
    /// 先<b>同步</b>快解（缓存 / AUMID 即路径 / shell:AppsFolder / 进程 exe 直配，有进程快照
    /// 250ms 预算）——在提取阶段就完成，图标随首帧一起出现，不会先闪一下默认图；
    /// 快解没中再转后台轮询，晚到就把 CustomIcon 顶上。拿到的都是 Copy，归这条通知私有，
    /// 退场随 OwnsCustomIcon 释放。
    /// </summary>
    internal static class ToastAppIconFallback
    {
        public static void ResolveInBackground(ToastData toast)
        {
            string aumid = toast.Aumid;
            if (string.IsNullOrWhiteSpace(aumid)) return;

            // 同步快解：调用点在轮询后台线程的提取阶段，这一两三百毫秒发生在通知上屏之前
            try
            {
                var quick = AppIconProvider.ResolveQuick(aumid);
                if (quick != null)
                {
                    if (toast.CustomIcon == null)
                    {
                        toast.CustomIcon = quick;
                        toast.OwnsCustomIcon = true;
                        Logger.Info($"[Toast图标] 应用 logo 快解成功（首帧无闪）: {aumid}");
                        return;
                    }
                    quick.Dispose();
                }
            }
            catch (Exception ex)
            {
                Logger.Debug($"[Toast图标] 快解异常: {ex.GetType().Name}: {ex.Message}");
            }

            _ = Task.Run(() =>
            {
                try
                {
                    for (int i = 0; i < 10; i++)   // ≈3s：覆盖通知展示期，拿到就还来得及换图
                    {
                        if (toast.CustomIcon != null) return;

                        var bmp = AppIconProvider.Get(aumid);
                        if (bmp != null)
                        {
                            if (toast.CustomIcon == null)
                            {
                                toast.CustomIcon = bmp;
                                toast.OwnsCustomIcon = true;
                                Logger.Info($"[Toast图标] 应用 logo 经 AppIconProvider 解析成功: {aumid}");
                            }
                            else bmp.Dispose();   // 晚到一步：已经有人先挂上了，这份 Copy 立即释放
                            return;
                        }
                        Thread.Sleep(300);
                    }
                    Logger.Debug($"[Toast图标] 兜底解析 3s 内没拿到，沿用预设/默认图: {aumid}");
                }
                catch (Exception ex)
                {
                    Logger.Debug($"[Toast图标] 兜底解析异常: {ex.GetType().Name}: {ex.Message}");
                }
            });
        }
    }

    public class ToastMessage
    {
        public string Title { get; set; } = "";
        public ObservableCollection<string> Bodies { get; set; } = new ObservableCollection<string>();
    }
}