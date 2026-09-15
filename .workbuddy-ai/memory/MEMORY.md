# NotchPeninsula 项目长期笔记

## 项目定位
C# / .NET 10 的 Windows 托盘「灵动岛」程序（Win32 分层窗口 + SkiaSharp 自绘，60FPS 常驻渲染，稳态零 GC 分配）。`Renderer.cs` 负责全部绘制，`NotchWindow.cs` 负责窗口/消息/尺寸动画，`ConsoleWindow.cs` 是设置面板。

## 插件系统架构（关键接线位置）
- `Plugin/PluginApi.cs`：冻结的公共接口（INotchPlugin / IPluginHost / IWidget / IDetailPage / ISecondaryWidget / ISettingsPage / IPluginWindow / WidgetHit / WidgetFrame / RenderTheme）。
- `Plugin/PluginHost.cs`：注册中心 + 服务入口。按 PluginId 归组登记，`UnregisterPlugin` 必须摘干净所有引用（否则可回收 ALC 回收不掉 → 热重载泄漏）。插件返回的字符串要经 `DetachString` 复制到宿主堆。
- `Plugin/PluginManager.cs`：发现/加载/卸载/影子拷贝 + 注册表持久化（启用状态、顺序表）。单例 `PluginManager.Instance`。
- `Plugin/PluginLoader.cs`：扫描 `plugins` 目录（支持单文件与目录布局 + plugin.json）。
- `Renderer.cs` 里的「🧩 插件组件渲染接线」段：组件快照按 `WidgetsVersion` 缓存，`_pluginSlots` 复用命中矩形，插件异常一律熔断。
- `NotchWindow.cs`：WM_LBUTTONDOWN / WM_RBUTTONDOWN / WM_MOUSEMOVE 里分发插件交互。

## 已开放 vs 未开放的插件能力
- 已开放：主显示组件 IWidget、详情页 IDetailPage、ScheduleRefresh、PostReminder、GetSetting/SetSetting、CreateWindow。
- 未开放（接口已冻结但主程序未渲染）：设置页 ISettingsPage/ICustomSettingsPage、副显示组件 ISecondaryWidget。

## 详情页（2026-09-15 开放）
- 状态由 `PluginHost` 持有（`ActiveDetailPage` / `OpenDetailPage` / `CloseDetailPage` / `ToggleDetailPage`）。
- 尺寸由插件 `MeasureWidth/MeasureHeight` 决定，宿主裁剪为宽 180~1000、高 48~480；`WINDOW_WIDTH`/`MAX_WINDOW_HEIGHT` 会随之变化，底层缓冲自动重建。
- 交互：右键组件展开、岛内再右键或点岛外收起；岛内左键走 `Renderer.DispatchDetailPageClick` → `IDetailPage.HitTest/OnAction`；Toast 优先级高于详情页。

## 构建 / 环境
- 本机 SDK：`dotnet 10.0.401`，目标框架 `net10.0-windows10.0.19041.0`。
- **注意**：在本机 WorkBuddy 的 bash 里直接 `dotnet build` 会报 NuGet `Value cannot be null (Parameter 'path1')`，因为环境缺 `ProgramData` / `ALLUSERSPROFILE` / `APPDATA` / `ProgramFiles`。编译前先补齐这些变量。
- `NotchPeninsula.csproj` 已用 `<Compile Remove="TestPlugin\**" />`（及 PluginSample）把插件工程排除在主程序编译之外，新增插件工程要照抄这段。
- 插件工程用 `ProjectReference` 引用主程序，并用 `InstallToHostPlugins` 目标把 dll 拷进 `bin/$(Configuration)/$(TargetFramework)/plugins`。

## 约定
- 代码注释与文档均为中文；注释习惯解释「为什么这么做」，不只是「做了什么」。
- 渲染路径追求零 GC：不要每帧分配、不要缓存原生 SKPaint 跨线程复用（插件侧硬性要求）。
