# NotchPeninsula 内存 / 资源泄露审查报告

- **日期**：2026-09-26
- **范围**：全部 43 个 `.cs`（约 20 400 行），Core / Media / Notify / Plugin / Render / UI
- **方法**：静态审查。按「非托管资源生命周期 / 事件订阅配对 / 定时器 / 静态集合增长 / 插件 ALC 回收」五条线逐类扫描
- **结论**：**释放纪律整体优秀，没有"跑几小时就撑爆"的主动泄漏。** 找到 **1 处真实泄漏（慢速）** + **2 处隐患**。

---

## 0. 判断基准：SkiaSharp 2.88.8 的释放语义（关键前提）

`csproj` 锁定 **SkiaSharp 2.88.8**。这个版本和 3.x 的语义**完全不同**，是本报告所有结论的基准：

1. 它维护一张 `SKObject` 的全局**「native 指针 → 托管对象」注册表 + 引用计数**，**只有 `Dispose()` 才会 `DeregisterHandle`**。
2. `SKNativeObject` **自带终结器**（`~SKNativeObject() => Dispose(false)`），所以"临时创建、不 Dispose、丢掉引用"的对象**不是永久泄漏** —— GC 最终会在终结器里释放它。
3. **真正的泄漏条件是两者叠加：对象被 `static` 字段强引用（永远可达）→ 终结器永不运行 → 引用计数永不归零。**

> 用 C++ 打个比方：这相当于**没有 RAII 的裸 `SkRefCnt`**，`using` 就是手写 `unref()` 配对；
> 而"强引用它却不管"等价于故意 leak 一个 `sk_sp`，让 `SkRefCnt` 永远停在 1。

**必须区分对待的两类写法：**

| 写法 | 判断 |
|---|---|
| `private static readonly SKPaint _bgPaint = new(...)` | ✅ **刻意的进程级复用**，不是泄漏，别去"修" |
| 运行期 new 出来的 shader / typeface / bitmap / path | ⚠️ 必须有归宿（`using` 或显式 Dispose） |

---

## 🔴 发现 1（真泄漏，慢速）：`Render/LyricsFont.cs` 缓存的 SKTypeface 永久可达

**位置**：`_perCp` 声明在 `LyricsFont.cs:36`，写入在 `Resolve()`（第 87 行）

**问题**：第 22–27 行的注释写着「只持有系统已安装字体的引用…没有可泄漏的非托管内存，也不需要 Dispose」。**在 2.88.8 下这个论断不成立**：

- `Match()`（第 135–146 行）调用 `SKFontManager.Default.MatchCharacter(...)`，**返回的是托管方拥有的新对象**（内部会 `Treasure()` 抬引用计数）；
- `Lookup()` 第③步（第 122 行）的 `SKTypeface.FromFamilyName(family)` 同样是新对象。

这些对象被存进 `static readonly Dictionary<int, SKTypeface?> _perCp` → **成为 GC root 上的强引用** → 按第 0 节第 3 点，**终结器永不运行，native 引用计数永不归零**。

**另外 `_perCp` 没有容量上限**，只在基础字体变化时才 `Clear()`，属于"只增不减"。每一条目是永久驻留的托管对象 + 一份永不释放的 native 引用。

**实际危害等级：低。** 触发条件是"基础字体给不出字形的**新**码点"（韩文谚文 / 泰文 / 天城文…），一首多语言歌最多几十次解析，稳态 60 FPS 下几乎不被触碰。所以这是**慢速、量级很小**的泄漏，不是急症 —— 但性质上确实是"只增不减"。

### ⚠️ 修法警告：不要简单地给缓存加"淘汰时 Dispose"

这是最容易踩反的坑。在 SkiaSharp **2.x** 里，实例注册表会让 `MatchCharacter` / `FromFamilyName` **返回同一个托管对象**（只要 native 指针相同）。因此**手动 `Dispose()` 一个可能与他人共享的 typeface，会让别处（如 `FontConfig.Normal`、某支静态画笔）手里的对象变成已释放状态，下一帧绘制就是 use-after-dispose**。

