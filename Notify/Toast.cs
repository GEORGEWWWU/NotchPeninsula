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

        // 本地 HTTP 接收服务（47300 端口）。提为字段是为了能在 Dispose 时 Stop/Close ——
        // 它占着一个 TCP 端口和一条后台接收循环，不能只靠进程退出兜底。
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
            System.Net.HttpListener? listener = null;
            try
            {
                listener = new System.Net.HttpListener();
                listener.Prefixes.Add("http://127.0.0.1:47300/api/activities/");
                listener.Start();

                // 挂到字段上：后台循环由闭包持有，本类则负责在 Dispose 时把它关掉。
                _httpListener = listener;

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
                                // 读取原始字符串（StreamReader 持有请求流，必须释放，
                                // 否则每次 POST 都留一个未关闭的流给 GC 终结器）
                                string rawJson;
                                using (var reader = new System.IO.StreamReader(ctx.Request.InputStream))
                                    rawJson = await reader.ReadToEndAsync();

                                // 暴力修复非法的反斜杠转义（解决 \N 报错问题），兼容严格的 JSON 解析
                                rawJson = rawJson.Replace("\\", "\\\\").Replace("\\\\\"", "\\\"");

                                using var doc = System.Text.Json.JsonDocument.Parse(rawJson);
                                var root = doc.RootElement;

                                string appName = root.TryGetProperty("kind", out var k) ? k.GetString() ?? "手机消息" : "手机消息";
                                string title = root.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "";
                                string body = root.TryGetProperty("subtitle", out var s) ? s.GetString() ?? "" : "";

                                // 自定义图标（可选）：图片链接 / data:image base64 / 本地文件路径 / 内置别名，
                                // 识别规则见 ToastIconProvider。顺带兼容 iconUrl 这个常见写法。
                                // 这里用容错读取：发送端把 icon 写成数字/对象时只忽略图标，不能让整条消息丢掉。
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

                                // 图标放后台解析：先用默认图标把消息弹出来，解析完 Renderer 下一帧自动换图。
                                // 这样下载图片既不会延迟消息弹出，也不会拖慢这次 HTTP 响应。
                                ToastIconProvider.ResolveInBackground(iconSpec, bmp => toast.CustomIcon = bmp);
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
                // 启动中途失败（端口被占等）时把已分配的监听器放掉，别留一个半死的实例占着端口
                try { listener?.Close(); } catch { }
                _httpListener = null;
            }
        }

        /// <summary>
        /// 停止并释放本地 HTTP 监听服务（47300 端口 + 后台接收循环）。幂等，可重复调用。
        /// 退出路径由 NotchWindow.ShutdownResources 调用。
        /// </summary>
        public void Dispose()
        {
            var listener = System.Threading.Interlocked.Exchange(ref _httpListener, null);
            if (listener == null) return;

            try { listener.Stop(); } catch { }
            try { listener.Close(); } catch { }
            Logger.Info("[HTTP接口] 本地监听已停止");
        }

        /// <summary>
        /// 容错地读一个字符串字段：字段缺失、类型不对（数字 / 对象 / 数组）都返回空串，
        /// 而不是抛异常把整条消息丢掉。
        /// </summary>
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

        private SKBitmap? _customIcon;

        /// <summary>
        /// 发送端自带的自定义图标（HTTP 消息 / 插件提醒）。
        /// 为 null 时 Renderer 走原有的 ProcessName / AppName 判定。
        ///
        /// 解析是异步的，所以这个属性可能在后台线程被赋值 —— 用 Volatile 保证渲染线程
        /// 不会读到「半构造」的引用。Renderer 每帧都会重新读它，赋值后下一帧即生效。
        /// </summary>
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