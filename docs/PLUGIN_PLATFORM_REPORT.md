# NPS 插件平台实现报告

> 对应 Issue #9「将 NPS 扩展为『一切皆小组件』的开放插件平台」。
> 从「媒体信息展示软件」到「开放插件平台」的完整实现记录。

---

## 1. 目标与背景

NotchPeninsula（NPS）原本是一个 Win32 + SkiaSharp 实现的 Windows 刘海屏/灵动岛组件，只展示媒体状态、通知和音频频谱，能力相对封闭。

Issue #9 提出：把 NPS 改造成一个**「一切皆小组件」的开放生态**，提供插件/小组件框架，让社区能开发「大学课表」「DSH 状态」等自定义组件。

本报告记录该设想的完整落地。

---

## 2. 架构总览

```
┌─────────────────────────────────────────────────────────────┐
│                      主程序 (NotchPeninsula.exe)              │
│                                                             │
│  PluginLoader ── 扫描 plugins/ 目录，AssemblyLoadContext 加载 │
│  PluginHost   ── 注册中心（组件/设置页/窗口）+ 服务入口         │
│  NotchWindow  ── 主刘海窗口：组件行渲染 + 交互路由             │
│  Renderer     ── 统一绘制（组件行/详情页/Toast）               │
│  ConsoleWindow── 设置窗口：组件顺序 + 每插件独立设置页          │
│  PluginWindow ── 通用插件窗口基础设施                          │
└─────────────────────────────────────────────────────────────┘
        ▲                      ▲
        │ 公共契约              │ DLL
   PluginApi.cs          SamplePlugins/（Hello + SystemPlugins）
```

**核心原则**：插件不持有窗口、不持有线程，只实现接口并交回「页对象」（绘制/命中/测量回调），主机在自己的循环里调用它们；插件通过 `IPluginHost` 反向拿服务。

---

## 3. 插件 API（公共契约）

全部定义在 `Plugin/PluginApi.cs`：

| 接口 | 作用 |
|---|---|
| `INotchPlugin` | 插件入口（`Initialize(host)`） |
| `IWidget` | 主显示区组件（测量/绘制/命中/交互） |
| `IDetailPage` | 详情页（绘制/命中/动作） |
| `ISettingsPage` | 声明式设置页（Toggle/Choice/Number 控件） |
| `ICustomSettingsPage` | 自定义设置页（自绘 UI + 鼠标处理） |
| `IPluginWindow` | 插件自有窗口（SkiaSharp 绘制 + 输入） |
| `IPluginHost` | 主机服务（注册/刷新/设置/提醒/窗口/主题） |
| `SettingControl` | 声明式控件（`ToggleSetting`/`ChoiceSetting`/`NumberSetting`） |
| `WidgetFrame` | 每帧渲染上下文（主题/透明度/位移/频谱/悬停） |
| `RenderTheme` | 主题快照 |
| `ReminderData` | 提醒数据 |
| `WidgetHit` | 命中结果 |

### `IPluginHost` 能力一览

- `RegisterWidget` / `RegisterSettingsPage` / `RegisterSecondaryWidget`
- `ScheduleRefresh(interval, callback)` — 后台定时回调（防重入）
- `event SettingsChanged` — 设置变更事件（即时响应）
- `GetSetting` / `SetSetting` — 设置持久化（自动加 `Plugin.<id>.` 前缀）
- `PostReminder` — 发提醒（复用 Toast 流）
- `CreateWindow` — 创建插件自有窗口
- `OpenDetailPage` / `CloseDetailPage` / `RequestRedraw` — 交互调度
- `CurrentTheme` — 主题快照

---

## 4. 完整能力清单

| 能力 | 状态 | 说明 |
|---|---|---|
| DLL 插件加载 | ✅ | `AssemblyLoadContext` 从 `plugins/<id>/` 加载 |
| 组件行 | ✅ | 统一渲染，媒体/时间/资源/插件并存，动态宽度 |
| 组件排序 | ✅ | 注册表 `WidgetOrder`，设置页 ▲▼ 按钮 |
| 组件启用/停用 | ✅ | 注册表 `DisabledWidgets`，设置页对勾开关 |
| 详情页 | ✅ | 右键打开，可交互（时间钟表 / 资源曲线 / 媒体播放控制） |
| 行内媒体控制键 | ✅ | 悬停显示上一首/播放/下一首，可点击 |
| 独立设置页 | ✅ | 每插件一个，Toggle/Choice/Number + 自定义 UI |
| 创建窗口 | ✅ | `IPluginWindow`（独立消息循环 + SkiaSharp + 输入） |
| 提醒页 | ✅ | `PostReminder` → 复用 Toast 展示 |
| 主动刷新 | ✅ | `ScheduleRefresh`，间隔可配 |
| 设置即时生效 | ✅ | `SettingsChanged` 事件 |
| 全部组件都是插件 | ✅ | 时间/资源/媒体/Hello 都是 DLL |

