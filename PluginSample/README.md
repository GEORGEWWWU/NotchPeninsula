# 手把手：写一个 NotchPeninsula 测试插件

本目录是一个**可直接编译**的最小插件示例（`HelloPlugin`）。照它做，你就能把自己的功能塞进灵动岛。

---

## 1. 插件是怎么被加载的

```
plugins/ 目录里的 DLL
        │  ① 扫描（单文件型 / 目录型都支持）
        ▼
PluginManager          —— 做「影子拷贝」到临时目录，避免占用原文件
        │  ② 可回收 AssemblyLoadContext 加载
        ▼
反射找到 INotchPlugin 的实现类 → Activator 实例化
        │  ③ 调用 Initialize(host)
        ▼
你的代码开始运行：可以弹提醒、注册组件、定时刷新……
```

几个关键点：

- **接口定义在主程序里**（`NotchPeninsula.Plugins.INotchPlugin` 等），插件只引用、不重复定义。
  加载器会把这些共享类型统一交给主程序，所以插件里 `INotchPlugin` 和主程序里的是**同一个类型**。
- **影子拷贝**：加载前会把 DLL 复制到 `%TEMP%\NotchPeninsula\plugins\` 再加载，
  所以原 DLL 不会被占用，可以随时覆盖升级。
- **热重载**：在「插件中心」点「重载」= 卸载旧的加载上下文 → 重新读盘 → 重新加载。
  你也可以先覆盖 `plugins\HelloPlugin.dll`，再点「重载」，新代码立即生效。

---

## 2. 写一个插件需要做什么

只需要三步：

### 步骤 1：建一个类库工程

`HelloPlugin.csproj`：

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0-windows10.0.19041.0</TargetFramework>  <!-- 必须和主程序一致 -->
    <OutputType>Library</OutputType>                                 <!-- 输出 dll，不是 exe -->
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <AssemblyName>HelloPlugin</AssemblyName>
  </PropertyGroup>
  <ItemGroup>
    <!-- 引用主程序即可拿到全部插件接口与 SkiaSharp -->
    <ProjectReference Include="..\NotchPeninsula.csproj" />
  </ItemGroup>
</Project>
```

> 注意：`TargetFramework` 一定要和主程序一致，否则加载时报兼容性错误。

### 步骤 2：实现 `INotchPlugin`

```csharp
public sealed class HelloPlugin : INotchPlugin
{
    public string Id => "com.example.hello-plugin";  // 全局唯一，建议反域名
    public string DisplayName => "Hello 插件示例";
    public string Version => "1.0.0";

    public void Initialize(IPluginHost host)          // ← 插件的入口
    {
        host.PostReminder(new ReminderData { Title = "已加载", Body = "Hello!" });
    }
}
```

`Initialize` 就是你的 `Main`。它会由主程序在加载成功后调用一次。

### 步骤 3：编译 & 安装

```powershell
# 编译（在仓库根目录）
dotnet build PluginSample/HelloPlugin.csproj

# 产物：
#   PluginSample\bin\Debug\net10.0-windows10.0.19041.0\HelloPlugin.dll
```

本示例工程带了一个 `InstallToHostPlugins` 目标：**编译完会自动把 `HelloPlugin.dll`
复制到主程序输出目录的 `plugins\` 文件夹**，所以直接启动主程序就能看到效果。

若想手动安装，把 DLL 复制到 `bin\<配置>\net10.0-windows10.0.19041.0\plugins\`，
或在主程序里「插件中心 → 导入 DLL」选择该文件即可。

加载成功后，灵动岛会弹出「Hello 插件已加载」的提醒 —— 说明 DLL 已经跑在主程序进程里了。

**体验热重载**：改一下本文件里的提示文字 → 重新 `dotnet build PluginSample/HelloPlugin.csproj`
→ 回到「插件中心」点该插件的「重载」，新文案立刻生效（无需重启主程序）。

---

## 3. 插件能用的能力（`IPluginHost`）

| 能力 | 方法 | 说明 |
| --- | --- | --- |
| 弹提醒 | `PostReminder(ReminderData)` | 复用灵动岛现有的 4 秒 Toast |
| 定时刷新 | `ScheduleRefresh(interval, callback)` | 回调在**后台线程**执行，返回 `IDisposable`，`Dispose()` 即停止 |
| 读写设置 | `GetSetting` / `SetSetting` | key 自动加 `Plugin.<id>.` 前缀，互不干扰 |
| 监听设置变化 | `event SettingsChanged` | 主机写入设置后触发 |
| 当前主题 | `CurrentTheme` | 含 `TextColor` / `SubTextColor` / `BackgroundColor` / `GlobalDpi` |
| 注册组件 | `RegisterWidget(IWidget)` | 主显示区小组件，主机每帧调 `MeasureWidth` / `Draw` |
| 注册副组件 | `RegisterSecondaryWidget` | 副显示区只读信息 |
| 注册设置页 | `RegisterSettingsPage(ISettingsPage)` | 声明式控件，主机负责绘制与持久化 |
| 自有窗口 | `CreateWindow(title, w, h)` | 创建一个 SkiaSharp 绘制的独立窗口 |

**线程模型（很重要）**：`Draw` / `MeasureWidth` 跑在渲染线程，`ScheduleRefresh` 回调跑在后台线程。
两者共享的字段请用 `lock` / `Interlocked` / `volatile` 保护，否则可能读到半更新的数据。

---

## 4. 调试与排错

- 加载失败时「插件中心」会直接把错误显示在插件名下面（例如缺少依赖、没有实现 `INotchPlugin`）。
- 详细堆栈在日志里，按主程序同目录的 `Logger` 输出查看。
- 常见原因：
  - **DLL 里没有 `INotchPlugin` 实现** → 导入时会被自动回滚删除。
  - **TargetFramework 不一致** → 报兼容性错误。
  - **缺依赖 DLL** → 把插件做成**目录型**：`plugins/MyPlugin/` 放主 dll + 依赖 dll（可用 `plugin.json` 指定入口）。

---

## 5. 进阶建议

- `Id` 一旦发布就不要改，否则用户那边的「启用/禁用」状态会丢失。
- 需要长期后台任务（轮询、WebSocket）时，务必用 `ScheduleRefresh` 或自己起线程，
  **绝不要在 `Initialize` 里写死循环**，那会卡住加载。
- 宿主会自动回收它交给插件的资源（刷新句柄 / 组件 / 设置页）。
  但插件内部如果订阅了**全局静态事件**、或者自己起了线程 / 打开了文件句柄，
  记得退订与释放；如果希望宿主在卸载时帮你调用清理逻辑，可以让插件实现 `IDisposable`。

### 关于「热重载后旧代码的回收」

主程序用**可回收的 AssemblyLoadContext** 加载插件，卸载时会尝试把旧代码整块回收。
.NET 对这个过程的要求很严格：只要还有**任何一处**强引用指向旧插件对象，
旧代码就回收不掉。主程序已经做了该做的（摘除所有登记引用、按值复制插件返回的字符串、
把卸载动作放在独立栈帧里），但回收仍然是**尽力而为**：

- 功能上不受影响 —— 点「重载」一定能加载到新代码、且 DLL 文件不被占用；
- 极端情况下旧代码可能延迟到后续 GC 才释放，日志里会打印
  `加载上下文暂未被回收` 的提示，属正常现象，不代表热重载失败。

所以：**不要在插件里长期持有外部对象的强引用**，那样才是真正会持续泄漏的写法。
