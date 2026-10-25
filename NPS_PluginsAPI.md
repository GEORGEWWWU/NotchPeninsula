# NotchPeninsula 插件开发文档

> 面向要从零写一个插件的人，也面向要查某个成员到底怎么用的人。
>
> 文中每个接口、默认值、上限都对着仓库里的源码核过。权威来源是这几个文件，文档与代码不一致时以代码为准：
>
> | 文件 | 内容 |
> |---|---|
> | `Plugin/PluginApi.cs` | 全部插件接口与数据模型（唯一的接口契约） |
> | `Plugin/WidgetKit.cs` | AutoWidth 协议与布局盒子 |
> | `Plugin/PluginLoader.cs` | 插件目录发现与入口 DLL 挑选 |
> | `Plugin/PluginManager.cs` | 加载 / 启用 / 禁用 / 重载 / 导入 / 移除 / 失败归类 |
> | `Plugin/PluginHost.cs` | 宿主侧实现（注册、刷新、提醒、设置、窗口） |

## 目录

- [1. 五分钟上手](#1-五分钟上手)
- [2. 加载、安装与生命周期](#2-加载安装与生命周期)
- [3. 工程配置](#3-工程配置)
- [4. 概念模型](#4-概念模型)
- [5. 第一个完整插件](#5-第一个完整插件)
- [6. 组件 IWidget](#6-组件-iwidget)
- [7. 详情页 IDetailPage](#7-详情页-idetailpage)
- [8. 常用能力](#8-常用能力)
- [9. 把插件暴露成 MSP 工具](#9-把插件暴露成-msp-工具)
- [10. 字体、主题与渲染上下文](#10-字体主题与渲染上下文)
- [11. 常见坑速查](#11-常见坑速查)
- [12. API 速查](#12-api-速查)
- [13. 排错](#13-排错)
- [附录 A：接入自己的媒体来源](#附录-a接入自己的媒体来源)
- [附录 B：术语对照](#附录-b术语对照)

---

## 1. 五分钟上手

### 1.1 插件是什么

NotchPeninsula 在屏幕顶部显示一条「灵动岛」。岛上的原生内容（时间、硬件、媒体）是写死的；**插件**让你用 C# 写一个类库，编译成 dll，往岛上加自己的内容。

一个插件 dll 里必须有**一个** `public`、非抽象、带无参构造函数的类实现 `INotchPlugin`。程序靠这一点识别「这是不是插件」。

插件能做的事，全部通过 `Initialize(IPluginHost host)` 拿到的那只 `host` 发起：

| 想做的事 | 调用 |
|---|---|
| 在岛上放一段内容 | `host.RegisterWidget(new MyWidget())` |
| 每 N 秒后台刷一次数据 | `host.ScheduleRefresh(...)` |
| 弹一条几秒的提示 | `host.PostReminder(...)` |
| 记住用户的设置 | `host.GetSetting / SetSetting` |
| 开一个自己的小窗口 | `host.CreateWindow(...)` |
| 展开 / 收起详情页 | `host.OpenDetailPage / CloseDetailPage` |

### 1.2 最小可运行插件

```csharp
using NotchPeninsula.Plugins;
using SkiaSharp;

namespace MiniPlugin;

public sealed class MiniPlugin : INotchPlugin
{
    public string Id => "com.example.mini";      // 反域名，全局唯一，发布后不要改
    public string DisplayName => "最小插件";
    public string Version => "1.0.0";
    public string Author => "你的名字";

    public void Initialize(IPluginHost host) => host.RegisterWidget(new MiniWidget());
}

internal sealed class MiniWidget : IWidget
{
    public string Id => "com.example.mini.widget";
    public string DisplayName => "Mini";
    public IDetailPage? DetailPage => null;

    public float MeasureWidth(float availableHeight) => 56f;   // 完整显示需要多宽（逻辑像素）

    public void Draw(SKCanvas canvas, SKRect rect, WidgetFrame frame)
    {
        using var paint = new SKPaint
        {
            IsAntialias = true,
            TextSize = 12.5f,
            Color = frame.Theme.TextColor.WithAlpha(frame.Alpha),
        };
        canvas.DrawText("Hi", rect.Left + 8f, rect.MidY + frame.TextOffsetY + 4.5f, paint);
    }

    public WidgetHit HitTest(float x, float y, SKRect rect) => WidgetHit.None;  // 不响应点击
    public void OnLeftClick(string? action, float x, float y) { }
    public void OnRightClick() { }
    public void OnActivate(IPluginHost host) { }
    public void OnDeactivate() { }
}
```

编译后把 dll 丢进 `plugins\`，重启程序（或在「我的插件」里点重载），岛上就多了一个 `Hi`。

这个例子用的是**老协议**（自己上报宽度）。新插件建议用 [6.2](#62-宽度协议autowidth推荐) 的 AutoWidth：宽度交给宿主量，少一半口径问题。

### 1.3 装进程序

三种方式，效果一样：

1. 把 `MyPlugin.dll` 复制进 **exe 同级的 `plugins\`** 文件夹。
2. 程序里「我的插件」→ 导入 → 选 dll（会自动复制进 `plugins\` 并立即加载）。
3. 程序里「设置中心 → 插件市场」直接装（最省事，见 [2.5](#25-插件市场)）。

---

## 2. 加载、安装与生命周期

### 2.1 目录布局与入口 DLL 的挑选

插件根目录固定是：**程序 exe 所在目录下的 `plugins\`**。

```
plugins\
├─ HelloPlugin.dll                 ← 单文件布局：没有第三方依赖时用这个
├─ MyPlugin\                       ← 目录布局：带依赖时用这个
│  ├─ MyPlugin.dll
│  ├─ SomeLibrary.dll
│  └─ plugin.json                  ← 可选：{ "dll": "MyPlugin.dll" }
└─ _recycle\                       ← 程序自己的回收站，启动时跳过
```

发现规则（`PluginLoader.Discover`）：

- 根目录下每个 `*.dll` 都是一个候选插件。
- 每个**子目录**是一个候选插件；以 `_` 或 `.` 开头的目录**直接跳过**（`_recycle` 就是这么排除的）。
- 子目录里挑入口 DLL 的顺序：
  1. `plugin.json` 里的 `"dll"` 字段（文件不存在会记警告，继续往下找）；
  2. 与目录同名的 `<目录名>.dll`；
  3. 目录里第一个「不是宿主自带组件」的 dll。
- 宿主自带组件（`NotchPeninsula` / `SkiaSharp` / `NAudio.*` / `System.Text.Json` 等）永远不会被当成插件加载，所以把依赖 dll 一起放在目录里是安全的。

> 一个目录里**只能有一个**入口 DLL。放多个插件请分多个目录 —— 否则只会加载第一个，其余被忽略。

### 2.2 影子副本：哪些文件会被复制

加载时宿主把插件**复制到临时目录再从那里加载**（`%TEMP%\NotchPeninsula\plugins\<guid>\`），所以 `plugins\` 里的文件不会被进程锁住，可以随时覆盖、删除、重装。

复制的白名单（**目录布局只看第一层**，**不复制子目录**）：

```
.dll  .json  .pdb  .config  .xml  .txt
```

由此得到两条硬约束：

1. **第三方依赖必须直接放在插件目录的一层**，不能塞进 `lib\` 之类的子目录 —— 子目录不会被复制，加载时就是「缺依赖」。
2. **图片 / 音频 / 数据库这类数据文件不会被复制**。`Assembly.Location` 指向的是临时影子目录，也别指望在那里读到它们。要带资源请用 `<EmbeddedResource>` 嵌进 dll，或首次运行时释放到 `%LOCALAPPDATA%\<你的插件>\`。

### 2.3 依赖如何解析

每个插件在自己的 `AssemblyLoadContext`（可回收）里加载，规则很简单：

- **宿主已经加载过的程序集，一律用宿主那份**（`SkiaSharp`、`NotchPeninsula` 本身等）。你自己目录里再放一份 `SkiaSharp.dll` 会被忽略 —— 不要试图自带，版本不一致只会更难查。
- 其余程序集从插件目录找同名 dll。

所以：**插件与宿主必须引用同一个 SkiaSharp 版本**（跟着主程序的包版本走），并且**不要**把 `NotchPeninsula.dll` 复制进插件目录。

### 2.4 启用 / 禁用 / 重载 / 移除

在「我的插件」里每行都有这几个动作：

| 动作 | 行为 | 落盘 |
|---|---|---|
| 启用 | 加载并运行 | 从「已禁用」清单移除 |
| 禁用 | 卸载并停止，下次启动也不加载 | 写进 `Plugins_Disabled` |
| 重载 | 卸载 → 重新发现 → 重新加载（热更新用） | — |
| 移除 | 卸载 → 文件移进 `plugins\_recycle\`（不是删除，可以手动找回） | 清掉顺序 / 隐藏项 |

列表里每行的 `运行中 · v1.0.0 · 作者` 来自入口类的 `Version` / `Author`。

### 2.5 插件市场

- 程序内：**设置中心 → 插件市场**（可浏览、搜索、分类筛选、装 / 更新 / 卸载 / 评分 / 看详情）。
- 浏览器：`https://nps.georgewu.top/market`。

市场对上传包认两种形态：

| 形态 | 落点 | 说明 |
|---|---|---|
| 单文件 `.dll` | `plugins\<文件名>` | 没有额外依赖时用 |
| `.zip` | `plugins\<市场 Id>\` | 解压后**摊平到同一层**（丢掉子目录结构） |

发布 zip 的注意事项：

- 目录名用的是**市场里那个插件的 Id**，不是 zip 里的文件名。
- zip 里 dll 放在根或任意一层子目录都行，安装时都会被摊平。
- **zip 里有多于一个 dll 时必须带 `plugin.json`**，写明 `{ "dll": "入口.dll" }`；否则宿主只能按「同名 → 第一个非宿主组件」猜，可能挑错。

市场里安装 / 更新都是**即时生效**的，不需要重启（宿主会把旧版本卸载、文件移入 `_recycle` 后再装新的）。

> 发布前请把 `Id` 定下来：市场靠 `Id` + 入口文件名 + 市场 slug 匹配本地版本，**发布后改 `Id` 会被当成另一个插件**，老用户的「更新」就失效了。

### 2.6 加载失败的六种原因

宿主会把失败归类，提示语直接告诉你该修哪里（`PluginFailureKind`）：

| 归类 | 通常的原因 |
|---|---|
| `NotAPlugin` | dll 里没有实现 `INotchPlugin` 的 public 类；或者你**自己复制了一份宿主接口**导致类型对不上 |
| `BadImage` | 不是 .NET 程序集 / 位数不对（x86 插件）/ 文件损坏 |
| `MissingDependency` | 依赖 dll 没一起放（尤其是放在子目录里，见 [2.2](#22-影子副本哪些文件会被复制)） |
| `HostMismatch` | 按旧版 API 编译，引用了宿主里已经不存在的类型或成员 |
| `InitFailed` | 构造函数或 `Initialize` 抛异常 —— 去日志看堆栈 |
| `Other` | 其他，错误原文在「我的插件」那一行里 |

最常见的三个：**目标框架写错**（必须与宿主一致）、**缺依赖**、**没实现入口类**。

### 2.7 「显示」与「启用」不是一回事

在 **设置中心 → 显示设置 → 显示内容** 那张列表里勾选 / 排序，只影响**是否画在岛上**和**先后顺序**：

- 取消勾选 = 从岛上收起来，**插件照常运行**（定时刷新、提醒、后台能力都不受影响）；
- 勾上显示会自动启用它；禁用插件会自动把它收起来。

对插件作者的含义：**`IWidget` 被隐藏时依然活着**，别把「岛上看不到」当成「插件被卸载」。同理，`Draw` 不被调用期间后台数据要照常维护。

---

## 3. 工程配置

### 3.1 csproj

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <!-- 必须与主程序完全一致，否则加载不了 -->
    <TargetFramework>net10.0-windows10.0.19041.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <OutputType>Library</OutputType>
    <AssemblyName>HelloPlugin</AssemblyName>
    <RootNamespace>HelloPlugin</RootNamespace>

    <Version>1.0.0</Version>
    <!-- 没写 Author 时，宿主会退回读这个字段 -->
    <Company>你的名字</Company>
  </PropertyGroup>

  <ItemGroup>
    <!-- 引用主程序工程，拿到 INotchPlugin / IPluginHost / IWidget / WidgetKit -->
    <ProjectReference Include="..\NotchPeninsula.csproj" />
  </ItemGroup>

  <!-- 可选：编译完自动复制到主程序的 plugins 目录，省得手动搬 -->
  <Target Name="InstallToHostPlugins" AfterTargets="Build">
    <PropertyGroup>
      <HostPluginDir>$(MSBuildThisFileDirectory)..\bin\$(Configuration)\$(TargetFramework)\plugins</HostPluginDir>
    </PropertyGroup>
    <MakeDir Directories="$(HostPluginDir)" />
    <Copy SourceFiles="$(TargetDir)$(AssemblyName).dll"
          DestinationFolder="$(HostPluginDir)"
          SkipUnchangedFiles="true" />
  </Target>

</Project>
```

要点：

1. `TargetFramework` **必须逐字一致**（当前是 `net10.0-windows10.0.19041.0`）。写错就是 `BadImage` 或 `HostMismatch`。
2. `OutputType` 必须是 `Library`。
3. 需要 `System.Windows.Forms` 的东西（例如 `SystemInformation.DragSize`）请在自己的工程里加 `<UseWindowsForms>true</UseWindowsForms>` —— 宿主用了 WinForms **不会**传递给你。
4. 需要多文件资源就加 `<EmbeddedResource>`，别指望数据文件跟着插件目录走（[2.2](#22-影子副本哪些文件会被复制)）。

### 3.2 引用方式

- **开发期推荐**：`ProjectReference` 指向主程序 `NotchPeninsula.csproj`（上面就是）。
- **不想拉整个源码**：引用主程序输出目录里的 `NotchPeninsula.dll` 也可，但要把 `<Private>false</Private>` 设上，**别让它出现在你的输出目录**（插件目录里出现宿主 dll 会让人误以为要一起发布）。
- 无论哪种，插件产物里**只带自己的 dll 和第三方依赖**，不带 `NotchPeninsula.dll` / `SkiaSharp.dll`。

### 3.3 编译与部署

```
dotnet build HelloPlugin.csproj -c Release
```

然后按 [2.1](#21-目录布局与入口-dll-的挑选) 放进 `plugins\`。带依赖就把整个输出目录（去掉宿主 dll）作为一个子目录放进去，配上 `plugin.json`。

### 3.4 命名与元数据

- `Id`：反域名风格、全局唯一、**发布后不要改**（市场匹配、设置隔离、禁用清单都靠它）。
- `DisplayName`：用户看到的名字。
- `Version`：语义化版本；市场「更新」按它比较。
- `Author`：列表里显示；不写会退回程序集 `Company`。
- 组件 `Id`：**全局唯一**（宿主按 `Id` 建立「组件 → 插件」映射），建议 `<插件Id>.widget` 这种前缀风格。

---

## 4. 概念模型

| 名词 | 接口 | 一句话 |
|---|---|---|
| 入口类 | `INotchPlugin` | 每个插件唯一的入口，宿主实例化后调 `Initialize` |
| 宿主 | `IPluginHost` | 插件向主程序要服务的唯一通道 |
| 组件 | `IWidget` | 岛上一段内容：量宽、绘制、点击 |
| 详情页 | `IDetailPage` | 右键组件后整块展开的大面板 |
| 渲染帧 | `WidgetFrame` | 每帧递给 `Draw` 的上下文（主题色、透明度、频谱…） |
| 主题 | `RenderTheme` | 颜色 / DPI / 圆角 |
| 命中 | `WidgetHit` | `HitTest` 的返回值，携带动作名 |
| 提醒 | `ReminderData` | 弹一条 Toast 的描述对象 |

数据流：

```
加载 dll
  → 找到 INotchPlugin，实例化
  → Initialize(host)                     插件在这里注册组件 / 定时器
  → RegisterWidget(w)                    宿主立刻回调 w.OnActivate(host)
  → 每帧：w.Layout(...) 或 w.MeasureWidth(...) → w.Draw(canvas, rect, frame)
  → 鼠标：w.HitTest(x, y, rect) → w.OnLeftClick(action, x, y)
  → 右键：有 DetailPage 就展开详情页，否则打开设置窗口
  → 卸载：插件 Dispose() → 各组件 OnDeactivate()
```

---

## 5. 第一个完整插件

一个圆点 + 一段文字，点击切换开关、记住状态、每 30 秒后台刷新一次。

```csharp
using System;
using NotchPeninsula.Plugins;
using SkiaSharp;

namespace HelloPlugin;

public sealed class HelloPlugin : INotchPlugin, IDisposable
{
    public string Id => "com.example.hello";
    public string DisplayName => "Hello 插件";
    public string Version => "1.0.0";
    public string Author => "你的名字";

    private IDisposable? _timer;
    private int _seconds;                  // 后台线程写、渲染线程读

    internal int Seconds => Volatile.Read(ref _seconds);

    public void Initialize(IPluginHost host)
    {
        // 1) 注册组件：宿主会同步回调组件的 OnActivate(host)
        host.RegisterWidget(new HelloWidget(this));

        // 2) 每 30 秒在后台刷一次（最小间隔 100ms）
        //    文案里的秒数会变 → 宽度也会变 → 刷完顺手让宿主重测一次。
        //    InvalidateWidgetLayout 只改一个版本号（加锁），后台线程直接调是安全的。
        _timer = host.ScheduleRefresh(TimeSpan.FromSeconds(30), () =>
        {
            Interlocked.Add(ref _seconds, 30);
            host.InvalidateWidgetLayout();
        });

        // 3) 证明自己活着
        host.PostReminder(new ReminderData
        {
            Title = "Hello 插件已加载",
            Body = "灵动岛上多了一个圆点，点它可以切换",
        });
    }

    public void Dispose() => _timer?.Dispose();   // 卸载 / 重载 / 退出时宿主会调
}

internal sealed class HelloWidget : IAutoWidthWidget
{
    private readonly HelloPlugin _plugin;
    private IPluginHost? _host;
    private volatile bool _on = true;

    public HelloWidget(HelloPlugin plugin) => _plugin = plugin;

    public string Id => "com.example.hello.widget";
    public string DisplayName => "Hello";
    public IDetailPage? DetailPage => null;

    // 文字会在「开 / 关」之间变长变短 → 告诉宿主宽度会变
    public bool WidthVaries => true;

    public void OnActivate(IPluginHost host)
    {
        _host = host;
        _on = host.GetSetting("on", "1") != "0";
    }

    public void OnDeactivate() { }

    // 演示用：每帧现拼字符串（正式插件建议在数据变化时算好缓起来，避免每帧分配）
    private string Label => _on ? $"Hello {_plugin.Seconds}s" : "Hello 已关闭";

    // 只描述「由哪几块组成」。不写余量、不夹上限 —— 宽度由宿主量。
    public Box? Layout(float availableHeight) => WidgetKit.Row(
        WidgetKit.Icon(11f),        // 圆点占位
        WidgetKit.Gap(5f),
        WidgetKit.Text(Label, 12.5f, TextWeight.SemiBold));

    public void Draw(SKCanvas canvas, SKRect rect, WidgetFrame frame)
    {
        if (frame.Alpha == 0) return;

        // 槽位里有宿主加的 1px 留白，按内容区排版
        var content = WidgetKit.ContentRect(rect);
        float cy = content.MidY + frame.TextOffsetY;

        // ⚠️ 画笔临时创建、画完释放；不要存字段跨线程复用
        using var dot = new SKPaint
        {
            IsAntialias = true,
            Color = (_on ? new SKColor(76, 175, 80) : new SKColor(120, 120, 120)).WithAlpha(frame.Alpha),
        };
        canvas.DrawCircle(content.Left + 5.5f, cy, 5f, dot);

        using var text = new SKPaint
        {
            IsAntialias = true,
            TextSize = 12.5f,
            Typeface = WidgetKit.Typeface(TextWeight.SemiBold),   // 与 Layout 用同一字体面
            Color = frame.Theme.TextColor.WithAlpha(frame.Alpha),
        };
        canvas.DrawText(Label, content.Left + 16f, cy + 4.5f, text);
    }

    // ⚠️ x / y 是「相对本组件矩形左上角」的坐标，不是岛内绝对坐标
    public WidgetHit HitTest(float x, float y, SKRect rect)
        => x >= 0f && x <= rect.Width && y >= 0f && y <= rect.Height
            ? new WidgetHit("toggle")
            : WidgetHit.None;

    public void OnLeftClick(string? action, float x, float y)
    {
        if (action != "toggle") return;
        _on = !_on;
        _host?.SetSetting("on", _on ? "1" : "0");
        _host?.InvalidateWidgetLayout();   // 文案变了 → 让宿主重测宽度
    }

    public void OnRightClick() { }
}
```

这段代码里已经包含了新手最容易踩的四件事：`OnActivate` 读回设置、画笔用完即释放、命中坐标是相对的、文案变化要通知重测宽度。下面逐条展开。

---

## 6. 组件 IWidget

### 6.1 必须实现什么

| 成员 | 默认 | 说明 |
|---|---|---|
| `string Id` | — | 全局唯一，宿主按它做映射 |
| `string DisplayName` | — | 设置里的显示名 |
| `IDetailPage? DetailPage` | — | 没有就返回 `null`（右键将打开设置窗口） |
| `void Draw(SKCanvas, SKRect, WidgetFrame)` | — | 每帧调用 |
| `WidgetHit HitTest(float x, float y, SKRect)` | — | 命中检测 |
| `void OnLeftClick(string? action, float x, float y)` | — | 左键点击 |
| `void OnRightClick()` | — | 右键点击（无坐标） |
| `void OnActivate(IPluginHost)` | — | 注册进宿主时**同步**调用 |
| `void OnDeactivate()` | — | 卸载摘登记时调用 |
| `float MeasureWidth(float availableHeight)` | `0` | 老协议的宽度上报，见 [6.3](#63-宽度协议measurewidth老协议) |
| `bool AcceptsFileDropWhenCollapsed` | `false` | 收起态接收文件，见 [6.8](#68-收起态接文件) |
| `bool AcceptsRightClick` | `false` | 右键单击透传，见 [6.7](#67-点击右键双击) |
| `bool AcceptsDoubleClick` | `false` | 左右键双击透传 |
| `void OnLeftDoubleClick / OnRightDoubleClick(float, float)` | 空 | 双击回调 |

> 接口里有默认实现的成员，都是**后加的**。它们必须带默认实现，否则老插件会因为 `TypeLoadException` 直接加载失败 —— 你自己给接口加成员时也要守这条规矩。

### 6.2 宽度协议：AutoWidth（推荐）

新插件直接实现 `IAutoWidthWidget`，**不要再写 `MeasureWidth`**。你只描述「我由哪几块组成」，宽度由宿主量、由宿主加留白。

```csharp
internal sealed class MyWidget : IAutoWidthWidget
{
    public bool WidthVaries => true;    // 宽度会随内容变化时才置 true

    public Box? Layout(float availableHeight) => WidgetKit.Row(
        WidgetKit.Icon(13f),                                   // 固定宽块
        WidgetKit.Gap(5f),                                     // 纯间距
        WidgetKit.Text("23°C", 12.5f, TextWeight.SemiBold));   // 按宿主当前字体重测

    // 绘制时用同一份字体，量画才一致
    public void Draw(SKCanvas canvas, SKRect rect, WidgetFrame frame)
    {
        using var paint = new SKPaint
        {
            IsAntialias = true, TextSize = 12.5f,
            Typeface = WidgetKit.Typeface(TextWeight.SemiBold),
            Color = frame.Theme.TextColor.WithAlpha(frame.Alpha),
        };
        var content = WidgetKit.ContentRect(rect);
        canvas.DrawText("23°C", content.Left + 18f, content.MidY + frame.TextOffsetY + 4.5f, paint);
    }

    // 其余成员同 IWidget
}
```

盒子只有六种，**不要自己派生 `Box`**（未知类型按 0 宽处理 = 这帧不显示）：

| 工厂 | 宽度 |
|---|---|
| `WidgetKit.Text(text, size, weight?)` | 按宿主当前字重实测 |
| `WidgetKit.Icon(width)` | 固定宽度块（图标、色块、进度点） |
| `WidgetKit.Gap(width)` | 纯间距 |
| `WidgetKit.Row(params Box[])` / `Row(gap, params Box[])` | 横向相加（默认块间距 0） |
| `WidgetKit.Stack(params Box[])` | 纵向叠放，取最宽的一行 |
| `WidgetKit.Min(minWidth, inner)` | 内容宽度下限（**不含**宿主留白） |

口径（全部由宿主独占，插件侧不该再出现 `+2f` / `PadX*2` 这类补偿）：

- 槽位宽 = `ceil(内容宽) + 2 × WidgetKit.AutoPadX`（`AutoPadX = 1f`）。
- 组件间距 = `WidgetKit.SlotGap`（16px），别再硬编码。
- 内容区 = `WidgetKit.ContentRect(slot)`（去掉那 1px 留白）。
- `WidthVaries = true` 时：宽度量化到 **1px**（吸收字宽浮点抖动）；**变宽立即生效**（等一帧文字就会露裁切）；**变窄要连续 600ms 确认**（避免长句→短句→长句来回抽）；变化幅度 ≤6px 视为抖动，不动。宽度固定的组件保持默认 `false`，一分钱额外开销都没有。
- `Layout` 返回 `null` / 空盒子 = 0 宽 = 本帧不显示。**不要在里面做 IO、加锁或耗时计算** —— 宿主只在注册表版本变化或你主动通知重测时调用它。
- `Layout` 抛异常时，宿主先退回该组件的 `MeasureWidth`（如果还实现着），再不行才熔断。一次布局 bug 不该让组件永久消失。

内容变了要主动通知：

```csharp
_host.InvalidateWidgetLayout();   // 宿主重新 Layout → 弹簧动画过渡到新宽度
```

### 6.3 宽度协议：MeasureWidth（老协议）

已经发布的插件、以及自绘复杂度高不想用盒子的插件继续用这个，行为完全不变。

```csharp
public float MeasureWidth(float availableHeight) => 文本宽 + 图标宽 + 间距;
```

语义只有一条：**完整显示我的内容需要多宽**（逻辑像素）。

- 不是「我希望多宽」，更不是「上限定多宽」。
- 宿主拿它和本帧剩余空间比：**装得下就按这个宽给、内容完整显示；装不下这一帧整个组件都不显示**（宿主不压缩、不截断、不加省略号）。
- 所以：别为了「挤进去」少报（结果是被压缩显示），也别用 `1920` 这类岛体上限去夹自己（会让宿主误判成刚好装得下）。真要保险丝，就往大了定。

**返回值会被缓存**：宿主只在「组件注册表版本变化」时调一次 `MeasureWidth`（插件注册 / 注销 / 顺序变化），之后每帧复用缓存值 —— 稳态 60FPS 零测量开销。所以：

- 宽度随内容变 → 内容变了必须调 `host.InvalidateWidgetLayout()`，否则宽度永远停在首次测量值；
- 宿主切换字体后会自动重测（内部按字体名 + 自定义路径做 key），**你不需要**再自己订阅 `FontConfig.Changed`。

### 6.4 预算与取舍

插件行不是无限宽。规则一句话：**每个组件要么完整显示，要么完全不显示。**

- 行预算 = `min(1920, 目标显示器工作区宽度)` − 原生内容本帧占用（`Renderer.IslandWidthCap` − 原生宽）。
- 按**显示顺序**（设置 → 显示内容 里那张列表，未调整过时就是注册顺序）贪心放行：所需宽度能完整放进剩余空间才显示；装不下的本帧整体隐藏（不绘制、不留位、不响应点击），后面的组件仍可继续尝试。
- 每个组件占 `宽 + 16px` 间距。

插件可以提前问预算，主动挑一条塞得进的内容：

```csharp
float budget = host.GetPluginRowBudget();    // 「不显示本插件时，我这个位置还剩多宽」
float slot   = host.GetSlotBudget(widgetId); // AutoWidth 专用：再扣掉我自己的 16px 槽间距

// 0          = 本位置没空间了
// +∞         = 宿主还没跑过首帧
// 其他        = 逻辑像素
```

- `GetPluginRowBudget()` 是按**插件**算的（扣掉原生 + 排在它前面的插件，含那些间距）。
- `GetSlotBudget(widgetId)` 是按**单个组件**算的（再扣掉它自己的槽间距），AutoWidth 插件在 `Layout` 前挑内容用它最准。
- 两个都随帧刷新，渲染线程和后台线程都能调。

### 6.5 坐标系

这是最容易写错的一处，务必分清：

| 位置 | `rect` | `x` / `y` |
|---|---|---|
| `Draw` | 你在岛上的矩形（**岛内绝对坐标**）：`Left`/`Right` 是横位置，`Top = 0`、`Bottom = 岛高` | — |
| `HitTest` | 同上（绝对） | **相对矩形左上角**（`0 ~ rect.Width`、`0 ~ rect.Height`） |
| `OnLeftClick` / `OnLeftDoubleClick` | — | **相对矩形左上角** |
| 详情页的 `HitTest` / `OnAction` / `OnMouse*` | 详情页矩形（绝对） | **相对矩形左上角** |

所以命中判断永远写成 `x >= 0 && x <= rect.Width`，**不要**写 `x >= rect.Left` —— `rect.Left` 不是 0。

垂直方向：`rect.Top` 是 0、`rect.Bottom` 是整条岛的高度，画面中线是 `rect.MidY`；再叠加 `frame.TextOffsetY` 做文字基线对齐（宿主自己的文字也是这么摆的）。

### 6.6 Draw 的三条规矩

1. **画笔（`SKPaint`）在 `Draw` 里临时 `new`、画完 `using` 释放**。不要缓存成字段跨线程复用 —— 卸载 / 重载时宿主会释放相关原生对象，你下次再用就是 use-after-free（`0xC0000005`）。
2. **透明度一律乘上 `frame.Alpha`**（`颜色.WithAlpha(frame.Alpha)`），否则启动淡入、Toast 叠化时你的内容会「不跟着淡」，看起来像贴上去的。`frame.Alpha == 0` 直接 `return`。
3. **异常会熔断**：`Draw` / `MeasureWidth` / `Layout` 抛异常，这个组件会被标记为 broken、从此不再绘制（日志里有记录）。别在这里做可能长时间阻塞的事。

### 6.7 点击、右键、双击

**左键**：`HitTest` 返回 `new WidgetHit("动件名")` → 宿主调 `OnLeftClick(action, x, y)`。动作名由你定义（`"toggle"`、`"next"`、`"open"`…），`WidgetHit.None` 表示没点中。

**右键**默认行为是「展开详情页 → 没有详情页就打开设置窗口」。两档可选的透传，互相独立，默认都关：

| 声明 | 行为 | 代价 |
|---|---|---|
| `AcceptsRightClick => true` | 命中本组件的右键**不再**展开详情页 / 打开设置，只回调 `OnRightClick()` 并消费 | 放弃了「右键展开详情页」这个默认入口 |
| `AcceptsDoubleClick => true` | 左右键双击回调 `OnLeftDoubleClick(x, y)` / `OnRightDoubleClick(x, y)` | 只开这一档时，右键单击会先等一个系统双击判定窗口（`GetDoubleClickTime()`，默认约 500ms）才执行默认行为 |

规则与代价：

- 宿主**不做**「延迟单击等双击」的排队。所以一次左键双击 = 先一次普通单击（`HitTest` + `OnLeftClick`）+ 再补一次双击回调。**双击动作必须幂等**。
- 右键两档都开时不等待：单击当场回调，双击随后再补一次。只开双击时，第一下先挂起、窗口内来了第二下才算双击（此时不执行默认行为），窗口过了才执行原行为。
- 组件这层的右键回调签名是 `OnRightClick()`（**不带坐标**）；详情页那层才带 `(x, y)`。

右键的分层消费顺序（知道就行，不用写代码）：收起详情页 → 广播给命中的组件 → 展开媒体面板 → 打开设置窗口。**原生媒体区域平时不消费右键**，所以插件组件只要不画到那上面，右键就归你。

### 6.8 收起态接文件

```csharp
public bool AcceptsFileDropWhenCollapsed => true;
```

置 `true`（并且提供了 `DetailPage`）后，用户把文件拖到**还没展开**的组件图标上时，宿主会自己把详情页展开、光标变成「可放入」，接着走详情页那套拖入回调，松手即落下。适合「组件本身就是文件入口」的插件（文件中转站就是这么做的）。

边界：展开**不等于**接受（落点仍要在详情页矩形内）；已有别的详情页开着时不会抢。

### 6.9 生命周期与熔断

- `OnActivate(IPluginHost)`：`host.RegisterWidget(w)` **内部同步调用**。这是读回持久化设置、缓存宿主引用的时机 —— 注意它可能在你 `Initialize` 还没返回时就跑到了。
- `OnDeactivate()`：卸载摘登记时调用。宿主保证**先**切断渲染侧对组件的引用（`InvalidatePluginSnapshot`），**再**回调它，所以在这里 `Dispose` 自己持有的原生资源是安全的。
- 卸载顺序：`插件.Dispose()`（如果实现了 `IDisposable`）→ 组件的 `OnDeactivate()` → 反注册 → 卸载程序集。
- 两边都可能跑到，释放逻辑请写成幂等的（`try { x?.Dispose(); } catch { }`）。

---

## 7. 详情页 IDetailPage

### 7.1 最小实现

组件上挂一个对象即可：

```csharp
internal sealed class MyDetailPage : IDetailPage
{
    public float MeasureWidth() => 320f;
    public float MeasureHeight() => 140f;

    public void Draw(SKCanvas canvas, SKRect rect, WidgetFrame frame)
    {
        using var paint = new SKPaint
        {
            IsAntialias = true, TextSize = 13f,
            Typeface = WidgetKit.Typeface(),
            Color = frame.Theme.TextColor.WithAlpha(frame.Alpha),
        };
        canvas.DrawText("详情页内容", rect.Left + 16f, rect.Top + 28f, paint);
    }

    public WidgetHit HitTest(float x, float y, SKRect rect) => WidgetHit.None;
    public void OnAction(string? action, float x, float y) { }
}
```

组件里：`public IDetailPage? DetailPage { get; } = new MyDetailPage();`（建一次就缓存，别每次访问都 `new`）。

### 7.2 尺寸与裁剪

- `MeasureWidth()` / `MeasureHeight()` 返回**你想要的尺寸**（逻辑像素），宿主只做保护性裁剪：
  - 宽 `180 ~ 1000`
  - 高 `48 ~ 480`
- 返回 `NaN` / `<= 0` 会按最小尺寸兜底并记日志。
- **同一次展开期间尺寸是固定的**：内容变了想让岛变大，得等下次展开。所以要么保证内容落在已报尺寸内，要么自己按当前尺寸重新排版（见下一条）。
- 展开 / 收起带弹簧动画，不用你自己做。

### 7.3 坐标系与展开动画

`Draw` 拿到的 `rect` 是**岛内绝对坐标**，但**动画进行中的每一帧它是插值尺寸**（目标 380×108，前几帧可能只有 130×34）。

⚠️ **不要拿 `rect` 当排版基准**。按它换行会让文字每帧重排、每帧跳位（肉眼就是「展开不丝滑」），任何以 `rect` 尺寸为 key 的缓存都会每帧失效。

正确做法：**排版永远按你 `MeasureWidth` / `MeasureHeight` 的目标尺寸算**，绘制时把整块内容对齐 / 缩放到当前 `rect`：

```csharp
float layoutW = 320f, layoutH = 140f;             // 与 MeasureWidth/Height 一致
float scale = Math.Min(Math.Min(rect.Width / layoutW, rect.Height / layoutH), 1f);  // 封顶 1，免得过冲被裁

canvas.Save();
canvas.Translate(rect.MidX, rect.MidY);
canvas.Scale(scale);
canvas.Translate(-layoutW * 0.5f, -layoutH * 0.5f);
// 之后都按 (0, 0, layoutW, layoutH) 这个固定坐标系画
canvas.Restore();
```

动画期间内容随容器一起放大，展开完成时 `scale = 1`，与设计尺寸 1:1。

### 7.4 鼠标事件

除了 `HitTest` / `OnAction`（老那套，一次点击只有一个回调），详情页还有完整的鼠标事件：

```csharp
void OnMouseDown(float x, float y);
void OnMouseMove(float x, float y);
void OnMouseUp(float x, float y);
void OnMouseLeave();
```

坐标同样是**相对详情页矩形左上角**。两套并存：让 `HitTest` 返回 `WidgetHit.None` 就自动从老那套退出，只走鼠标事件。

⚠️ **按住之后把鼠标拖出岛外再松手，`OnMouseUp` 不会来**（只有 `OnMouseLeave`），所以「按住」状态必须靠 `OnMouseLeave` 兜底复位。

### 7.5 拖入 / 拖出

拖入（四个成员都带默认实现，默认拒绝）：

```csharp
bool OnFilesDragEnter(int count);      // 返回 true 才接受这次拖放
void OnFilesDragOver(float x, float y);
void OnFilesDragLeave();
void OnFilesDrop(string[] paths);
```

拖出用宿主代发（详情页没有自己的窗口）：

```csharp
bool ok = host.StartFileDrag(paths, allowMove: false);   // 阻塞到用户松手
```

标准写法是**在 `OnMouseMove` 里判断「左键仍按下 + 位移超过阈值」再发起**，别放在 `OnMouseDown`（否则每次普通单击都会进一次 OLE 拖放循环，观感是「点一下卡一下」）。

约定：

- 落点必须落在详情页矩形内，拖到岛体别处算「不接受」。
- `StartFileDrag` 会**静默过滤**不存在的路径；全无效时直接返回 `false`，不进入拖放循环。
- `allowMove: true` 才允许「移动」效果（拖到同盘目录会真的移动文件），慎用。
- 只认文件系统上真实存在的路径：从网页拖文字 / 从画图工具拖位图一律拿不到。

**要用户离开面板去取文件**（拖入文件就是这样）的详情页，必须调长或关掉自动收起，否则鼠标刚移开面板就没了、拖放目标当场消失 —— 见下一条。

### 7.6 自动收起

```csharp
public TimeSpan AutoCollapseDelay => TimeSpan.Zero;
```

| 取值 | 行为 |
|---|---|
| `TimeSpan.Zero`（默认） | 用宿主内置的 **900ms** 延迟：鼠标离开岛 → 等 900ms → 收起；期间回到岛上就取消 |
| 正数 | 自定义时长，宿主夹到 `0.5s ~ 60s` |
| 负数（惯例 `Timeout.InfiniteTimeSpan`） | **全局屏蔽自动收起**：鼠标离开不收，**点到岛外也不收** |

「不收起」时关闭入口只剩两个：**岛内右键**（没把右键透传给插件时）和插件自己调 `CloseDetailPage()`。所以选这一档的详情页**务必留一个看得见的关闭出口**。

> 为什么默认有 900ms：详情页尺寸由插件决定，展开动画结束后鼠标可能刚好落在新面板之外。

### 7.7 右键 / 双击透传

详情页上同样是两档独立开关，语义与组件一致，只是回调带坐标：

| 声明 | 行为 |
|---|---|
| `AcceptsRightClick => true` + `OnRightClick(x, y)` | 岛内右键**不再收起面板**，当场回调；双击随后再补一次 |
| `AcceptsDoubleClick => true` + `OnLeftDoubleClick/OnRightDoubleClick(x, y)` | 左右键双击回调；只开这档时右键单击会晚一个双击窗口才收起 |

⚠️ 开了 `AcceptsRightClick` 等于把「右键关面板」这个手势整个让给插件 —— 请自己留关闭出口（面板里的 ×，或调 `CloseDetailPage()`）。若 `AutoCollapseDelay` 又是负数，那就只剩这一个出口了。

⚠️ 左键双击的第一下**仍会照常走一次单击**（`HitTest`/`OnAction` 与 `OnMouseDown`/`OnMouseUp` 都会来），动作要幂等。

### 7.8 主动开合

```csharp
host.OpenDetailPage("组件Id");                 // 不带结果，目标不存在/没有详情页时静默无操作
bool ok = host.TryOpenDetailPage("组件Id");    // 带结果：展开成功或本来开着 → true
host.CloseDetailPage();
bool now = host.ToggleDetailPage("组件Id");    // 切换语义：已展开则收起
```

典型用法：数据加载完了自动弹出来（`TryOpenDetailPage`），或在面板里放一个 × 按钮（`CloseDetailPage`）。

### 7.9 熔断与尺寸固定

- `MeasureWidth` / `MeasureHeight` / `Draw` 抛异常 → 该详情页被熔断并自动收起，主程序照常运行（日志有原因）。
- 一次展开期间尺寸固定；想变尺寸只能等下次展开。
- 详情页展开时：岛内左键只走详情页；来了 Toast 会先显示通知，通知结束后详情页自动回来。

---

## 8. 常用能力

### 8.1 定时刷新

```csharp
IDisposable timer = host.ScheduleRefresh(TimeSpan.FromSeconds(5), () => { /* 后台线程 */ });
timer.Dispose();   // 停止
```

- 间隔最小 **100ms**（更小的会被夹到 100ms）。
- 回调在**后台线程**（`System.Timers.Timer`）。
- **上一次回调还没跑完时，这一次直接跳过**（不排队、不叠加）—— 所以回调里可以放心做几秒的活。
- 回调抛异常会被宿主吞掉并记日志，定时器继续跑。
- 回调里更新数据、`Draw` / `Layout` 里读：用 `volatile` / `Interlocked` / `lock` 保证可见性与一致性。

### 8.2 提醒

```csharp
host.PostReminder(new ReminderData
{
    Title = "下载完成",
    Body = "3 个文件已就绪",
    Duration = TimeSpan.FromSeconds(6),   // 可选
    IconPath = "qq",                      // 可选
    AvatarPath = "https://example.com/a.png",  // 可选，优先级高于 IconPath
    OnClick = () => host.TryOpenDetailPage("com.example.hello.widget"),  // 可选
});
```

| 字段 | 说明 |
|---|---|
| `Title` / `Body` | 标题 / 正文 |
| `Source` | 左上角来源标签，不填就是「插件提醒」 |
| `IconPath` | 图标，四种写法：本地路径 / `https://` 图片 / `data:image/png;base64,...` / 内置别名（`"qq"`、`"windows"`…） |
| `AvatarPath` | 头像，写法同 `IconPath`，**优先级高于 `IconPath`**（头像 > 图标 > 兜底） |
| `Duration` | 停留时长：`<= 0` → 4 秒；`< 1s` → 1 秒；`> 60s` → 60 秒 |
| `OnClick` | 用户点中这条提示时的回调；宿主先收起通知再回调，回调异常自吞 |

- 图标 / 头像是**异步解析**的：先用兜底图标弹出来，解析完自动换图；任何一步失败都静默回退到下一级。
- 想加新的内置别名：把 `wechat-icon.png` 这类文件丢进程序目录的 `data/image/`（`<别名>-icon.*` 或 `<别名>-logo.*`）。

### 8.3 设置持久化

```csharp
string v = host.GetSetting("on", "1");   // key, 默认值
host.SetSetting("on", "0");              // 只存字符串
```

- 存在 `HKCU\SOFTWARE\NotchPeninsula`，key 自动加 `Plugin.<你的 Id>.` 前缀，插件之间不会互相覆盖。
- 存的是字符串：数字用 `int.TryParse`、布尔用 `"1"/"0"`，自己定规矩。
- `host.SettingsChanged`：**`SetSetting` 写入时会同步触发你自己注册的处理器**（在调用 `SetSetting` 的那条线程上），适合「改完立刻重算」。
- 读回的最佳时机是组件的 `OnActivate`。

> 宿主**不提供**「在设置窗口里给插件一块配置区」的能力（`RegisterSettingsPage` 未接线，注册了也不会显示）。要配置项就自己做 UI：做进详情页最省事，或用 `CreateWindow` 开独立窗口。

### 8.4 自定义窗口

```csharp
var win = host.CreateWindow("我的面板", 420, 260);   // 自动居中、有关闭按钮、Esc 可关

win.SetDraw((canvas, w, h) =>
{
    canvas.Clear(new SKColor(30, 30, 30));
    // 按 (0, 0, w, h) 画内容
});
win.SetMouse(down: (x, y) => { }, move: (x, y) => { }, up: (x, y) => { });
win.SetKey(ch => { });

win.RequestRedraw();   // 请求重绘（宿主在下一拍调你的 SetDraw）
// ...用户关掉窗口或你主动收工时
win.Close();
```

- `SetDraw` 的宽高与坐标系都是**逻辑像素**（宿主内部已按系统 DPI 缩放画布），和岛体口径一致。
- 鼠标 / 键盘回调也在同一套逻辑坐标里。
- 窗口归宿主管理：自动居中、右上角关闭按钮、Esc 关闭；插件 `Close()` 之外不要自己销毁句柄。

### 8.5 窗口的拖放

拖入分「过程」和「松手」两条独立的线，只订阅其中一条也行：

```csharp
win.SetDragHover(
    onEnter: count => { /* 拖入项进入窗口：显示「将导入 N 项」 */ },
    onOver:  (x, y) => { /* 移动中：记下坐标、画高亮 */ },
    onLeave: ()     => { /* 离开 / 取消 / 放下：复位高亮 */ });

win.SetFilesDrop(paths => { /* 松手：完整路径数组 */ });
```

四条硬约定：

1. **`onLeave` 一定会来，高亮必须挂在它上面复位**。只挂在 `onOver` 上，用户拖出去再松手，高亮就永远留在界面上。
2. **只接受文件拖入**：拖动内容里没有文件系统路径时（网页文字、画图位图），三个悬停回调都不触发、光标显示禁止、`SetFilesDrop` 也不响。
3. **拖出会阻塞**：`StartDragFiles` 走 OLE 的 `DoDragDrop`，阻塞到用户松手或按 Esc，而且**期间 `SetMouse` 的 up 回调不会触发**。所以状态要在 `StartDragFiles` 返回后自己复位（`finally`）。也**别在 `SetDraw` 里调它**。
4. **拖出要在 move 回调里按阈值发起**（不是 down）。

```csharp
private readonly List<string> _files = new();
private bool _pressed, _dragging;
private float _pressX, _pressY;

win.SetMouse(
    down: (x, y) => { _pressed = true; _pressX = x; _pressY = y; },
    move: (x, y) =>
    {
        if (!_pressed || _dragging || _files.Count == 0) return;
        if (Math.Abs(x - _pressX) < 4f && Math.Abs(y - _pressY) < 4f) return;

        _pressed = false;
        _dragging = true;
        try { win.StartDragFiles(_files); }   // 阻塞到松手 / Esc
        finally { _dragging = false; }        // ⚠️ up 回调不会来，必须在这里复位
    },
    up: (x, y) => _pressed = false);
```

`StartDragFiles(paths, allowMove: false)` 同样会静默过滤不存在的路径，全无效直接返回 `false`。

> 拖放期间宿主会**推迟关闭请求**（插件被卸载 / 热重载），不会让窗口在 OLE 里被销毁 —— 但你自己也别在拖放中调 `Close()`。

### 8.6 卸载钩子

```csharp
// 插件自己建的数据目录：卸载时宿主会多问一句「要不要一起删」
host.RegisterUninstallDirectory(@"C:\Users\me\AppData\Local\HelloPlugin");

// 宿主看不见的东西（注册表项、外部进程、临时文件）：只在真正卸载时执行一次
host.RegisterUninstallAction(() => { /* 清理 */ });
```

- 目录：**最多登记一个**，重复调用只认第一次。宿主**绝不擅自删除** —— 卸载时弹窗把路径摆给用户看，点「删除」才递归删，点「保留」就只卸载。路径要是绝对路径、且是插件独占的子目录（`%USERPROFILE%`、桌面、系统目录、盘符根、宿主程序目录会被拒绝）。
- 动作：只在**真正卸载**时执行，禁用 / 重载 / 退出都不执行，也不弹窗；执行时机是「实例还活着、文件还没删」；抛异常只记日志、不会挡住卸载；重复注册以最后一次为准；在 UI 线程同步执行，别长时间阻塞。

---

## 9. 把插件暴露成 MSP 工具

插件可以把自己的能力挂到 MSP 网络上：本机任何 MSP 程序都能调用你注册的工具、订阅你发出的信号。
你不用懂协议，也不用碰 `MspNode` —— 全部经 `IPluginHost`，**命名、校验、撤销都由宿主完成**。

> **前提**：`设置 → 通用设置 → MSP 接入（实验性）` 必须开着（默认关闭）。
> 关着的时候注册不会报错，只是不会出现在对端清单里；打开后自动补上。

### 9.1 最短路径

```csharp
public void Initialize(IPluginHost host) => host.RegisterMspToolsFrom(new MyTools(host));

internal sealed class MyTools(IPluginHost host)
{
    [MspTool("current", Description = "查询当前天气")]
    public object Current(string city, int days = 1) => new { ok = true, city, days };
}
```

一行注册完。对端看到的名字是 **`notchpeninsula_<插件id>_current`**。

| 你写的 | 对端看到 |
|---|---|
| 插件 `Id = "Weather"` + `[MspTool("current")]` | `notchpeninsula_weather_current` |
| 插件 `Id = "Weather"` + `[MspTool]`（不写名字） | `notchpeninsula_weather_<方法名>` |

### 9.2 名字规则

- 前缀 `notchpeninsula_` 由宿主加，**你不要自己写**（写了会变成 `notchpeninsula_weather_notchpeninsula_xxx`）。
- 整串按 MSP 的 `tool_name` 文法 `^[a-z0-9_-]+$` 规范化：转小写，`[^a-z0-9_-]` 换成 `_`。
- **工具名里不能有 `.`**（MSP 不允许），`Weather.Current` 会被规范成 `weather_current`。
- 同插件内重名 → 后者覆盖（热重载的正常路径）；**跨插件撞名 → 后到者被拒**，日志里写明是被谁占了。

### 9.3 schema 自动生成，别手写

`[MspTool]` 方法、或传给 `RegisterMspTool` 的委托，会**原样**交给 MSP SDK，`inputSchema` 从签名生成：

- 参数名进清单；
- **带默认值的参数自动成为可选**（上例 `days` 不传就是 1）；
- 要精确控制就传显式 `inputSchema` 覆盖。

> ⚠️ **不要为了「拦截一下」把委托再包一层**（比如 `args => inner.DynamicInvoke(...)`）。
> 包完之后 SDK 看到的是包装函数，**默认值和参数名都丢了** —— 可选参数会退化成必填。

### 9.4 另一个入口：不写 attribute

动态生成工具、或从配置里读一批工具时：

```csharp
_regs.Add(host.RegisterMspTool("current", "查询当前天气",
    (string city, int days = 1) => new { ok = true, city, days }));
```

它和 attribute 走**同一个注册器**（同前缀、同校验、同撞名规则），只是入口不同。

### 9.5 发信号

```csharp
// 声明：只给末段动作名，宿主补 plugin.<插件id>. 前缀
_regs.Add(host.DeclareMspSignal("tick", "每次有人调用我的工具时发出"));

// 发布：完整路径
host.PublishMspSignal("plugin.weather.tick", new { city = "北京", at = DateTimeOffset.Now });
```

- 声明出来的是 `plugin.weather.tick` —— 插件**给不了**前面的域，所以冒充不了别家插件，也碰不到宿主自己的 `notify.*` / `media.*`。
- 发布可以发到任意合法路径（这一版不做管控），但入口只有 `PublishMspSignal` 一处。
- **MSP 关着时发布是空操作**，只记 debug —— 插件不必自己判断开关状态。
- 路径文法 `^[a-z0-9_-]+(\.[a-z0-9_-]+)+$`，不合规会被拒并记日志。

### 9.6 生命周期与撤销

| 时机 | 行为 |
|---|---|
| 插件先于 MSP 加载 | 先记账；MSP 启动时统一登记，进**握手那份清单** |
| MSP 运行中新装插件 | 立刻登记并推 `manifest_update`，**对端不需要重连** |
| 插件卸载 / 禁用 | 从清单摘掉并推送，对端重取清单就看不到了 |
| 插件重载 | 同名重新登记，工具名与信号路径不变，对端无感 |

三个方法都返回 `IDisposable`；宿主在插件卸载时也会自己摘，显式 dispose 是双保险。

> ⚠️ 摘掉之后**旧 handler 仍可能被调用**（MSP 协议层没有「反注册工具」的 API）。
> 工具实现里最好自己判一下状态，或者在插件停用后返回一个明确的错误。这与 Python SDK 的行为一致。

### 9.7 四条纪律

协议线程是**同步**调用你的工具的，对端在等返回值。

1. **不要抛异常** —— 返回 `{ ok = false, error = "…" }`，让对端拿到结构化错误，而不是一个 RPC 失败。
2. **不要做耗时 IO** —— 你慢一秒，对端就多等一秒。
3. **返回值别太大** —— 几 KB 以内；几十 KB 的字符串对两端都不友好。
4. **统计要无锁** —— 工具会从协议线程并发进来，`Interlocked` / `Volatile` 足够，别用 `lock` 挡住渲染线程。

---

## 10. 字体、主题与渲染上下文

### 10.1 字体

宿主把「全岛用哪套字体」放在 `NotchPeninsula.FontConfig`（注意命名空间是 `NotchPeninsula`，不是 `NotchPeninsula.Plugins`）：

| 成员 | 说明 |
|---|---|
| `Normal` / `SemiBold` / `Bold` | 三档字重的 `SKTypeface`，就是岛体当前在用的字体 |
| `Fallback` | 缺字兜底（恒为系统字体），用户选了纯拉丁字体时中文会落到它上面 |
| `HasCustomFont` / `CustomFontPath` / `DisplayName` | 当前字体状态；`CustomFontPath` 可当排版缓存的失效键 |
| `event Action? Changed` | 用户切换 / 重置字体时触发 |

**两条铁律**：

1. **绝不缓存 `SKTypeface`**。宿主换字体时会 `Dispose` 旧字体面，你存进字段下一帧拿去画就是 use-after-dispose（`0xC0000005`）。**每次 `Draw` / `Layout` 现取一次**。
2. **绝不 `Dispose` 宿主的字体面**。它是进程级共享资源（全岛每一支文字画笔都在用）。只有你自己 `SKTypeface.FromFile(...)` 创建的才归你释放。

AutoWidth 插件直接用 `WidgetKit.Typeface(weight)` —— 它与 `Layout` 测量用的是同一份，天然「量画一致」。老协议插件的标准写法：

```csharp
internal static class PluginFont
{
    public static SKTypeface? Resolve()
    {
        try { return FontConfig.Normal; } catch { }
        try { return FontConfig.Fallback; } catch { return null; }
    }
}
```

用 `try/catch` 包住是为了「旧宿主没有这个类型」时只让字体优化失效，其余功能照常。

> 宿主换字体后会**自己重测插件宽度**（内部按字体名 + 自定义路径做 key），你不需要订阅 `FontConfig.Changed` 来触发重测。自己另存了「按字体算好的排版缓存」时，才需要用 `Changed` / `CustomFontPath` 作失效依据。

### 10.2 主题与 WidgetFrame

```csharp
public readonly record struct WidgetFrame(
    RenderTheme Theme,
    byte Alpha,
    float TextOffsetY,
    float[]? Bars,
    bool IsHovered);
```

| 字段 | 说明 |
|---|---|
| `Theme` | 本帧主题快照（见下） |
| `Alpha` | 合成透明度 `0~255`；启动淡入、Toast / 详情页叠化都反映在这里 |
| `TextOffsetY` | 文字垂直偏移（逻辑像素），加到文字基线上 |
| `IsHovered` | 鼠标是否悬停在本组件（或详情页）矩形内 |
| `Bars` | 音频频谱，长度 5（低音→高音，归一化到 `0~1`）；没有音频时全 0 |

```csharp
public readonly record struct RenderTheme(
    SKColor TextColor,
    SKColor SubTextColor,
    SKColor BackgroundColor,
    float GlobalDpi,
    float NotchBottomRadius);
```

- 颜色一律 `xxx.WithAlpha(frame.Alpha)`，才能跟着淡入 / 叠化。
- `GlobalDpi`：宿主把逻辑像素换成物理像素用的系数（设置里可改，默认 1.0）。你自己做坐标换算时必须用同一个，否则高 DPI 下会偏。
- `NotchBottomRadius`：岛体底部圆角（宿主夹在 0~28），贴边画内容时用。

### 10.3 频谱

`frame.Bars` 是宿主传进来的同一份数组（每帧数值变化，长度 5）。**可以当帧读，别存进字段当快照**，也别改它。

```csharp
if (frame.Bars is { Length: 5 } bars && bars[0] > 0.05f)
{
    // 画一根随低音跳动的条
}
```

### 10.4 RequestRedraw 是空操作

`host.RequestRedraw()` 目前**什么都不做** —— 宿主是常驻 60FPS 渲染，画布每帧都会重画。数据变了只要把字段更新掉（注意线程安全），下一帧自然就看到了；宽度变了再补一个 `InvalidateWidgetLayout()`。

---

## 11. 常见坑速查

| # | 症状 | 原因 | 正确做法 |
|---|---|---|---|
| 1 | 加载失败 / 列表里显示不兼容 | `TargetFramework` 与宿主不一致 | 逐字用 `net10.0-windows10.0.19041.0` |
| 2 | `NotAPlugin` | dll 里没有 `public` 的 `INotchPlugin` 实现；或自己复制了一份宿主接口 | 引用主程序工程，不要自带接口定义 |
| 3 | `MissingDependency` | 依赖放在子目录里（不会被影子复制） | 依赖 dll 直接放插件目录一层 |
| 4 | 偶发 `0xC0000005` | 缓存了 `SKPaint` / `SKTypeface` 跨线程复用 | `Draw` 里临时 `new`、`using` 释放；字体现取 |
| 5 | 文字被裁 / 岛宽不跟着变 | 宽度变了没通知 | 内容变化后 `InvalidateWidgetLayout()` |
| 6 | 文字被压缩、显示不全 | `MeasureWidth` 为了挤进去少报了 | 报真实需求宽度，装不下让宿主隐藏 |
| 7 | 点不中 / 点错位置 | 把 `HitTest` 的 `x/y` 当成绝对坐标 | 它们是相对矩形左上角的 `0..Width` |
| 8 | 插件内容不跟着淡入 | 颜色没乘 `frame.Alpha` | `Color.WithAlpha(frame.Alpha)` |
| 9 | 后台数据与画面不同步 / 读到半新半旧 | 后台线程写、渲染线程读没有同步 | `volatile` / `Interlocked` / `lock` |
| 10 | 详情页展开动画卡顿、文字每帧跳 | 用 `rect`（动画插值）当排版基准 | 按 `MeasureWidth/Height` 的目标尺寸排版，再整体缩放 |
| 11 | 鼠标移开面板就没了，拖不进文件 | 自动收起太短 | `AutoCollapseDelay` 调长或返回负数 |
| 12 | 右键关不掉详情页 | 开了 `AcceptsRightClick` 又没留出口 | 面板里放 × 或自己 `CloseDetailPage()` |
| 13 | 拖入高亮永远不消失 | 只靠 `onOver` 复位 | `onEnter` 点亮 + `onLeave` 熄灭 |
| 14 | 拖出后交互卡在「拖动中」 | 以为 up 回调会来 | `StartDragFiles` 返回后用 `finally` 复位 |
| 15 | 每次单击都卡一下 | 在 down 回调里发起拖出 | 在 move 里按位移阈值发起 |
| 16 | 双击动作执行了两次副作用 | 第一下已经走过一次单击 | 双击动作做幂等 |
| 17 | 卸载后旧 dll 删不掉 / 热重载没生效 | 插件自己持有程序集或文件句柄 | 别在静态字段里存插件类型；资源在 `Dispose` 里放 |
| 18 | 数据文件读不到 | 影子副本只复制 dll/json/pdb/config/xml/txt | 用嵌入资源，或释放到 `%LOCALAPPDATA%` |
| 19 | 装好却不在岛上 | 显示设置里没勾 | 显示设置 → 显示内容里勾上（与「启用」是两回事） |
| 20 | 组件偶尔整个消失 | 插件行预算不够，装不下就整块隐藏 | 用 `GetSlotBudget` / `GetPluginRowBudget` 提前挑内容 |
| 21 | 对端清单里没有我的工具 | MSP 接入关着（默认关），或工具名不合文法被拒 | 设置里打开 MSP 接入；看日志有没有「注册被拒」 |
| 22 | 工具调用报「参数缺失」 | 把委托又包了一层，SDK 读不到默认值 | 直接传方法组 / 带默认值的 lambda，别包 `DynamicInvoke` |
| 23 | 插件卸载了还能被调 | MSP 协议层没有「反注册工具」 | 工具实现里判状态，停用后返回明确错误 |

---

## 12. API 速查

标注「未开放」的接口**定义了但主程序没有接线**（注册了也不会显示），别依赖。

### `INotchPlugin`（入口，必须实现）

| 成员 | 说明 |
|---|---|
| `string Id` | 反域名唯一标识，发布后不要改 |
| `string DisplayName` | 显示名 |
| `string Version` | 版本 |
| `string Author` | 作者；默认空 → 退回程序集 `Company` |
| `void Initialize(IPluginHost host)` | 加载后调用 |

入口类**可选**实现 `IDisposable`，卸载 / 重载 / 退出时宿主会调。

### `IPluginHost`（宿主，`Initialize` 注入）

| 分类 | 成员 | 状态 |
|---|---|---|
| 注册 | `RegisterWidget(IWidget)` | 开放 |
| 注册 | `RegisterSecondaryWidget(ISecondaryWidget)` | **未开放** |
| 注册 | `RegisterSettingsPage(ISettingsPage)` | **未开放** |
| 主题 | `RenderTheme CurrentTheme` | 开放 |
| 提醒 | `PostReminder(ReminderData)` | 开放 |
| 设置 | `GetSetting(key, fallback)` / `SetSetting(key, value)` / `event SettingsChanged` | 开放 |
| 刷新 | `IDisposable ScheduleRefresh(TimeSpan, Action)` | 开放 |
| 交互 | `RequestRedraw()` | 空操作 |
| 交互 | `OpenDetailPage(id)` / `TryOpenDetailPage(id)` / `CloseDetailPage()` / `ToggleDetailPage(id)` | 开放 |
| 布局 | `InvalidateWidgetLayout()` | 开放 |
| 布局 | `GetPluginRowBudget()` / `GetSlotBudget(widgetId)` | 开放 |
| 窗口 | `CreateWindow(title, w, h)` | 开放 |
| 拖放 | `StartFileDrag(paths, allowMove)` | 开放 |
| MSP | `IDisposable RegisterMspTool(tool, desc, handler, inputSchema?)` | 开放（需开 MSP 接入） |
| MSP | `IDisposable RegisterMspToolsFrom(instance)`（扫 `[MspTool]`） | 开放（需开 MSP 接入） |
| MSP | `IDisposable DeclareMspSignal(action, description)` | 开放（需开 MSP 接入） |
| MSP | `PublishMspSignal(path, payload)` | 开放（需开 MSP 接入） |
| 卸载 | `RegisterUninstallDirectory(path)` / `RegisterUninstallAction(action)` | 开放 |

### `IWidget`

| 分类 | 成员 | 默认 |
|---|---|---|
| 标识 | `Id` / `DisplayName` / `DetailPage` | — |
| 尺寸 | `MeasureWidth(availableHeight)` | `0` |
| 绘制 | `Draw(canvas, rect, frame)` | — |
| 交互 | `HitTest(x, y, rect)` / `OnLeftClick(action, x, y)` / `OnRightClick()` | — |
| 交互 | `AcceptsRightClick` | `false` |
| 交互 | `AcceptsDoubleClick` / `OnLeftDoubleClick(x,y)` / `OnRightDoubleClick(x,y)` | `false` / 空 |
| 拖入 | `AcceptsFileDropWhenCollapsed` | `false` |
| 生命周期 | `OnActivate(host)` / `OnDeactivate()` | — |

### `IAutoWidthWidget`（推荐）

| 成员 | 默认 | 说明 |
|---|---|---|
| `Box? Layout(float availableHeight)` | — | 描述组成；`null` / 空盒 = 本帧不显示 |
| `bool WidthVaries` | `false` | 宽度随内容变化时置 `true`（1px 量化、变宽立即、变窄 600ms 确认） |

### `WidgetKit`

| 成员 | 说明 |
|---|---|
| `const float SlotGap` | `16f`，组件间距 |
| `const float AutoPadX` | `1f`，宿主给槽位加的左右留白（单侧） |
| `Text / Icon / Gap / Row / Stack / Min` | 六种盒子工厂 |
| `Measure(Box?, padX)` | 盒子 → 槽位宽（宿主内部用；插件一般不需要） |
| `ContentRect(SKRect slot)` | 槽位 → 内容区 |
| `Typeface(TextWeight)` | 与测量同一份字体面，**借来的，不要 Dispose** |

### `IDetailPage`

| 分类 | 成员 | 默认 |
|---|---|---|
| 尺寸 | `MeasureWidth()` / `MeasureHeight()` | — |
| 绘制 | `Draw(canvas, rect, frame)` | — |
| 交互 | `HitTest(x, y, rect)` / `OnAction(action, x, y)` | — |
| 鼠标 | `OnMouseDown/OnMouseMove/OnMouseUp(x, y)` / `OnMouseLeave()` | 空 |
| 拖入 | `OnFilesDragEnter(count)` / `OnFilesDragOver(x,y)` / `OnFilesDragLeave()` / `OnFilesDrop(paths)` | `false` / 空 |
| 透传 | `AcceptsRightClick` + `OnRightClick(x,y)` | `false` |
| 透传 | `AcceptsDoubleClick` + `OnLeftDoubleClick/OnRightDoubleClick(x,y)` | `false` |
| 收起 | `AutoCollapseDelay` | `Zero`（= 900ms） |

### `IPluginWindow`

| 成员 | 说明 |
|---|---|
| `SetDraw(Action<SKCanvas, int, int>?)` | 绘制回调（宽高为逻辑像素） |
| `SetMouse(down, move, up)` | 鼠标回调（逻辑坐标） |
| `SetKey(Action<char>?)` | 键盘字符 |
| `SetFilesDrop(Action<string[]>?)` | 拖入（松手）；传 `null` 取消并关闭接收 |
| `SetDragHover(onEnter, onOver, onLeave)` | 拖入过程 |
| `StartDragFiles(paths, allowMove = false)` | 拖出，阻塞到松手，返回是否被接受 |
| `RequestRedraw()` / `Close()` | 请求重绘 / 关闭 |

### 数据模型

```csharp
readonly record struct WidgetHit(string? Action);   // WidgetHit.None / .IsHit
readonly record struct RenderTheme(SKColor TextColor, SKColor SubTextColor,
    SKColor BackgroundColor, float GlobalDpi, float NotchBottomRadius);
readonly record struct WidgetFrame(RenderTheme Theme, byte Alpha, float TextOffsetY,
    float[]? Bars, bool IsHovered);

class ReminderData { Title, Body, Source, IconPath, AvatarPath, Duration, OnClick }
```

### 未开放的接口（定义了但没接线）

- `ISecondaryWidget`：副显示区只读信息。注册后不绘制。
- `ISettingsPage` / `ICustomSettingsPage` / `SettingControl`：声明式设置页。`RegisterSettingsPage` 只把页存进宿主列表，**全程序没有任何地方读它去渲染**。

做配置请走「自己存 + 自己做 UI」（详情页或 `CreateWindow`）。

---

## 13. 排错

1. **看日志**：`%LocalAppData%\NotchPeninsula\app.log`（1MB 轮转，连当前共 3 个文件）。加载、失败原因、你的 `Logger.Warn` 都在里面。
2. **看「我的插件」的状态**：`运行中 · vX · 作者` = 正常；失败会带归类与错误原文。
3. **在自己的代码里留痕**：

```csharp
using NotchPeninsula;   // Logger 在这个命名空间

try { /* 可能出错的初始化 */ }
catch (Exception ex) { Logger.Warn($"[我的插件] 初始化失败: {ex.Message}"); }
```

`Logger` 的工具：`Debug / Info / Warn / Error(msg, ex?)`，以及 `DebugThrottled(template)`（同一条**常量模板** 10 秒内只写第一行，窗口结束时补一行「期间重复 N 次」，专给按秒重试的路径用；模板必须是常量，含歌名 / 路径的动态消息无法归并）。日志内部加锁、任何异常自吞，不会影响主流程。

4. **常见定位顺序**：加载失败（先看归类）→ 组件不显示（看显示设置勾选 / 预算）→ 显示但画错（坐标系 / alpha / 字体）→ 点了没反应（`HitTest` 返回值 / 右键透传开关）→ 偶发崩溃（缓存画笔 / 跨线程访问原生对象）。

---

## 附录 A：接入自己的媒体来源

如果你写的是「替某个播放器取歌词 / 进度」的插件，可以实现 `IMediaSessionSource`（`Plugin/PluginApi.cs` 之外，`Plugin/MediaSessionSource.cs`）：

```csharp
// 示意骨架：字段略。每个属性都要线程安全地返回「当前快照」。
public sealed class MyPlugin : INotchPlugin, IMediaSessionSource
{
    public string SourceId => "com.example.myplayer";    // 稳定标识（日志 / 去重）
    public string SourceName => "我的播放器时间轴";
    public int Priority => 100;                          // 多路源同时可用时，大的赢

    public bool IsActive => _hasSession;                 // 暂停也算有会话
    public bool IsPlaying => _playing;
    public TimeSpan Position => _position;               // 宿主会按墙钟继续推算
    public TimeSpan Duration => _duration;               // 未知给 TimeSpan.Zero
    public string Title => _title;                       // 宿主不消费，你自己用
    public string Artist => _artist;                     // 同上
    public string? CoverUrl => _coverUrl;                // 同上

    public event Action? Changed;                        // 换歌 / 播放状态变化时触发

    public void TogglePlayPause() { }
    public void Next() { }
    public void Previous() { }
    public void OpenApp() { }
}
```

- 不需要额外注册：宿主在 `Initialize` **之后**用 `plugin is IMediaSessionSource` 自动登记，卸载时反登记。
- 若日志里出现「未实现 IMediaSessionSource，跳过外部媒体源登记」而你确实实现了它 —— 九成是**你把宿主的接口文件复制进了插件工程**：两边是不同 `Type`，判等必然为 false，而且不报错。引用主程序工程即可，别自带接口定义。
- 约定：所有属性都是「当前快照」，随取随用、线程安全；实现方自己在后台按自己的节奏刷新，宿主每帧只读缓存值。
- `Changed` **只**在「歌曲变了 / 播放状态变了 / 有无会话变了」时触发，**不**在秒级进度推进时触发（进度靠 `Position` + 墙钟推算）。
- `IsActive == false` 时其余字段一律视为无意义，宿主会回落到 SMTC。

## 附录 B：术语对照

| 中文 | 代码 |
|---|---|
| 灵动岛 | 岛体 / `NotchWindow` + `Renderer` |
| 插件根目录 | `plugins\`（`PluginManager.PluginsRoot`） |
| 入口类 | `INotchPlugin` |
| 宿主 | `IPluginHost`（实际注入的是按插件隔离的 `ScopedPluginHost`） |
| 组件 | `IWidget` |
| 详情页 | `IDetailPage` |
| 槽位 | 组件在插件行里占的那一格（`WidgetLayout.Slot`） |
| 插件行预算 | `Renderer.GetPluginRowBudget()`，见 [6.4](#64-预算与取舍) |
| 熔断 | 组件 / 详情页抛异常后被停用，不再绘制 |
| 影子副本 | 加载前复制到临时目录的那份插件文件，见 [2.2](#22-影子副本哪些文件会被复制) |