---

## 5. 提交历史

| Commit | 内容 |
|---|---|
| `2ba0859` | Plugin API 契约 + Renderer 重构 + 示例插件 DLL |
| `2a3be89` | 插件详情页：右键路由 + IDetailPage 渲染 |
| `ed5980d` | 插件设置页框架：ConsoleWindow「插件」Tab + 声明式控件 |
| `6fb84b0` | 内置组件全部拆成 DLL 插件 + 组件行合并重构 + 排序 |
| `ebf0a4a` | 系统组件详情页：媒体播放控制 + 系统资源实时曲线 |
| `a7e3716` | 组件启用/停用开关 + 时间日期详情页钟表 |
| `de15a51` | 设置页扩展：自定义 UI + 全控件 + 窗口创建 + 每插件独立设置页 |
| `2b1adf7` | 系统组件设置页 + 采样间隔可配置 + 颜色重置修复 |
| `53cd320` | 清理 Renderer 死代码（约 800 行） |
| `0886764` | 设置即时生效：SettingsChanged 事件 |
| `3a70b98` | 行内媒体控制键：悬停显示播放控制并支持点击 |

---

## 6. 如何写一个插件

一个最小插件 = 一个 DLL 项目 + 实现 `INotchPlugin` + 注册组件。

```
plugins/
  MyPlugin/
    plugin.json     → { "id": "my", "name": "我的插件", "dll": "MyPlugin.dll" }
    MyPlugin.dll    → 引用主程序（或共享 Abstractions 程序集）
```

```csharp
public sealed class MyPlugin : INotchPlugin
{
    public string Id => "my";
    public string DisplayName => "我的插件";
    public string Version => "1.0.0";

    public void Initialize(IPluginHost host)
    {
        host.RegisterWidget(new MyWidget(host));          // 组件
        host.RegisterSettingsPage(new MySettingsPage());  // 设置页
    }
}
```

- **数据源自取**：后台线程 / `ScheduleRefresh` / WebSocket / 定时器，任意方式，无沙箱限制。
- **线程安全**：后台写、渲染线程读的数据要 `volatile`/`lock`（见 `IWidget` 接口注释）。
- **参考实现**：`SamplePlugins/HelloPlugin`（文本/详情/设置/窗口/提醒演示）、`SamplePlugins/SystemPlugins`（时间/资源/媒体，自包含 Win32/SMTC）。

---

## 7. 已知限制与后续方向

1. **逐组件 hover**：当前 `isHovered` 是窗口级（鼠标在刘海任意位置即 hover），控制键会在整个刘海悬停时显示，而非精确到单个组件。可后续加逐组件命中检测。
2. **媒体行内无封面**：行内媒体组件只有文字 + 控制键，封面缩略图只在详情页显示。
3. **采样间隔重启生效**：硬件/媒体采样间隔在插件启动时读一次，改了要重启（`SettingsChanged` 已具备，未接入重调度）。
4. **副显示区（任务栏）未做**：Phase 4 计划项，独立窗口 + 布局，长期延后。
5. **组件开关与旧设置未整合**：旧的「组合模式/待机显示内容」设置已被组件行取代，未迁移成统一的「组件开关」配置。

---

## 8. 结论

从 Issue #9 的设想出发，NPS 已经从一个「媒体信息展示软件」完成了向「一切皆小组件」开放插件平台的关键跨越：**外部 DLL 加载、组件行、排序、启用停用、详情页、独立设置页、自定义 UI、创建窗口、提醒、主动刷新、设置即时生效**，全部落地，且**时间/资源/媒体等内置功能也全部拆成了 DLL 插件**，与社区插件走完全相同的 `IWidget` 契约。课表、DSH 状态等自定义插件现已可直接开发。
