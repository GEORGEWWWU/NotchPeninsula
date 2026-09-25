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
        // 这条链路原本是完全静默的：权限失效 / 快照为空 / 调用异常 / 正文提取失败，
        // 四种情况都只体现在返回值里，而调用方把返回值丢掉了 —— 于是「收不到通知」时
        // 日志里一条线索都没有。这里只在状态**发生变化**时落一行，稳态下零输出，不会刷屏。
        private string _lastPollDiag = "";

        // 心跳诊断状态：用来区分两种"静默"——
        //   ① 调用挂死：每 2s 仍然发起新调用，但一直没有返回 → "上次读到快照 N 秒前" 持续变大；
        //   ② 快照冻结：调用秒回，但内容停在一个旧 maxId 上不再前进。
        // 只看"有没有新通知"是区分不出来的（通知中心一直挂着旧通知时快照本来就不变），
        // 所以必须把"最后一次成功读到快照的时刻 + 内容摘要"记下来。
        private const int HeartbeatRounds = 30;      // ≈60s（轮询 2s 一次）
        private const long SlowPollWarnMs = 3000;    // 单次取快照超过这个耗时就在日志里点出来
        private int _pollRounds;
        private int _lastSnapshotCount = -1;
        private uint _lastSnapshotMaxId;

        // 供渲染循环里的"轮询看门狗"读取（跨线程，单写多读，用 Volatile）：
        //   LastSnapshotUtcTicks     —— 最近一次**成功取到快照**的时刻；
        //   LastPollAttemptUtcTicks  —— 最近一次**发起轮询**的时刻（判"定时器还活着吗"用它：
        //                               "取不到数据"由轮询自己的诊断负责，看门狗只管"有没有在跑"）。
        private long _lastSnapshotUtcTicks;
        private long _lastPollAttemptUtcTicks;
        public long LastSnapshotUtcTicks => System.Threading.Volatile.Read(ref _lastSnapshotUtcTicks);
        public long LastPollAttemptUtcTicks => System.Threading.Volatile.Read(ref _lastPollAttemptUtcTicks);

        // 计数器回退护栏：通知平台的 ID 计数器一旦被重置（平台重启、数据库重建等），新通知的 ID 会落在
        // 水位线**以下**，而"水位线只升不降"的规则会让它们被永久忽略 —— 也就是永久静默。
        // 所以这里记住见过的 ID：若快照里出现"低于水位线、且从未见过"的 ID，就判定计数器回退，
        // 把水位线重置为当前最大 ID 并打点。容量有界（FIFO 淘汰），稳态开销可忽略。
        // 只在轮询内部访问（已由 _pollInFlight 串行化）。
        private const int SeenIdCapacity = 512;
        private readonly HashSet<uint> _seenIds = new();
        private readonly Queue<uint> _seenIdOrder = new();

        // ── 通道失效检测与自愈（"调用挂死"这一类的兜底） ──────────────────────────
        // 现象：启动一会儿后 GetNotificationsAsync 再也不返回（也不抛异常），轮询从此静默。
        // 处理：单次调用加超时 → 判定通道失效 → 暂停常规轮询、按节奏尝试重连；
        //       重连成功后把水位线重置为当前快照的最大 ID —— 与"重启后重新初始化"同语义，
        //       不会把失效期间堆下来的旧通知一次性全弹出来。
        // 注意：权限被撤销/API 静默失败的表现是**返回空列表**（会走另一条分支，不会到这里），
        //       所以"超时"这一个判据只对应"调用挂死"，两者在日志里能区分开。
        private const int PollTimeoutMs = 8000;        // 单次取快照超时
        private const int RecoverRetryMs = 60_000;     // 判定失效后，每 60s 重连一次
        private const int RecoverFailLogMinutes = 10;  // 重连连续失败时，最多每 10 分钟落一条 WARN
        private int _pollInFlight;                     // 0/1：同一时刻只允许一个取快照调用在飞
        private bool _pollSuspended;                   // 通道已判死：常规轮询暂停，只试重连
        private DateTime _nextRecoverUtc;
        private DateTime _lastRecoverFailLogUtc;
        private int _recoverAttempts;

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
                    // 初始化这次取快照也走超时：万一它在启动时就挂住，不能让轮询定时器一直起不来
                    // （挂住的话把 _initialized 留在 false，第一次成功取到快照时只建水位线、不弹通知）。
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

                // 挂到字段上：后台循环由闭包持有，本类则负责在 Dispose 时把它关掉。
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
                        }
                        catch (Exception ex)
                        {
                            // 退出时 listener 被 Stop/Close，取上下文必然抛异常 —— 那是正常收尾，不当错误报。
                            if (listener.IsListening) Logger.Error("[HTTP接口] 消息解析或处理异常", ex);
                            else Logger.Debug($"[HTTP接口] 监听已停止，接收循环退出：{ex.GetType().Name}");

                            // 出错也必须给发送端一个响应：以前异常路径直接跳过响应写入，
                            // 客户端会一直挂到超时（keep-alive 下还会连累同一条连接上的下一条请求）。
                            try { if (ctx != null) ctx.Response.StatusCode = 500; } catch { }
                        }
                        finally
                        {
                            // "每个请求一定有响应"的保证放在 finally：正常 / 异常 / 退出三条路都要关掉它。
                            try { ctx?.Response.Close(); } catch { }
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

        /// <summary>
        /// 轮询诊断打点：同一状态只写一次日志（状态变了才写）。
        /// 轮询每 2s 一次，逐次输出会淹没日志，所以这里按"状态迁移"记录。
        /// </summary>
        private void LogPollDiag(string diag, bool isError = false)
        {
            if (diag == _lastPollDiag) return;
            _lastPollDiag = diag;
            if (isError) Logger.Warn($"[通知轮询] {diag}");
            else Logger.Info($"[通知轮询] {diag}");
        }

        /// <summary>
        /// 记住一批通知 ID（仅供"计数器回退护栏"使用）。容量有界，FIFO 淘汰最早的。
        /// </summary>
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

        /// <summary>
        /// 带超时地取一次通知快照。TimedOut=true 表示调用压根没返回 —— 这就是"通道挂死"的判据
        /// （权限被撤销 / API 静默失败的表现是**返回空列表**，走的是另一条分支）。
        /// </summary>
        private async Task<(bool TimedOut, IReadOnlyList<UserNotification>? Result, Exception? Error)> GetSnapshotAsync(int timeoutMs)
        {
            var listener = _listener;
            if (listener == null) return (false, null, null);

            try
            {
                var opTask = listener.GetNotificationsAsync(NotificationKinds.Toast).AsTask();
                var finished = await Task.WhenAny(opTask, Task.Delay(timeoutMs));
                if (finished != opTask) return (true, null, null);   // 超时：这个调用会被永久搁置，不再等它
                return (false, await opTask, null);
            }
            catch (Exception ex)
            {
                return (false, null, ex);
            }
        }

        /// <summary>
        /// 通道自愈：重试一次取快照；只有访问权限不是 Allowed 时才重新申请访问
        /// （Allowed 状态下反复调用 RequestAccessAsync 可能弹出系统授权框，不做）。
        /// 成功即恢复轮询并把水位线重置为当前快照的最大 ID。
        /// </summary>
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

            // 重连成功：水位线取当前快照的最大 ID，失效期间的通知按"错过"处理（与重启同语义）
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

        /// <summary>重连失败的日志节流：首次立刻落一条 WARN，之后最多每 10 分钟一条（状态没变化时不刷屏）。</summary>
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
            // 心跳：每 30 轮（≈60s）一行。若调用挂死，"上次读到快照"的秒数会一路变大；
            // 若调用正常但快照冻结，秒数会一直是 1~2s，而 maxId 停住不动。
            if (++_pollRounds % HeartbeatRounds == 0)
            {
                var snapshotTicks = LastSnapshotUtcTicks;
                var agoText = snapshotTicks == 0
                    ? "从未"
                    : $"{(DateTime.UtcNow - new DateTime(snapshotTicks, DateTimeKind.Utc)).TotalSeconds:F0} 秒前";
                Logger.Info($"[通知轮询] 心跳：上次读到快照 {agoText}，count={_lastSnapshotCount}，maxId={_lastSnapshotMaxId}，"
                          + $"通道状态={(_pollSuspended ? $"已失效(重连中，第 {_recoverAttempts} 次)" : "正常")}");
            }

            // 通道已判死：常规轮询先停下，只按节奏重连 —— 否则每次 tick 都会再发起一个永不返回的调用。
            if (_pollSuspended)
            {
                if (DateTime.UtcNow >= _nextRecoverUtc) await TryRecoverAsync();
                return (null, "通知通道疑似失效，等待重连");
            }

            // 上一个调用还没回来：本轮不重复发起（挂死时也最多只留一个待决调用，不会每 2s 堆一个）。
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
                    // 与权限失效导致的静默空列表（微软文档明确：权限被撤销时 API 不抛异常，只返回空）。
                    // 只有后者才是故障，所以顺带读一次 GetAccessStatus 作为判据。
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

                // 计数器回退护栏：快照最大 ID 落到水位线以下、而且这个 ID 我们从没见过 ——
                // 只可能是通知平台的 ID 计数器被重置了。此时若不处理，"水位线只升不降"会让
                // 之后所有新通知永久被忽略（永久静默）；这里把水位线拉回当前最大 ID 即可恢复捕获。
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
                    // 提取失败时水位线已经推进过，这条通知不会再被处理 —— 日志在 ExtractToastData 里。
                }
                else
                {
                    // 水位线没前进：快照里的通知都是已经见过的（最常见的是通知中心留着旧通知）。
                    // 如果用户明确收到了新消息却停在这一行上不去，就说明通知 ID 没有单调递增。
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
                    // best-effort process identifier: prefer AppUserModelId, fall back to display name
                    ProcessName = appInfo?.AppUserModelId ?? appName
                };
            }
            catch (Exception ex)
            {
                // 这里原本是裸 catch：WinRT 对象失效 / 属性访问异常都会让通知无声消失。
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
        // 三个文本字段都是**单行显示**（灵动岛每个字段只画一行），所以在赋值入口就归一化：
        // 换行（\r\n）、制表符等控制字符与连续空白折叠成一个空格，并去掉首尾空白。
        // 系统通知的正文常自带换行（一个 textElement 内部就含 \r\n），原样交给 Skia 会把换行
        // 当缺字画成一个方块 —— 就是「显示乱码」；它还会把文本宽度测量撑成错误的值。
        private string _appName = "";
        private string _title = "";
        private string _body = "";

        public string AppName { get => _appName; set => _appName = NormalizeText(value); }
        public string Title { get => _title; set => _title = NormalizeText(value); }
        public string Body { get => _body; set => _body = NormalizeText(value); }
        public string Aumid { get; set; } = "";
        public uint NotificationId { get; set; }
        public UserNotification? InternalNotification { get; set; }
        // best-effort process name for display
        public string ProcessName { get; set; } = "";

        /// <summary>
        /// 把多行文本压成单行：控制字符（含 \r \n \t）与空白一律当分隔符，连续多个只留一个空格，
        /// 首尾空白直接丢掉。emoji 的代理对不受影响（逐 char 追加，顺序不变）。
        /// </summary>
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