# NotchPeninsula 插件开发文档

这份文档面向完全没有写过插件的人：介绍程序的插件系统是什么、插件怎么装进程序、以及最重要的——怎么写一个属于自己的 dll 插件。文档全程对着本项目的真实代码编写，文中的接口名、调用方式都可以直接在 `Plugin/` 目录的源码里找到。

---

## 一、插件系统是什么

`NotchPeninsula` 程序会把一个系统托盘区变成一个“灵动岛”，在屏幕上方显示时间、硬件占用、媒体控制等内容。原生自带的内容是写死在程序里的；而**插件**允许任何人用 C# 写一小段代码，编译出一个 dll，为这个灵动岛增加全新的内容，比如课表、天气、倒计时、系统监控，甚至一个你自己的小窗口。

插件做的事情，本质上是向程序“注册”下面这几类东西：

- **组件**（Widget）：显示在灵动岛主区域里的一段内容，可点击。
- **详情页**（DetailPage）：右键某个组件后展开的更详细内容页。
- **设置页**（SettingsPage）：在设置窗口里出现的、属于你这个插件的配置区域。
- **刷新定时器**（ScheduleRefresh）：程序按指定时间间隔在后台调用你的代码，比如每 30 秒更新一次数据。
- **提醒**（PostReminder）：弹出灵动岛顶部那种几秒钟的提示消息。
- **自定义窗口**（CreateWindow）：你自己独立于灵动岛的一个可绘制、可被鼠标和键盘操作的小窗口。

程序的插件管理器（`PluginManager`）会扫描一个专门的插件目录，把里面的每个 dll 当作一个插件加载。关于加载的技术细节这里先不展开，你只需要知道：**只要把一个合法的插件 dll 放进那个目录，重启程序（或点“重新加载”）后它就会被程序发现并运行。**

---

## 二、插件怎么安装进程序

插件目录的位置：**程序 exe 所在文件夹下的 `plugins` 文件夹**。也就是说，如果程序装在 `C:\NotchPeninsula\`，那插件目录就是 `C:\NotchPeninsula\plugins\`。

如果你不想自己从头写插件，也可以**直接从官方插件市场下载现成的插件**：在浏览器打开插件市场地址 `https://nps.georgewu.top/market`，按分类挑一个插件下载，拿到 dll 后再用下面两种方式之一装进程序即可。市场里每一个插件的简介、版本、作者都会列出来，方便你挑选和更新。

同时，项目**欢迎并允许所有人发布自己写好的插件**。任何人都可以把整理好的插件（至少一个 dll，带依赖的话连同依赖文件一起）投递到这个市场，让其他用户搜索、下载、使用。发布前建议先按下文把插件在自己电脑上跑通、测试稳定再上传，并在简介里写清楚它能做什么、怎么用。

把已经拿到的插件装进程序有两种方式，你选哪种都行：

1. **手动复制文件夹**：直接把编译好的 dll 复制到 `plugins` 文件夹里，然后启动程序（或使用程序里的“重新加载”功能）。
2. **用程序的导入功能**：在程序的“插件中心”里点导入按钮，选择一个 dll 文件，程序会自动把它复制进 `plugins` 目录并立即尝试加载。

插件目录支持两种放置方式：