**推荐修法：让缓存不再强引用 typeface。** 值改成弱引用，并给负缓存和容量各配一个"无资源"的容器：

```csharp
// 值改为弱引用：静态字典不再是 typeface 的 GC root —— GC 会在终结器里
// 释放 native 引用计数，不需要（也绝不能）手动 Dispose 共享实例。
private static readonly Dictionary<int, WeakReference<SKTypeface>> _perCp = new(96);
private static readonly HashSet<int> _noFace = new();        // 负缓存：只存 int，零资源
private static readonly Queue<int> _cpOrder = new(96);       // 容量兜底
private const int PerCpCap = 512;

internal static SKTypeface? Resolve(int cp, SKTypeface baseTypeface)
{
    if (!ReferenceEquals(baseTypeface, _baseFace))
    {
        _baseFace = baseTypeface;
        _perCp.Clear(); _noFace.Clear(); _cpOrder.Clear();
        _widths.Clear(); _widthOrder.Clear();
    }

    if (_perCp.TryGetValue(cp, out var wr) && wr.TryGetTarget(out var alive))
        return alive;                       // 还没被 GC 收走 → 直接命中
    if (_noFace.Contains(cp)) return null;  // 负缓存命中

    var face = Lookup(cp, baseTypeface);    // 新对象，所有权归本方法
    if (face == null) { _noFace.Add(cp); _perCp.Remove(cp); return null; }

    if (_perCp.Count >= PerCpCap && _cpOrder.Count > 0)
        _perCp.Remove(_cpOrder.Dequeue());  // 只摘引用，不 Dispose（关键）
    _perCp[cp] = new WeakReference<SKTypeface>(face);
    _cpOrder.Enqueue(cp);
    return face;
}
```

若嫌弱引用会导致"偶发重解析"，另一种等价安全的做法是**把缓存值换成字体族名 string**（`_perCp` 改存 `string`），族名 → face 走一张有界表，换基础字体时统一释放。两者都不引入悬挂风险，**区别只是弱引用偶尔要重查一次系统字体服务**。

---

## 🟡 发现 2（脆弱点，当前无害）：`Media/AudioAnalyzer.cs:383` 的匿名委托无法退订

```csharp
volume.OnVolumeNotification += _ => EnsureCaptureAlive();   // 匿名 lambda，永远退不掉
```

`Dispose()`（第 100–115 行）只对**最后一次**的 `_enumerator` 调 `UnregisterEndpointNotificationCallback`，而上面这个 lambda 从未 `-=`。

**当前实际影响 ≈ 0**：`SubscribeSystemEvents()` 只在初始化路径调用一次（`AudioAnalyzer.cs:77`），且 `AudioAnalyzer` 随进程存活。所以它是**潜伏的脆弱点，不是现存 bug**：将来只要有人在"设备切换"路径里再调一次 `SubscribeSystemEvents`，就会**累积订阅 + 漏掉旧 enumerator 的反注册**。

**修法**：换具名方法并保存委托，Dispose 里配对退订：

```csharp
private Action<object, float>? _volumeHandler;   // 字段持有，才能 -=

_volumeHandler = (_, _) => EnsureCaptureAlive();
volume.OnVolumeNotification += _volumeHandler;

// Dispose() 中：
if (_endpointVolume != null && _volumeHandler != null)
{
    try { _endpointVolume.OnVolumeNotification -= _volumeHandler; } catch { }
    _volumeHandler = null;
}
```

---

## 🟡 发现 3（逻辑隐患，非泄漏）：`Media/MediaController.cs:360` 用 AppID 当字典 key

`_watchedSessions`（第 129 行）以 **AppID 为 key** 记录"我已经订阅过哪些会话"，`SyncPlaybackWatchers` 靠它做差集来解绑消失的会话。

**问题**：如果系统把**同一个 AppID 的会话替换成一个新的 COM 对象**（同一 App 的新会话实例，AppID 不变），字典会认为"这个 AppID 已经订阅过了" → **既不解绑旧对象，也不订阅新对象**。

