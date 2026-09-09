using Windows.UI.Notifications;
using Windows.UI.Notifications.Management;
using System.Collections.ObjectModel;
using System.Diagnostics;

namespace NotchPeninsula
{
    public class ToastNotificationListener
    {
        private UserNotificationListener? _listener;
        private uint _lastNotificationId;
        private bool _initialized;
        
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
                    var notifications = await _listener.GetNotificationsAsync(NotificationKinds.Toast);
                    if (notifications != null && notifications.Count > 0)
                    {
                        _lastNotificationId = notifications.Max(n => n.Id);
                    }
                    _initialized = true;
                    
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
            try
            {
                var listener = new System.Net.HttpListener();
                listener.Prefixes.Add("http://127.0.0.1:47300/api/activities/");
                listener.Start();

                byte[] okRes = System.Text.Encoding.UTF8.GetBytes("{\"ok\":true}");

                Task.Run(async () =>
                {
                    while (listener.IsListening)
                    {
                        try
                        {
                            var ctx = await listener.GetContextAsync();
                            if (ctx.Request.HttpMethod == "POST")
                            {
                                // 读取原始字符串
                                string rawJson = await new System.IO.StreamReader(ctx.Request.InputStream).ReadToEndAsync();

                                // 暴力修复非法的反斜杠转义（解决 \N 报错问题），兼容严格的 JSON 解析
                                rawJson = rawJson.Replace("\\", "\\\\").Replace("\\\\\"", "\\\"");

                                using var doc = System.Text.Json.JsonDocument.Parse(rawJson);
                                var root = doc.RootElement;

                                string appName = root.TryGetProperty("kind", out var k) ? k.GetString() ?? "手机消息" : "手机消息";
                                string title = root.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "";
                                string body = root.TryGetProperty("subtitle", out var s) ? s.GetString() ?? "" : "";

                                Logger.Info($"[HTTP接口] 收到发送端消息推送到灵动岛 -> 类型: {appName}, 标题: {title}, 内容: {body}");

                                OnToastDetected?.Invoke(new ToastData
                                {
                                    AppName = appName,
                                    Title = title,
                                    Body = body,
                                    ProcessName = "PostForwarder",
                                    // 赋予动态 ID，强制让 Renderer 更新文本缓存
                                    NotificationId = (uint)Environment.TickCount
                                });
                            }

                            ctx.Response.StatusCode = 200;
                            ctx.Response.ContentType = "application/json";
                            ctx.Response.ContentLength64 = okRes.Length;
                            await ctx.Response.OutputStream.WriteAsync(okRes, 0, okRes.Length);
                            ctx.Response.Close();
                        }
                        catch (Exception ex)
                        {
                            Logger.Error("[HTTP接口] 消息解析或处理异常", ex);
                        }
                    }
                });

                Logger.Info("[HTTP接口] 本地 47300 端口监听已启动，等待接收手机消息...");
            }
            catch (Exception ex)
            {
                Logger.Error("[HTTP接口] 端口监听启动失败 (可能被占用)", ex);
            }
        }

        public async Task<(ToastData? Data, string? Message)> FetchLatestNotificationAsync()
        {
            if (_listener == null)
                return (null, "监听器未初始化");
            if (NotchWindow.IsToastEnabled == false)
                return (null, "通知监听已被禁用");
            try
            {
                var notifications = await _listener.GetNotificationsAsync(NotificationKinds.Toast);
                
                if (notifications == null || notifications.Count == 0)
                    return (null, null);
                
                var latestNotif = notifications.OrderByDescending(n => n.Id).First();
                uint maxId = latestNotif.Id;
                
                if (maxId == 0)
                    return (null, null);
                
                if (!_initialized)
                {
                    _lastNotificationId = maxId;
                    _initialized = true;
                    return (null, "首次初始化完成");
                }
                
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
                
                return (null, null);
            }
            catch (Exception ex)
            {
                return (null, $"获取通知失败: {ex.Message}");
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
                    return null;
                
                var binding = visual.GetBinding("ToastGeneric");
                
                if (binding == null)
                    return null;
                
                var textElements = binding.GetTextElements();
                
                if (textElements == null || textElements.Count == 0)
                    return null;
                
                string title = textElements[0]?.Text ?? "";
                string body = string.Join(" ", textElements.Skip(1).Select(t => t.Text));

                if (title.Contains("微信") || title.Contains("WeChat") ||
    body.Contains("微信") || body.Contains("WeChat"))
                {
                    Logger.Debug($"[Toast] 已过滤微信通知 -> 应用: {appName}, 标题: {title}");
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
                    // best-effort process identifier: prefer AppUserModelId, fall back to display name
                    ProcessName = appInfo?.AppUserModelId ?? appName
                };
            }
            catch
            {
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
        public string AppName { get; set; } = "";
        public string Title { get; set; } = "";
        public string Body { get; set; } = "";
        public string Aumid { get; set; } = "";
        public uint NotificationId { get; set; }
        public UserNotification? InternalNotification { get; set; }
        // best-effort process name for display
        public string ProcessName { get; set; } = "";
    }
    
    public class ToastMessage
    {
        public string Title { get; set; } = "";
        public ObservableCollection<string> Bodies { get; set; } = new ObservableCollection<string>();
    }
}