- **单文件布局**：插件没有任何外部依赖，就把 `MyPlugin.dll` 直接放在 `plugins` 根目录下即可。
- **目录布局**：插件还需要别的 dll 一起运行（比如引用了第三方库），就把 `MyPlugin.dll` 连同那些依赖一起放进一个子文件夹 `plugins\MyPlugin\` 里。程序会自动挑选这个文件夹里的入口 dll（优先读取该文件夹下的 `plugin.json`，里面可以写 `{ "dll": "MyPlugin.dll" }` 来指定哪个是入口；没有的话就找和文件夹同名的 dll；再没有就找第一个不是宿主组件的 dll）。目录里的依赖 dll 会被一并加载，这样带依赖的插件也能工作。

在“插件中心”里，每个插件都可以被**启用 / 禁用 / 移除 / 调整位置**：

- 启用 = 加载并运行；禁用 = 卸载并停止，下次启动也不会加载。
- 移除 = 先卸载，再把文件移进 `plugins\_recycle\` 回收站（不是直接删除，还能手动找回）。
- 调整位置 = 决定你的插件内容在灵动岛上排在左边还是右边。

程序启动时会清理上一次加载留下的临时影子目录，然后自动加载所有已启用且没有失败的插件，所以你不用担心残留旧版本文件。

---

## 三、写插件前要认识的几个名词

整个插件 API 定义在程序源码的 `Plugin/PluginApi.cs` 文件里。写插件时，你会和这几个名词打交道，每个名词就是一句话能讲清的概念，理解了它们就能读懂任何插件代码：

- **入口类（INotchPlugin）**：每个插件必须有一个类实现这个接口。程序加载你的 dll 后，会找到这个类，调用它的 `Initialize` 方法，把刚才讲的那些能力通过参数递给你。它的三个属性 `Id`、`DisplayName`、`Version` 用来标识这个插件。

- **宿主（IPluginHost）**：程序递给你入口类的那个对象就是宿主。你通过它调用 `RegisterWidget`、`PostReminder`、`ScheduleRefresh`、`GetSetting`、`CreateWindow` 等方法来使用程序的能力。简单说，**宿主就是插件向主程序请求服务的通道**。

- **组件（IWidget）**：灵动岛上显示的一段内容。实现这个接口的类负责两件事：告诉程序自己需要多大（`MeasureWidth`），以及把自己画出来（`Draw`）；同时还能响应鼠标点击（`HitTest` / `OnLeftClick` / `OnRightClick`）。

- **注册（Register）**：程序默认不知道你的插件里有什么，你在 `Initialize` 里调用 `host.RegisterWidget(...)` 就是把你做好的组件“上架”给程序，告诉程序“我有个组件，请把它放进灵动岛”。不注册的东西不会出现在界面上。

- **刷新回调 + 提醒 + 设置**：`ScheduleRefresh` 让程序每隔固定时间在后台调用你的一段逻辑（比如拉取数据）；`PostReminder` 弹一条几秒钟的提示；`GetSetting` / `SetSetting` 用来把插件自己的配置（比如开关状态、数字、选项）写到 Windows 注册表里并读回来——下次启动程序时你的插件还能记住上次的设置。

- **渲染帧（WidgetFrame / RenderTheme）**：程序在屏幕上每一帧绘制时，会准备一个包含当前主题颜色、透明度、文字偏移量等信息的“快照”交给你的 `Draw` 方法。你在绘制时用这些信息，就能让你的内容和程序自带的主题、动画表现一致——比如白色和黑色主题下都能看清。

---

## 四、编写你的第一个插件（完整示例）

写插件不需要改动主程序，你只需要新建一个独立的类库工程。下面给出一个能直接编译运行的完整示例：一个“Hello 插件”，它会在灵动岛上显示一个圆点和一个文字，点击圆点可以切换开关并弹提醒，同时每 30 秒在后台刷新一次运行秒数。

### 步骤 1：新建工程

创建一个类库工程。关键点有三条，请逐一对照：

1. **目标框架**必须和主程序完全一致，否则程序加载不了你。当前是 `net10.0-windows10.0.19041.0`，写在你的 `.csproj` 里。
2. 项目类型必须是 **类库**（`OutputType` 为 `Library`），最终产物是 dll 而不是 exe。
3. **引用主程序工程**。插件本身不定义这些接口，接口都在主程序里，你要通过 ProjectReference 引用 `NotchPeninsula.csproj`，然后在代码里 `using NotchPeninsula.Plugins;` 才能拿到 `INotchPlugin`、`IPluginHost`、`IWidget` 这些类型。

下面是 `.csproj` 文件内容（假设你的插件叫 `HelloPlugin`）：

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <!-- 必须与主程序保持一致，否则无法被加载 -->
    <TargetFramework>net10.0-windows10.0.19041.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <OutputType>Library</OutputType>
    <AssemblyName>HelloPlugin</AssemblyName>
    <Version>1.0.0</Version>
    <RootNamespace>HelloPlugin</RootNamespace>
  </PropertyGroup>

  <ItemGroup>
    <!-- 引用主程序，拿到 INotchPlugin / IPluginHost / IWidget 等接口定义 -->
    <ProjectReference Include="..\NotchPeninsula.csproj" />
  </ItemGroup>

  <!-- 编译后自动把 dll 复制到主程序输出目录的 plugins 文件夹，省得手动搬 -->
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

### 步骤 2：写入口类和组件

新建一个源码文件（可以是一个文件里同时放入口类和组件，也可以分开放在两个文件里）。下面是完整代码，可以直接复制，注释说明了每一段在做什么：

```csharp
using System;
using System.Threading;
using NotchPeninsula.Plugins;
using SkiaSharp;

namespace HelloPlugin;

/// <summary>
/// 插件入口：程序找到实现 INotchPlugin 的类后，会实例化它并调用 Initialize。
/// </summary>
public sealed class HelloPlugin : INotchPlugin, IDisposable
{
    private IPluginHost? _host;
    private IDisposable? _timer;
    private HelloWidget? _widget;
    private int _seconds; // 后台线程写，渲染线程读，用内存操作保证安全

    // 反域名风格的唯一标识，避免和其他插件撞车；显示名和版本用于列表展示。
    public string Id => "com.example.hello";
    public string DisplayName => "Hello 插件";
    public string Version => "1.0.0";

    public void Initialize(IPluginHost host)
    {
        _host = host;

        // 1) 立刻弹一条提醒，直观证明插件被加载了。
        host.PostReminder(new ReminderData
        {
            Title = "Hello 插件已加载",
            Body = "灵动岛右上角出现了一个圆点，可以点它",
        });

        // 2) 每 30 秒在后台给秒数 +30，展示“定时刷新”怎么用。
        _timer = host.ScheduleRefresh(TimeSpan.FromSeconds(30),
            () => Interlocked.Add(ref _seconds, 30));

        // 3) 把组件“注册”给程序，它才会出现在灵动岛上。
        _widget = new HelloWidget(this);
        host.RegisterWidget(_widget);
    }

    // 渲染线程安全地读取后台累计的秒数。
    internal int Seconds => Volatile.Read(ref _seconds);