这**不是内存泄漏**（旧 RCW 没人引用会走 GC），但会**永久漏掉该 App 的 `PlaybackInfoChanged` 事件** —— 表现为"某个软件偶发切歌状态不刷新"，而且是静默失效、极难排查。

**修法**：key 不只用 AppID，或校验引用一致性：

```csharp
// 存值已经有了 session，直接比对引用即可
if (!_watchedSessions.TryGetValue(id, out var watched) || !ReferenceEquals(watched, sessions[i]))
{
    watched?.PlaybackInfoChanged -= OnPlaybackInfoChanged;   // 旧对象先摘（幂等）
    sessions[i].PlaybackInfoChanged += OnPlaybackInfoChanged;
    _watchedSessions[id] = sessions[i];
}
```

> 这条建议单独跑一次回归：切歌状态在各个 App 上是否都还能实时刷新。

---

## ✅ 已确认无问题的部分（附证据，避免重复怀疑）

| 项目 | 证据 |
|---|---|
| **GDI / 位图句柄** | `SelectObject(memDc, oldBitmap)` → `DeleteObject(hBitmap)` → `DeleteDC(memDc)` → `ReleaseDC(IntPtr.Zero, screenDc)` 在 NotchWindow(531/878/2066)、PluginWindow(574)、ConsoleWindow(1181)、TrayMenuWindow(721)、UpdateManager(336) **五处全部成套** |
| **SKSurface 释放顺序** | 绑定在 `pBits` 上的 `SKSurface` 先于 DIB 释放 —— 顺序正确（PluginWindow:572 / NotchWindow:528） |
| **定时器** | 一律「具名方法 + `Stop()` + `-=` + `Dispose()`」；`TrayMenuWindow` 每次弹出都 new 实例，也做了三件套（第 357–362 行，注释还写明了"不严格释放就是每开一次菜单泄漏一个窗口对象"） |
| **SMTC 事件** | 换会话时先 `_currentSession.MediaPropertiesChanged -= ...`（MediaController:334）；播放状态由 `SyncPlaybackWatchers` 用字典做差集统一解绑（除发现 3 的 key 问题外，机制本身正确） |
| **音频事件** | `DataAvailable` / `RecordingStopped` 在 `ReleaseCapture()` 里配对 `-=` 后 `Dispose()`（AudioAnalyzer:173–175） |
| **WebSocket / CTS** | `Stop()` 用 `ContinueWith(_ => cts?.Dispose())` 确保 CTS 在循环任务结束后才释放（JustSoloLyricClient:102）—— 这个细节很多人写错 |
| **插件系统 ALC** | collectible ALC + 影子拷贝 + `DetachString` 防 loader heap 钉住 + `WeakReference` + `GC.Collect/WaitForPendingFinalizers` 验证回收（PluginManager:705–748）；`[MethodImpl(NoInlining)]` 还专门规避了 Debug 下局部变量延长 ALC 寿命的坑 —— **这是全项目最见功力的一段** |
| **插件窗口登记表** | `PluginWindow._windows` / `PluginHost._windows` 都有 `DetachWindow` 注销，不会无限增长 |
| **字体生命周期** | `FontConfig.SwitchFont` 铁律「**先 `Changed?.Invoke()` 重绑画笔，再 `DisposeFaces(oldFaces)`**」，ttc 里多余字重当场释放，失败路径全释放 —— 时序完全正确 |
| **字体切换的悬挂防护** | `ApplyFont` 里 `_cachedToast*Runs.Clear()` / `_karaokeRuns*.Clear()`（Renderer.Paint:193–201），因为那些 run 里存着 SKTypeface |
| **Shader 重建** | `_fadePaint.Shader?.Dispose()` 在赋新值之前（Renderer.Paint:121）✅ |
| **缓存容量** | ToastIconProvider 24 条（且淘汰时**故意不 Dispose** 以防 use-after-free，是合理取舍）/ `LyricsFont._widths` 64 / 提示音队列 4 / `_seenIds` FIFO —— 全部有界 |
| **静态事件** | `Program.cs:121` 的 `SystemEvents.UserPreferenceChanged += lambda` 只捕获**静态**成员，不钉住任何实例 ✅ |
| **单例窗口** | ConsoleWindow 静态 `_instance` 只 new 一次（ConsoleWindow.cs:692/718），无重复创建 |

