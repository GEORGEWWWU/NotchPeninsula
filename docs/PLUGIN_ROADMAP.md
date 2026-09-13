# NPS 插件化平台实现计划

> 对应 Issue #9：「一切皆小组件」开放插件平台。
> 状态：草案（待社区对齐后进入 Phase 1）。

## 总原则

- 每个阶段结束都是一个**可运行、且行为与现状一致**的版本。
- 把内置功能先改造成「第一批插件」，用它们验证 API 是否足够通用。
- 全程继续 **纯 Win32 + SkiaSharp**，不引入 WPF/WinForms 控件体系。
- 守住「极致性能」底线：把「零 GC / 复用静态画笔 / 不每帧 new」写成插件开发硬性约定。

---

## Phase 0 — 冻结 API 契约（只写接口，不接线）

**目标**：定死插件接口，供社区评审，避免后续返工。

**交付**：`Plugin/PluginApi.cs`（纯接口 + 数据 record，不引用现有实现）。

包含：

- `INotchPlugin` 插件入口
- `IWidget` 主显示区小组件（测量 / 绘制 / 命中 / 交互四段）
- `IDetailPage` 详情页
- `ISettingsPage` + `SettingControl`（声明式控件：Toggle / Choice / Number）
- `ISecondaryWidget` 副显示区小组件（占位，Phase 4）
- `ReminderData` 提醒
- `IPluginHost` 主机服务 + 页注册
- 服务接口 `IMediaService` / `IHardwareService` / `IToastService`

**验收**：`dotnet build` 通过；接口文件独立可评审。

---

## Phase 1 — 渲染重构为「小组件模型」（核心，风险最高）

**目标**：把 `Renderer.Draw()` 的巨型 if、`NotchWindow.RenderLoop()` 的硬编码状态机，重构成「遍历 widget 列表」。

**改动文件**：

- `NotchWindow.cs`：新增 `PluginHost` 实例；`RenderLoop` 里 `expectedTargetWidth/Height` 的 switch（约 483-507 行）换成 `ComputeWidgetRowWidth()` 遍历 widget `MeasureWidth`。
- `Renderer.cs`：4 个内置状态（媒体 / 时钟 / 硬件 / 通知）抽成 4 个内置 widget 类，各自实现 `MeasureWidth` / `Draw` / `HitTest`；`Renderer.Draw` 降级为「遍历 widget、算 rect、调 Draw」的调度器。
- 新增 `Plugin/WidgetLayout.cs`：布局引擎（测量 → 布局 → 记录 rect → 绘制），rect 存原子快照供命中检测用。

**关键步骤**：

1. 抽 `IMediaService`（包 `MediaController` 的 Title/Artist/Thumbnail/IsPlaying 等）、`IHardwareService`（包 `Renderer.UpdateHardwareStats`）、`IToastService`（包 `ToastNotificationListener`）。
2. 写 4 个内置 widget：`MediaWidget` / `ClockWidget` / `HardwareWidget` / `ToastWidget`，把现有绘制代码原样搬入（只改坐标来源，不改画法）。
3. `RenderLoop` 和 `WndProc` 的坐标魔法数字，改成从 widget rect 快照读取。

**验收**：运行后视觉效果与交互与现在完全一致（对照截图逐屏比对）；组合模式、穿透、展开、歌词卡拉OK 均不回归。

**风险**：纯重构无新功能，易出「看着一样但坐标偏几像素」的 bug。每个 widget 单独提交，坏一个回滚一个。

---

## Phase 2 — 交互增强（右键路由 + 详情页泛化）

**目标**：落地「特殊按键」——右键按归属组件开详情，左键透传。

**改动文件**：

- `NotchWindow.cs` `WndProc` 的 `WM_RBUTTONDOWN`（现在固定 `ConsoleWindow.Toggle()`，约 822-827 行）：遍历 widget rect 快照，命中哪个调哪个 `OnRightClick`；空白区右键仍开设置。
- 泛化 `Renderer.cs`（约 947-964 行）的 alpha=1 隐形热区技巧，让每个 widget 声明自己的可点击热区（支持穿透模式下右键/局部点击）。
- 把 `IsMediaExpanded` 展开分支（`Renderer.cs` 约 679-766 行）泛化为 `ActiveDetailWidget` 宿主。
- 内置 `MediaDetailPage`：封面 + 歌词 + 播放控制（把现有展开态搬入）。

**验收**：媒体小组件右键 → 原地展开详情；空白处左键照常透传；托盘 / 空白区右键仍能进设置。

**待定**：右键开详情后「打开设置」入口保留在哪（托盘 / 空白区右键 / 两者）。

---

## Phase 3 — 插件加载 + 设置页框架

**目标**：社区插件真正能跑起来。

**改动文件**：

- 新增 `Plugin/PluginLoader.cs`：`AssemblyLoadContext` 从 `%AppData%\NotchPeninsula\plugins\<id>\` 加载 DLL + 读 manifest，反射找 `INotchPlugin`，实例化 + `Initialize(host)`。
- 新增 `Plugin/SettingsRenderer.cs`：把 `ConsoleWindow` 写死的开关/下拉/步进器抽成「声明式控件渲染器」，从 `ISettingsPage.Controls` 驱动，命中 + 持久化走 `Program.SaveSetting`（加 `Plugin.<id>.` 前缀）。
- `ConsoleWindow.cs`：加「插件」tab，列出已注册 `ISettingsPage`。

**验收**：放入第三方插件 DLL，重启后设置页可见、主显示区能画 widget、右键能进详情、配置可持久化。

**风险**：`PublishSingleFile`（csproj 13-14 行）下加载外部 DLL 有细微坑，本阶段开头先做最小可加载 POC 验证。

---

## Phase 4 — 提醒页抽象 + 副显示区（延后）

- **提醒页**：把现有 Toast「4 秒消失 + 点击唤醒」抽象成 `PostReminder`。低成本，可提前到 Phase 2 后做。
- **副显示区**：全新第二个窗口 + 独立布局 + 独立交互，长期延后。

---

## 已定决策

- 一个组件对应一个详情页（`IWidget.DetailPage`）。
- 一个插件可注册多个组件（`IPluginHost.RegisterWidget` 可多次调用）。
- 数据来源由插件自行处理，主机不提供数据源服务。
- 插件支持主动刷新：`IPluginHost.ScheduleRefresh(interval, callback)` + `RequestRedraw()`。

## 全局待定决策

| 决策点 | 选项 | 建议 |
|---|---|---|
| manifest 格式 | JSON / XML | JSON（项目已全程用 System.Text.Json） |
| 详情触发键 | 右键 / 双击 | 右键 |
| 设置入口 | 托盘 / 空白右键 / 两者 | 两者都留 |
| 插件安全 | 信任 / 沙箱 | 第一版信任，文档明说 |

---

## 风险与回滚

1. **Phase 1 回归风险最高**：纯重构无新功能，逐屏比对验收；每 widget 单独提交。
2. **性能底线**：插件 API 把零 GC / 复用画笔写成硬性约定。
3. **每阶段独立可发布**：任一阶段卡住不影响已上线功能。

---

## 体量预估

| 阶段 | 预估 |
|---|---|
| Phase 0 | 1-2 天 |
| Phase 1 | 1-2 周（取决于回归强度） |
| Phase 2 | 3-5 天 |
| Phase 3 | 1 周 |
| Phase 4 | 另议 |
