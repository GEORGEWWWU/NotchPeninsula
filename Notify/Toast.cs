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

                                string iconSpec = ReadStringProp(root, "icon");
                                if (string.IsNullOrWhiteSpace(iconSpec))
                                    iconSpec = ReadStringProp(root, "iconUrl");

                                Logger.Info($"[HTTP接口] 收到发送端消息推送到灵动岛 -> 类型: {appName}, 标题: {title}, 内容: {body}, 图标: {ToastIconProvider.Describe(iconSpec)}");

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

        private static string ReadStringProp(System.Text.Json.JsonElement root, string name)
        {
            try
            {
                return root.TryGetProperty(name, out var v) && v.ValueKind == System.Text.Json.JsonValueKind.String
                    ? v.GetString() ?? string.Empty
                    : string.Empty;
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
            try
            {
                var appInfo = notification.AppInfo;
                var displayInfo = appInfo?.DisplayInfo;
                
                string appName = displayInfo?.DisplayName ?? "系统通知";
                string aumid = appInfo?.AppUserModelId ?? "";
                
                var toastNotification = notification.Notification;
                var visual = toastNotification?.Visual;
                
                if (visual == null)
                {
                    Logger.Warn($"[Toast] 提取失败：通知没有 Visual 内容（应用: {appName}, ID: {notification.Id}）");
                    return null;
                }
                
                var binding = visual.GetBinding("ToastGeneric");
                
                if (binding == null)
                {
                    Logger.Warn($"[Toast] 提取失败：没有 ToastGeneric 绑定（应用: {appName}, ID: {notification.Id}）");
                    return null;
                }
                
                var textElements = binding.GetTextElements();
                
                if (textElements == null || textElements.Count == 0)
                {
                    Logger.Warn($"[Toast] 提取失败：ToastGeneric 里没有文本元素（应用: {appName}, ID: {notification.Id}）");
                    return null;
                }
                
                string title = textElements[0]?.Text ?? "";
                string body = string.Join(" ", textElements.Skip(1).Select(t => t.Text));

                if (title.Contains("微信") || title.Contains("WeChat") ||
    body.Contains("微信") || body.Contains("WeChat"))
                {
                    Logger.Debug($"[Toast] 已过滤微信通知 -> 应用: {appName}, 标题: {title}, ID: {notification.Id}");
                    return null;
                }

                Logger.Info($"[Toast] 捕获系统通知 -> 应用: {appName} ({aumid}), 标题: {title}, 内容: {body}, ID: {notification.Id}");

                return new ToastData
                {
                    AppName = appName,
                    Title = title,
                    Body = body,
                    Aumid = aumid,
                    InternalNotification = notification,
                    NotificationId = notification.Id,
                    ProcessName = appInfo?.AppUserModelId ?? appName
                };
            }
            catch (Exception ex)
            {
                Logger.Warn($"[Toast] 提取异常：{ex.GetType().Name}: {ex.Message}（ID: {notification.Id}）");
                return null;
            }
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
    }
    
    public class ToastMessage
    {
        public string Title { get; set; } = "";
        public ObservableCollection<string> Bodies { get; set; } = new ObservableCollection<string>();
    }
}