    // 点击圆点后由组件回调进来：保存开关状态 + 给用户反馈。
    internal void OnToggled(bool enabled)
    {
        if (_host is not IPluginHost h) return;
        h.SetSetting("enabled", enabled ? "1" : "0"); // 写入注册表，下次启动还记得
        h.PostReminder(new ReminderData
        {
            Title = "Hello 插件",
            Body = enabled ? "已开启（再点一次关闭）" : "已关闭（再点一次开启）",
        });
    }

    // 程序卸载插件时会调用 Dispose，把申请的资源还回去。
    public void Dispose()
    {
        _timer?.Dispose();
        _widget?.Dispose();
    }
}

/// <summary>
/// 灵动岛组件：一个圆点 + 一段文字，点击切换开关。
/// </summary>
internal sealed class HelloWidget : IWidget
{
    private readonly HelloPlugin _plugin;
    private volatile bool _on = true;

    // 文字内容变化时才重新测量，避免无意义地重复计算。
    private string _lastText = "";
    private float _lastTextWidth;
    private bool _lastOn;
    private int _lastSeconds = -1;

    public HelloWidget(HelloPlugin plugin) => _plugin = plugin;

    public string Id => "com.example.hello.widget";
    public string DisplayName => "Hello";
    public IDetailPage? DetailPage => null; // 不提供详情页

    // 告诉程序这个组件要多宽（逻辑像素）：圆点 + 间距 + 文字宽。
    public float MeasureWidth(float availableHeight)
    {
        RefreshText();
        return 10f + 8f + _lastTextWidth + 4f;
    }

    // 每帧被调用：把你的内容画到灵动岛上。rect 是程序分给你的区域。
    public void Draw(SKCanvas canvas, SKRect rect, WidgetFrame frame)
    {
        RefreshText();
        float cy = rect.MidY + frame.TextOffsetY; // 垂直居中

        // 圆点：开启=绿，关闭=灰；透明度跟随程序的淡入动画。
        // 注意：画笔在这里“临时创建、用完即释放”，不要存在字段里跨线程复用，
        // 否则插件开关时可能因为原生对象被释放而崩溃（详见文末“踩坑”）。
        using var dot = new SKPaint
        {
            IsAntialias = true,
            Color = (_on ? new SKColor(76, 175, 80) : new SKColor(130, 130, 130))
                        .WithAlpha(frame.Alpha),
        };
        canvas.DrawCircle(rect.Left + 6f, cy, 4f, dot);

        // 文字用程序当前的主题色，黑/白主题下都能看清。
        using var text = new SKPaint
        {
            IsAntialias = true,
            TextSize = 12.5f,
            Color = frame.Theme.TextColor.WithAlpha(frame.Alpha),
        };
        canvas.DrawText(_lastText, rect.Left + 18f, cy + 4.5f, text);
    }

    // 命中检测：判断鼠标点在了组件范围内没有。返回一个“动作名”表示这次点击要做什么。
    public WidgetHit HitTest(float x, float y, SKRect rect)
        => x >= 0 && x <= rect.Width && y >= 0 && y <= rect.Height
            ? new WidgetHit("toggle")
            : WidgetHit.None;

    // 左键点击触发，动作名就是 HitTest 返回的那个。
    public void OnLeftClick(string? action, float x, float y)
    {
        _on = !_on; // 切换开关，volatile 保证下一帧渲染就能看到新状态
        _plugin.OnToggled(_on);
    }

    // 右键点击；我们没有详情页，这里什么都不做。
    public void OnRightClick() { }

    // 程序启动加载时调用，把上次保存的开关状态读回来。
    public void OnActivate(IPluginHost host)
        => _on = host.GetSetting("enabled", "1") != "0";

    // 程序停止时调用。
    public void OnDeactivate() { }