### 顺带一提（不是泄漏，是 GC 抖动）

`ConsoleWindow.Render.cs` 的 150 处 `new` 里，绝大多数是 `SKRect` / `SKColor` **结构体**（栈上，零堆分配），只有 3 处是 `SKPaint` / `SKPath` 且都用了 `using` —— 这个文件很干净。真正会在每帧产生垃圾的是 `MediaLogoProvider.GetLogo` 的 `bmp.Copy()`（调用方 `MediaController:477-490` 已正确 Dispose 旧值），调用频率是"媒体属性变化时"而非每帧，可接受。

---

## 建议的验证步骤

静态分析只能给"嫌疑"，**发现 1 需要实测确认**：

```bash
# 1) 看托管堆与私有内存是否单调上升
dotnet-counters monitor --process-id <pid> --counters System.Runtime

# 2) 抓两份堆快照（间隔听几首多语言歌：韩文 / 泰文 / 日文）
dotnet-gcdump collect -p <pid> -o before.gcdump
dotnet-gcdump collect -p <pid> -o after.gcdump

# 3) 比对 typeface 实例数是否持续增长
dotnet-dump analyze before.gcdump
> dumpheap -stat -type SkiaSharp.SKTypeface
```

判读要点：

- **托管堆 GC 后不回落 + `SKTypeface` 实例数单调增长** → 发现 1 成立；
- **托管堆回落但进程 Private Bytes 持续上升** → 是 native 侧（此时查 GDI 句柄数：任务管理器加"GDI 对象"列，应稳定在低位）；
- 修完后**同一个用例下 `SKTypeface` 实例数应当收敛到一个小常数**（只等于系统里实际用到的那几套字体）。

---

## 修复状态（2026-09-26 已实施）

三处均已改完，**仅动宿主侧文件，不涉及插件 API**，因此无插件兼容风险：

| # | 文件 | 改动摘要 |
|---|---|---|
| 1 | `Render/LyricsFont.cs` | `_perCp` 值由 `SKTypeface?` 改为 `WeakReference<SKTypeface>?`；新增 `_cpOrder` + `PerCpCap = 512` 容量兜底与 `Store()` 私有方法；占位/负缓存统一走 `null` 值。**同时改正了类注释里"不需要 Dispose"的错误论断。** |
| 2 | `Media/AudioAnalyzer.cs` | 新增 `_volumeNotificationHandler` 字段（具名委托）+ `_systemEventsSubscribed` 幂等守卫；`Dispose` 里补 `-=` 退订并把 `_enumerator` 置空。 |
| 3 | `Media/MediaController.cs` | `SyncPlaybackWatchers` 挂载分支改用 `ReferenceEquals` 判「是否为同一个会话对象」，对象被替换时先摘旧再挂新。 |

**未做编译验证**（按项目约定由 zzjjack 自行构建）。改动均为局部替换，无新增类型引用，
唯一外部依赖是 `NAudio.CoreAudioApi.AudioEndpointVolumeNotificationDelegate`
（已核对 `NAudio.Wasapi 2.2.1` 的 XML 文档确认该类型存在，且文件已 `using NAudio.CoreAudioApi`）。

---

## 优先级建议

| # | 位置 | 性质 | 建议 |
|---|---|---|---|
| 1 | `Render/LyricsFont.cs:36/87` | 真泄漏（慢速） | ✅ 已修：值改弱引用 |
| 2 | `Media/AudioAnalyzer.cs:383` | 脆弱点（当前无害） | ✅ 已修：具名委托 + 幂等守卫 |
| 3 | `Media/MediaController.cs:360` | 事件丢失（非泄漏） | ✅ 已修：引用比对重挂；**建议补一次多 App 切歌回归** |