    // 只在内容变化时才重建文字并重新测量宽度。
    private void RefreshText()
    {
        int sec = _plugin.Seconds;
        if (sec == _lastSeconds && _on == _lastOn && _lastText.Length > 0) return;

        _lastSeconds = sec;
        _lastOn = _on;
        _lastText = _on
            ? (sec <= 0 ? "Hello" : $"Hello · {sec}s")
            : $"OFF · {sec}s";
        using var measure = new SKPaint { IsAntialias = true, TextSize = 12.5f };
        _lastTextWidth = measure.MeasureText(_lastText);
    }
}
```

### 步骤 3：编译并放进程序

在命令行进入你的插件工程目录，运行下面这条命令：

```
dotnet build HelloPlugin.csproj -c Debug
```

如果 `csproj` 里包含了我们写的那段 `InstallToHostPlugins` 目标，编译完成后 dll 会自动出现在主程序输出目录的 `plugins` 文件夹里；如果没有，你手动把生成的 `HelloPlugin.dll` 复制到 `plugins` 文件夹即可。然后启动（或重启)主程序，插件就会出现在灵动岛和“插件中心”里。

---

## 五、组件（IWidget）详解——把内容画到灵动岛上

这是最常见也最核心的部分。组件负责一段显示内容，一共要实现五个方法，外加两个生命周期方法，每帧和每次点击都会用到它们。逐个解释：

- **`DisplayName` / `DetailPage`**：显示名用于把组件区分开来；`DetailPage` 指向这个组件的详情页，没有就返回 `null`（右键点击程序会默认展开详情页，没有就不展开）。
- **`MeasureWidth(float availableHeight)`**：程序需要知道你的组件占多宽，才能决定灵动岛整体多宽并排布大家。返回一个逻辑像素的宽度。内容（比如文字）变了，就返回一个不同的值，灵动岛会自动做宽度变化动画。高度一般不必自己决定，程序会统一处理，你按传入的高度来布局。
  这个返回值的语义是「**完整显示我的内容需要多宽**」，不是「我希望多宽」。程序会用它和本帧剩余空间比对：装得下就按这个宽度给你、内容完整显示；装不下这一帧就**整个组件都不显示**（程序不会替你压缩、截断或加省略号——那才是真正的显示不全）。所以别为了「挤进去」而少报宽度，也别用岛体上限（`800`）去夹自己的返回值。详见第九节「开放接口清单」里 `IWidget` 那条。
- **`Draw(SKCanvas canvas, SKRect rect, WidgetFrame frame)`**：每一帧都调用，把你想要的内容画出来。`rect` 是程序分给你的一块区域，含上下左右（`rect.MidY` 是垂直中线）；`frame` 是这一帧的上下文，见下一条。
- **`WidgetFrame`（渲染帧）**：包含 `Theme`（当前主题色，如 `frame.Theme.TextColor` 是文字颜色）、`Alpha`（透明度，0–255，跟随程序的淡入和叠化）、`TextOffsetY`（文字垂直偏移，用来和整体布局对齐）。绘制时用 `颜色.WithAlpha(frame.Alpha)` 就能让你的内容配合程序动画淡入淡出，看起来浑然一体。
- **`HitTest(...)` / `OnLeftClick` / `OnRightClick`**：鼠标交互三步。先 `HitTest` 判断点没点中，点中了返回一个动作名（随便起，比如 `"toggle"`）；之后程序调用 `OnLeftClick` 并把动作名交给你。`WidgetHit.None` 表示没点中；`new WidgetHit("toggle")` 表示点中并携带动作名。
- **`OnActivate(IPluginHost host)` / `OnDeactivate()`**：组件被启用和停止时各调用一次。`OnActivate` 是你读回持久化设置的好时机。

绘制所用的 `skiaSharp` 画笔（`SKPaint`）有一个非常重要的约定：**在 `Draw` 方法里临时创建、画完就释放，绝不要把它们缓存成字段在多个线程之间复用**。原因见文末“踩坑”部分，这是你写插件必须遵守的安全规则。

---

## 六、经常用到的其他能力

这些能力和组件配合，能让插件真正实用起来。每个都只用一段话说明怎么用，写法在示例里已有体现的部分会用示例回指。

**定时刷新（ScheduleRefresh）**：`host.ScheduleRefresh(interval, 回调)` 让程序每隔一段时间在后台线程调用你的回调（最小间隔 100 毫秒）。回调里更新你自己的数据即可，渲染线程会自动读到最新值，无需手动触发重绘。它返回一个 `IDisposable`，`Dispose` 就停止刷新。适合做“每 5 分钟拉一次课表”“每 30 秒轮询一次状态”这类事情。注意回调在后台线程运行，更新数据时要保证渲染线程的安全读取（用 `volatile`、`Interlocked` 或 `lock`）。示例如第五节和示例代码里的 `_seconds`。

**提醒（PostReminder）**：`host.PostReminder(new ReminderData { Title = ..., Body = ..., Duration = ... })` 弹出一条几秒钟的灵动岛顶部提示。`ReminderData` 里 `IconPath` 可以配图标图片路径（可选），`OnClick` 可以配点击后的回调（可选）。适合做“数据更新了”“事件已提醒”这类反馈。

**持久化设置（GetSetting / SetSetting）**：插件自己的配置存在 Windows 注册表里，程序会自动给每个插件的配置 key 加上 `Plugin.<你的Id>.` 前缀，所以你和其他插件不会互相覆盖。`GetSetting(key, 默认值)` 读，`SetSetting(key, value)` 写，存的是字符串。组件加载时在 `OnActivate` 里读回，运行时用 `host.SettingsChanged` 事件监听设置被改动（比如用户在设置页里改了配置）。这是让插件“记住上次状态”的机制，示例图里的开关就是用这套实现的。

**设置页（ISettingsPage）**：想让用户在程序设置窗口配置你的插件，就实现这个接口并 `host.RegisterSettingsPage(...)`。它只需要声明一个标题和一组控件，程序负责绘制和持久化。控件有三种：`ToggleSetting`（开/关）、`ChoiceSetting`（单选）、`NumberSetting`（数字）。程序把控件的变化存进注册表，会触发 `SettingsChanged` 事件，你的插件订阅后就能立刻响应。**注意：目前主程序还没有真正渲染设置页，注册了也不会显示（暂未开放）**，机制先写好，等接线补齐就能直接用。

**详情页（IDetailPage）**：右键组件展开的详细内容页。在组件的 `DetailPage` 属性里返回一个实现 `IDetailPage` 的对象即可（没有就返回 `null`，右键就只会打开设置窗口）。它的方法和组件类似：`MeasureWidth` / `MeasureHeight` 报尺寸、`Draw` 画内容、`HitTest` / `OnAction` 处理点击，只是画面更大，可以展示更多信息或做成一个小设置面板。**已开放**：右键组件后灵动岛会按你报的尺寸整块展开成详情页，展开 / 收起都带和原生一致的弹簧动画；岛内左键会按 `HitTest` 命中的动作名回调 `OnAction`。约定与细节见第七节。

**自定义窗口（CreateWindow）**：`host.CreateWindow(title, width, height)` 创建一个独立于灵动岛的、可用 SkiaSharp 绘制、支持鼠标和键盘的小窗口（自动居中、右上角有关闭按钮、Esc 可关闭）。它返回一个 `IPluginWindow`，你可以 `SetDraw` 设置绘制回调 `(canvas, width, height)`，`SetMouse` 设置鼠标按下/移动/松开回调，`SetKey` 设置键盘字符回调，画完调用 `RequestRedraw()` 刷新，用完 `Close()` 关闭。适合做“悬浮工具面板”这类不依赖灵动岛的小工具。

---

## 七、详情页（IDetailPage）怎么用

详情页就是「右键组件后，灵动岛整块展开成你的内容」。它不占用灵动岛的常驻位置，只在用户主动右键时才出现，所以适合放“详细信息、设置项、操作按钮”这类平时不该露出来的东西。用法只有三步：

1. **给组件挂上详情页**：在组件的 `DetailPage` 属性里返回一个实现 `IDetailPage` 的对象（建议建一次就缓存起来，别每次访问都 `new`）。返回 `null` 表示这个组件没有详情页，右键它仍然只是打开设置窗口。
2. **报尺寸**：`MeasureWidth()` / `MeasureHeight()` 返回你想要的大小，单位是逻辑像素。**尺寸完全由你决定**，宿主只做一层保护性裁剪：宽度会被限制在 `180 ~ 1000`，高度限制在 `48 ~ 480`，免得插件把岛体撑到屏幕外。灵动岛会用弹簧动画平滑过渡到这个尺寸，不用你自己做动画。
3. **画内容 + 处理点击**：`Draw(canvas, rect, frame)` 里的 `rect` 就是整个岛体区域（左上角是 `rect.Left / rect.Top`，`rect.MidX / rect.MidY` 是中心），照着它布局即可；`HitTest(x, y, rect)` 返回动作名，用户左键点中后宿主回调 `OnAction(action, x, y)`。`x / y` 都是相对 `rect` 左上角的逻辑坐标，和你 `HitTest` 里判断的坐标系完全一致。

   ⚠️ **但 `rect` 在展开 / 收起动画期间是「插值尺寸」，不能拿它当排版基准。** 宿主用弹簧动画把岛体从折叠态尺寸过渡到你报的目标尺寸，动画进行中的每一帧 `rect` 都只是过程值（例如目标 380×108，前几帧可能只有 130×34）。按它换行会导致文字每帧重排、每帧跳位——肉眼就是「展开动画不丝滑」；而且任何拿 `rect` 尺寸当 key 的分行缓存都会每帧失效，等于每帧全量重排一次（长文本尤其明显）。

   正确做法：**排版永远按你 `MeasureWidth` / `MeasureHeight` 返回的目标尺寸算**，绘制时再把整块内容对齐 / 缩放到当前 `rect`：

   ```csharp
   float layoutW = /* 你的目标宽度 */, layoutH = /* 你的目标高度 */;
   float scale = Math.Min(rect.Height / layoutH, 1f); // 弹簧有过冲，封顶 1 免得边缘被裁
   canvas.Save();
   canvas.Translate(rect.MidX, rect.MidY);
   canvas.Scale(scale);
   canvas.Translate(-layoutW * 0.5f, -layoutH * 0.5f);
   // ...按 (0, 0, layoutW, layoutH) 这个固定坐标系绘制...
   canvas.Restore();
   ```

   这样动画期间内容随容器一起放大，展开完成后 `scale = 1`，与设计尺寸 1:1。

交互上还有几条约定，知道就行，不用你写代码：

- **右键展开 / 收起**：右键组件展开详情页；详情页展开时再在岛内右键一次就收起（不会再弹设置窗口）。
  注意岛内的右键是**分层消费**的，顺序为：收起详情页 → 广播给命中的组件 → 打开设置窗口。
  **原生媒体控制器区域（标题 / 歌词 / 频谱 / 播放按钮 / 空白）不消费右键** —— 那里的右键一律打开设置窗口，
  所以插件组件只要不画到那上面去，右键就完全归你。
  另外通知（Toast）/ 剪贴板面板 / 详情页显示期间，岛体被整块接管，这些原生命中区都会作废，右键不会误触发。
- **鼠标移开就收起**：鼠标离开灵动岛，详情页会自动收起（原生媒体控制面板同理）。详情页带约 **0.9s 延迟** ——
  它的尺寸由插件决定，展开动画结束后鼠标可能刚好落在新面板之外，宿主会等 0.9s 再收，这期间鼠标回到岛上就取消。
  鼠标移到岛外点一下左键则立即收起。
  所以插件不用自己做关闭按钮——当然你想加也行（调用 `host.CloseDetailPage()` 即可）。
- **左键优先给详情页**：详情页展开期间，岛内左键只会走详情页的 `HitTest` / `OnAction`，不会误触到原生媒体按钮。
- **通知优先**：详情页展开时如果来了新的通知（Toast），灵动岛会先显示通知，通知结束后详情页自动回来。
- **异常熔断**：`MeasureWidth` / `MeasureHeight` / `Draw` 里抛异常，这个详情页会被停用并自动收起，主程序照常运行（日志里能看到原因）。所以别在里面做可能阻塞很久的事。
- **尺寸变化要主动报**：详情页内容变了、想让岛体跟着变大变小，直接让 `MeasureWidth` / `MeasureHeight` 返回新值即可；宿主在下一次展开时会重新测量（同一次展开期间尺寸是固定的，不会每帧抖动）。
  反过来说：**同一次展开期间，即使你的内容变了，岛体尺寸也不会跟着变**（宿主没有「请重测详情页」的接口）。所以要么保证内容能落在已报的尺寸内显示完整，要么在内容变化时自行按当前尺寸重新排版（例如上面那段 `scale` 写法，空间不够时内容整体缩一点，而不是被裁掉）。
- **主动开合**：`host.OpenDetailPage("你的组件Id")` / `host.CloseDetailPage()` 可以让插件自己控制详情页的开合（比如数据加载完了自动弹出来）。传入的 Id 必须是组件 `Id` 属性那个字符串。
- **一个组件一个详情页**：详情页是挂在组件上的，一个组件最多对应一个详情页；多个组件可以各自有自己的详情页。

最省事的验证方式：直接看本仓库 `TestPlugin/` 目录下的测试插件，它把上面这套全部用了一遍，右键它的组件就能看到详情页长什么样。

---

## 八、几个必须避开的坑

新手写插件最容易在这几处出问题，提前知道能省下大把调试时间：

1. **画笔（SKPaint）不要缓存跨线程复用。** SkiaSharp 的画笔是原生对象，你把它存在字段里，在别的线程（绘制线程）使用，会在开关插件卸载时因为“原生对象已被释放”直接触发崩溃（`0xC0000005` 访问冲突）。正确做法是：在 `Draw` 方法里临时 `new` 一个、画完 `using` 释放。这是本项目已经踩过的真实教训。Typeface 等原生字体对象同理，也应避免无谓缓存；只做英文/数字内容的组件直接走默认字体即可。

2. **后台线程写、渲染线程读的数据必须安全。** `ScheduleRefresh` 的回调在后台线程执行，而 `Draw` / `MeasureWidth` 在渲染线程执行。两边共用同一个变量时，用 `volatile`、`Interlocked` 或 `lock` 来保证正确性，否则会出现数据读到一半、状态不同步的问题。

3. **目标框架必须和主程序一致。** `TargetFramework` 写错（比如用低版本的 .NET）程序就加载不了你的 dll，会在“插件中心”里显示加载失败。当前主程序是 `net10.0-windows10.0.19041.0`，照抄示例即可。

4. **一定要 `using NotchPeninsula.Plugins;` 并引用主程序工程。** 接口类型都在主程序里，不引用、不写 `using`，代码根本编译不过。

5. **入口类必须实现 `INotchPlugin`。** 程序是靠“dll 里有没有实现 `INotchPlugin` 的类”来识别一个 dll 是不是合法插件的。如果你的 dll 里没有这样的类，程序会判定“不是合法插件”并拒绝加载（或直接删除导入的文件）。类要写成 `public`，不能是抽象类或接口。

6. **不要在 `Initialize` 里做耗时阻塞。** `Initialize` 里应该尽量只做注册和启动后台刷新，不要在这里长时间卡住主程序。重活放到 `ScheduleRefresh` 回调或独立线程去完成。

7. **给资源一个清理机会。** 如果插件申请了定时器、线程、句柄等，让入口类实现 `IDisposable` 并在 `Dispose` 里释放。程序卸载插件时会调用它，这样热重载（升级插件）时旧代码才能真正被回收干净。

8. **组件不注册就不会显示。** 你在 `Initialize` 里 `new` 了组件对象还不够，必须 `host.RegisterWidget(...)` 把组件交给程序。同样的，设置页要 `RegisterSettingsPage`、二级内容要 `RegisterSecondaryWidget`。

看完这些、再对照示例代码动手写一遍，你就能做出自己的灵动岛插件了。遇到问题可以从“插件中心”看每个插件的加载状态和错误信息，多数加载失败（缺依赖、框架不符、没实现入口类）都会在那里给出提示。

---

## 九、开放接口清单（面向有经验的人）

这一节给有经验的开发者一份“接线总览”：程序对外开放了哪些接口、每个接口负责衔接哪一段能力、有哪些成员。用一句话概括——**你只需要实现好入口类，其余能力全部由下面的接口自由组合**。所有定义都在 `Plugin/PluginApi.cs` 一个文件里，翻源码就能逐行核对。需要特别说明：**接口定义了、但主程序还没有真正接线实现（注册了也不会在界面上出现，或只有占位逻辑）的，会在后面标上（暂未开放）**。正式开放的接口可以放心用；标了（暂未开放）的接口建议你真正要用之前先确认它已经转正，否则写了也看不到效果。

**入口类 `INotchPlugin`**（每个插件唯一必须实现的接口）
- 属性：`Id` / `DisplayName` / `Version`，用来标识插件并在列表里展示。
- 方法：`void Initialize(IPluginHost host)`，程序加载后调用，在这里注册组件、设置页，并申请定时刷新等。

**宿主 `IPluginHost`**（`Initialize` 注入给你的对象，插件向程序请求全部服务的通道）
- 注册：`RegisterWidget(IWidget)`（已开放，注册后渲染侧真正绘制）/ `RegisterSecondaryWidget(ISecondaryWidget)`（暂未开放，注册后无界面绘制）/ `RegisterSettingsPage(ISettingsPage)`（暂未开放，注册后设置窗口尚未渲染）。
- 主题：`RenderTheme CurrentTheme { get; }` 取当前帧主题快照。
- 提醒：`void PostReminder(ReminderData)`。
- 设置持久化：`string GetSetting(string key, string fallback)` / `void SetSetting(string key, string value)`，键会自动加 `Plugin.<你的Id>.` 前缀隔离，不会互相覆盖；`event Action? SettingsChanged` 在设置被写入后触发。
- 刷新：`IDisposable ScheduleRefresh(TimeSpan interval, Action callback)`，后台线程周期性回调，返回对象 `Dispose` 即停止。
- 交互：`void RequestRedraw()`（常驻 60FPS 渲染下为空操作，事件驱动化预留）/ `void OpenDetailPage(string widgetId)`（已开放，展开指定组件的详情页，组件不存在或没有详情页时返回 false 且不展开）/ `void CloseDetailPage()`（已开放，收起当前详情页）。
- 布局：`void InvalidateWidgetLayout()`（已开放，请求宿主重新测量本插件组件的宽度）。
  宿主的组件宽度是按「组件注册表版本」缓存的——只在插件注册 / 注销 / 排序时调一次 `MeasureWidth`，之后每帧直接复用缓存值（稳态 60FPS 零测量开销）。
  所以**组件宽度随内容变化的插件**（例如按文本长度自适应），在内容变化后必须调用它通知宿主，下一帧才会重新测量并用新宽度布局；岛体宽度会走既有弹簧动画平滑过渡到新值。
  内容没变、宽度没变就别调（会让宿主重测一次所有插件组件）。
- 布局：`float GetPluginRowBudget()`（已开放，**本插件所在位置的剩余可用宽度**）。
  语义是「**不显示本插件时，它那个位置还剩多少长度**」（含与原生内容之间的 16px 间距）。宿主按组件从左到右的优先级分配，把原生内容（媒体控制器及其长标题自适应、组合模式下的时间日期 / 硬件占用）以及**排在前面（更高优先级）的插件**已经占掉的宽度都扣掉了，剩下的就是这个值。
  **宽度随内容自适应的插件应该用它来决定内容取多长**：拿到的内容如果「完整显示所需宽度 > 这个剩余」，宿主下一帧会把该组件整体隐藏（见下一条），此时换一条更短的更划算。
  为什么不给插件写死阈值：原生占用与岛体尺寸都是可配置的（控制台 `Custom_StandbyW` / `Custom_MediaW` 等），组合显示开着时原生模块还会占掉一大截 —— 写死任何值都会在某个场景下失准。
  返回 `0` = 本位置已无空间（原生吃满，或本帧插件行被通知 / 剪贴板 / 详情页接管）；返回正无穷 = 宿主还没跑过首帧。这两种情况都不适合判断内容长短，插件应退回自己的保守估值。
  该值随帧刷新，无需缓存；渲染线程与后台线程调用都安全。
- 窗口：`IPluginWindow CreateWindow(string title, int width, int height)`。

**主显示组件 `IWidget`**（灵动岛主区域里的一段内容，一个插件可注册多个）
- 属性：`Id` / `DisplayName` / `IDetailPage? DetailPage`（已开放：非空时右键该组件会在灵动岛展开这个详情页；为 null 则右键只打开设置窗口）。
- 测量与绘制：`float MeasureWidth(float availableHeight)` / `void Draw(SKCanvas canvas, SKRect rect, WidgetFrame frame)`。
  `MeasureWidth` 的语义是「**完整显示本组件内容所需的宽度**」，不是「我希望多宽」，更不是「上限定多宽」。宿主拿它和本帧剩余空间比对：装得下就按这个宽度布局、内容完整显示；装不下就本帧整个组件不显示。
  `MeasureWidth` **只在组件注册表版本变化时被调用一次**（插件注册 / 注销 / 内容顺序变化），返回值被缓存进后续每一帧的布局，宿主不会每帧问你。所以宽度要随内容变化的插件，在内容更新后得主动调一次 `host.InvalidateWidgetLayout()`，否则宽度会一直停在首次测量值上（同理，宿主切换灵动岛字体后字宽会变，需要重测的话请订阅 `FontConfig.Changed`）。
  宿主不对组件宽度做上下限裁剪，但请务必返回**真实需求值**：为了「塞进去」而少报，只会让文字被压缩显示；用 `800` 这类岛体上限去夹自己，会让宿主误判成「刚好装得下」。真要定上限，就往大了定（纯防异常值的保险丝），放不下交给宿主隐藏即可。
- 岛体总长上限与「插件行取舍」：宿主岛体的总长上限是 `800`（与消息弹窗 `Toast` 的最大长度一致，常量 `Renderer.MAX_ISLAND_WIDTH`），Toast / 剪贴板面板的自适应宽度、组合模式总宽、插件行取舍都以它封顶。
  规则一句话：**每个组件要么完整显示，要么完全不显示。** 宿主用「岛体总长上限 − 原生内容本帧占用宽度」得出插件行预算，按注册顺序逐个贪心放行——所需宽度能完整落进剩余空间的组件才显示，装不下的组件本帧整体不显示（不绘制、不留位、不响应点击），后面的组件仍可继续尝试。原生内容照常显示，岛体宽度也不会被撑过上限。
  典型场景：媒体控制器开着且歌词很长（触发了自适应撑宽），原生内容吃掉大半宽度，剩余装不下你的组件时它就会暂时消失，等歌词变短、原生内容收窄后自动回来。
  宿主对媒体文本的自适应也留了余量：文本区上限是 `Renderer.MEDIA_TEXT_MAX_WIDTH`（默认 480），超长标题 / 歌词不会把岛体吃满，右侧的律动频谱与播放按钮位置始终稳定。
  这是宿主侧行为，**对所有插件一视同仁**，插件不用做任何处理、也无法阻止。组合模式（`GetCompositeWidth`）走同一套预算规则，插件的显示与否同样只取决于「能不能完整放下」。
  **插件能做的**：拿 `host.GetPluginRowBudget()` 在取内容时就比一次，主动避开「抽了一条塞不进去的内容 → 被隐藏」这种情况（本插件 OneSaying 就是这么做的：内容太长就换一条短的）。
- 命中与点击：`WidgetHit HitTest(float x, float y, SKRect rect)` / `void OnLeftClick(string? action, float x, float y)` / `void OnRightClick()`。
- 生命周期：`void OnActivate(IPluginHost host)` / `void OnDeactivate()`。

**副显示组件 `ISecondaryWidget`**（副显示区的只读信息）（暂未开放）
- 只有 `Id` 和 `void Draw(SKCanvas canvas, SKRect rect, WidgetFrame frame)`。当前注册后不会在任何地方真正绘制。

**详情页 `IDetailPage`**（右键组件展开后的详细内容）（已开放）
- `float MeasureWidth()` / `float MeasureHeight()` / `void Draw(SKCanvas, SKRect, WidgetFrame)` / `WidgetHit HitTest(float, float, SKRect)` / `void OnAction(string? action, float x, float y)`。
- 尺寸由插件决定，宿主只把宽裁剪到 `180 ~ 1000`、高裁剪到 `48 ~ 480`；右键组件展开，鼠标离开岛体（约 0.9s 后）/ 岛内再右键 / 点击岛外都会收起，`OpenDetailPage` / `CloseDetailPage` 也可用。细节见第七节。

**设置页 `ISettingsPage` / `ICustomSettingsPage`**（程序设置窗口里属于插件的区域）（暂未开放）
- 声明式：`string Title` + `IReadOnlyList<SettingControl> Controls`。控件有三种：`ToggleSetting`（开/关）、`ChoiceSetting`（单选）、`NumberSetting`（数字），各自带默认值，注册后设置窗口目前尚未渲染它们。
- 自定义式：实现 `ICustomSettingsPage` 可自己绘制整个设置页，提供 `MeasureHeight()`、`Draw(SKCanvas, SKRect, RenderTheme)`、`OnMouseDown/Move/Up(float, float)`。一个设置页可以同时实现 `ISettingsPage` 和 `ICustomSettingsPage`（暂未开放）。

**自定义窗口 `IPluginWindow`**（`CreateWindow` 的返回值）
- `SetDraw(Action<SKCanvas, int, int>)` 设置绘制回调；`SetMouse(down, move, up)` 设置鼠标三个事件；`SetKey(Action<char>)` 设置键盘字符；`RequestRedraw()` 请求重绘；`Close()` 关闭。

**渲染上下文 `WidgetFrame` / `RenderTheme`**（`Draw` 每帧收到的快照）
- `WidgetFrame`：`Theme`（主题）、`Alpha`（合成透明度 0–255）、`TextOffsetY`（文字垂直偏移）、`Bars`（可选频谱）、`IsHovered`。
- `RenderTheme`：`TextColor` / `SubTextColor` / `BackgroundColor` / `GlobalDpi` / `NotchBottomRadius`。

**命中模型 `WidgetHit`**——`readonly record struct WidgetHit(string? Action)`。`Action` 由组件自己定义（如 `"toggle"` / `"next"`），未命中用 `WidgetHit.None`，`IsHit` 判断是否命中。`OnLeftClick` 收到的动作名就是这里返回的。

**提醒数据 `ReminderData`**——`Title`（标题）、`Body`（正文）、`IconPath`（可选图标路径）、`Duration`（时长，默认 4 秒）、`OnClick`（可选点击回调）。

一句话总结整个数据流：程序加载 dll → 找到 `INotchPlugin` 入口并调 `Initialize` → 插件借 `IPluginHost` 注册 `IWidget`、申请定时刷新 → 渲染循环每帧调组件的 `MeasureWidth` + `Draw` 画到灵动岛 → 鼠标命中后调 `HitTest` / `OnLeftClick`（右键则展开 `DetailPage`，详情页自己的 `HitTest` / `OnAction` 接管岛内左键）→ 卸载时调 `OnDeactivate` / `Dispose`。当前真正开放、能立刻看到效果的是主显示组件、详情页、定时刷新、提醒、设置持久化和自定义窗口；设置页、副显示组件这两类接口已冻结可用，但主程序还未完成接线（暂未开放），等待后续版本补齐。整个开放面就这么多，剩下的就是把你想展示的数据填进 `Draw` 里。
