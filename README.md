<div align="center">

<img src="./NPS_NotchPeninsula-logo.ico" alt="NotchPeninsula" width="180" />

<h1>NotchPeninsula</h1>
  
<p>专为 Windows 而生的刘海屏/灵动岛组件，极致内存占用与性能</p>

![Windows](https://img.shields.io/badge/Windows-10%2F11-0078D6?logo=windows\&logoColor=white)
![.NET 10](https://img.shields.io/badge/.NET-10.0-512BD4?logo=dotnet&logoColor=white)
![C#](https://img.shields.io/badge/C%23-14.0-239120?logo=csharp&logoColor=white)
![NAudio](https://img.shields.io/badge/NAudio-2.2-5C2D91)
![SkiaSharp](https://img.shields.io/badge/SkiaSharp-2.88-8A2BE2)
![Version](https://img.shields.io/badge/version-1.9.0-blue)
![License](https://img.shields.io/badge/License-Apache%202.0-green.svg)

<p>
  
  <a href="https://github.com/GEORGEWWWU/NotchPeninsula/wiki">进阶玩法</a> &nbsp; | &nbsp;
  <a href="https://github.com/GEORGEWWWU/NotchPeninsula/releases/latest">下载地址</a> &nbsp; | &nbsp;
  <a href="https://qm.qq.com/cgi-bin/qm/qr?k=i70z7rbl-VWpejQugvlXeARDUjwP7sIW\&jump_from=webapi\&authKey=b6Pj6zLuuCINDhafPJRttePdy3D45vvtWzcZ109LWoWYXkcKo8bNWI7fMhr+yV87" target="\_blank">交流群 1080730621</a>
  
</p>

<img width="1500" height="306" alt="NotchPeninsula 项目展示组件展示" src="https://github.com/user-attachments/assets/88150409-d532-486b-8b92-853aebab1165" />
<img width="1000" height="174" alt="媒体控制器 自定义字体" src="https://github.com/user-attachments/assets/fc3c12bd-08dc-4191-9f52-b66465a0ffa1" />
<img width="1920" height="200" alt="NotchPeninsula 封面 1 3 0 效果" src="https://github.com/user-attachments/assets/b80857fa-58a5-4379-8f1c-cf75db704ab1" />

</div>

## 项目概览

NotchPeninsula 是一个面向 Win 10/11 的“刘海屏”风格桌面小组件，采用 Win32 + SkiaSharp + NAudio 的组合实现，核心目标是把系统媒体状态、通知消息和音频可视化整合到屏幕顶部的极窄悬浮区域中。

它会在桌面顶部生成一个轻量透明窗口，把系统媒体状态、通知消息与音频可视化集中在一条极窄的悬浮区域里：

- 系统媒体播放状态与多平台桌面歌词（含译文与歌词扫光）
- 实时音频频谱，以及时间日期与 CPU / 内存占用
- Windows Toast 通知与可选提示音
- 剪贴板链接识别，以及可安装第三方插件的自定义组件
- 自动隐藏、窗口置顶、鼠标穿透与自动更新检测

这种设计适合单屏、窄边框布局，以及希望保留桌面空间同时拥有交互反馈的使用场景。

## 功能特性

| 模块               | 说明                                                                                                                                                                         |
| ---------------- | -------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| **媒体控制**         | 自动接管系统媒体会话（浏览器 / 网易云音乐 / QQ 音乐 / 酷狗 / Spotify / Apple Music / Echo Music / LX Music 等），支持播放暂停、上下曲与时间轴拖动。判据只有一条：**系统同时给出歌名与歌手**即为音乐模式（联网取歌词与封面），否则按视频模式处理 —— 只显示标题与应用图标，不联网 |
| **桌面歌词**         | 多引擎依次回退（顺序见下），支持译文与**逐字歌词**（逐字优先，拿不到逐字数据时自动回退卡拉 OK 的整行扫光）；歌词再长也不截断，岛体按真实文本长度自适应加宽                                                                                                                        |
| **实时音频可视化**      | WASAPI Loopback 捕获系统输出，分析为 5 组频段并平滑渲染；未播放媒体时保留音量动态效果；自动跟随系统默认输出设备                                                                                                          |
| **Windows 通知**   | 监听 Toast 通知，支持「缩略 / 紧凑 / 完整」三种呈现并可整体关闭；可选消息提示音（内置 14 个音源或本地音频），走独立音量通道，不改动系统音量                                                                                             |
| **时间日期与硬件**      | 时间日期 + CPU / 内存占用；岛上显示什么、按什么次序，由「显示设置 → 显示内容」一张列表统一编排                                                                                                                      |
| **剪贴板链接**        | 事件驱动监听，复制链接后自动弹出面板、点击即用默认浏览器打开；稳态零 CPU、零额外内存                                                                                                                               |
| **Just Solo 歌词** | 接入 Just Solo LyricServer，获取带时间轴的歌词与服务端推送的频谱；音量操作单一出口 —— 连上就调播放器音量，否则调系统主音量                                                                                                 |
| **插件系统**         | 独立 dll 放入程序同级 `plugins` 目录即自动加载，支持热重载；开放组件、详情页、定时刷新、提醒、自定义窗口五类扩展点。插件窗口与详情页均支持**双向文件拖放**（可拖入文件 / 文件夹，也可拖出到资源管理器），组件还可声明「收起态也收文件」。[详见开发文档](./NPS_PluginsAPI.md)            |
| **外观与个性化**       | 主题（深色 / 浅色 / 跟随系统）与背景不透明度，刘海样式、底部圆角、各状态尺寸与全局 DPI 缩放；字体可整体替换自定义字体（缺字自动回退）；窗口置顶、鼠标穿透、多显示器指定                                                                                  |
| **自动隐藏**         | 总开关「允许灵动岛自动隐藏」下三种模式**互相独立、可任意组合**：焦点离开时 / 暂停播放后 / 全屏应用时。**穿透模式下同样生效**（隐藏后点屏幕顶部那条细边即可唤回，也可用托盘菜单「唤回灵动岛」）；通知、媒体展开面板、插件详情页与剪贴板弹窗期间一律不隐藏                                                                                           |
| **交互**           | 双击封面切回正在播放的应用（默认关闭，可在交互设置开启）；岛内右键按区域直达对应设置页；托盘菜单快捷控制                                                                                                                       |

### 歌词 / 译文 / 封面获取顺序

换歌时（歌名或歌手变化）一次性联网获取，按顺序回退、取到即停。

- **主歌词**：落月 API（QQ 音乐） → QQ 音乐官方歌词 → 落月 API（网易云） → 网易云 → LRCLIB
- **译文**：落月 API（QQ 音乐） → QQ 音乐官方歌词 → 落月 API（网易云） → 网易云
- **封面**：落月 API（QQ 音乐） → 网易云 → QQ 音乐（直连兜底） → 该程序自身的应用图标

> 落月那两个源还会顺带带回**逐字歌词**（逐字时间轴），「媒体设置 → 逐字歌词」开启时会优先用它推进扫光；
> 其它源没有这份数据，那一行自动退回整行均匀推进。覆盖范围取决于曲库本身 —— 实测落月 QQ 侧基本都有，
> 落月网易云侧**只有部分歌**有（孤勇者 / 勾指起誓有，花がら / 起风了 / 晴天 / Lemon 都是空的）。
> **逐字与整行推进是同一条链的两种驱动方式** —— 设置里只有「逐字歌词」**一个**开关：
> 关掉即纯文字；开着则有逐字数据就按字推进、**拿不到逐字数据时自动回退卡拉 OK 的整行扫光**
>（这条说明也写在设置页的开关旁边，不必翻文档）。

> **QQ 曲库的标题匹配对非中文歌放宽一级**：这类歌名在库里常写成「原名 + 中译别名」
>（《花がら(Withered Flower)》在库里是《花がら (枯花)》），归一化后互不包含，全字匹配会整条落空。
> 因此**歌名不是中文**（含假名或纯拉丁）且全字匹配落空时，会再按「包含匹配」尝试一次
>（公共子串足够长即算命中）。**歌手校验不放宽**，中文歌也不受影响。

> **用网易云音乐播放时走「网易优先」**：上表中网易系档位（落月 API（网易云） → 网易云）整体提到最前，
>   
> **歌词与封面都是如此** ——
>   
> `落月 API（网易云） → 网易云 → 落月 API（QQ 音乐） → QQ 音乐官方歌词 → LRCLIB`。
>   
> 其他播放器按表中顺序，网易系档位留在后排当兜底。

## 使用方式

### 启动

在 Windows 上直接运行编译后的程序即可。程序启动后会驻留到托盘区，界面默认显示在屏幕顶部中央。

### 设置入口

右键托盘图标：**打开设置 / 唤回灵动岛 / 开机自启 / 退出**。

设置窗口按左侧导航分为 7 个页签：

- **个性化中心** —— 主题、背景不透明度、刘海样式与圆角、全局折叠态高度、通知尺寸、DPI 缩放
- **通用设置** —— 开机自启、窗口置顶、系统消息通知与提示音、剪贴板链接检测、灵动岛字体
- **显示设置** —— 显示形态、目标显示器、**显示内容**（勾选显示项并调整先后次序）
- **媒体设置** —— 媒体控制、目标媒体平台、歌词与译文、逐字歌词、歌词延迟
- **交互设置** —— 自动隐藏、媒体交互方式、双击封面跳转应用、鼠标穿透
- **插件中心** —— 导入 / 启用 / 禁用 / 热重载 / 移除插件、打开插件目录、插件市场
- **关于软件** —— 版本信息与相关链接

> 「媒体交互方式」是折叠态媒体区的**展开功能总闸**，同时决定展开入口：
>   
> 开启后左键留给双击跳转、展开走**右键**；关闭则左键单击展开。

### 权限要求

由于项目使用了 Windows 通知监听能力，首次运行时可能需要允许此应用访问通知：

- 设置 → 隐私和安全性 → 通知
- 打开对应权限开关

如果关闭了通知权限，Toast 监听将无法正常显示消息。

## 项目结构

```text
NotchPeninsula/
├── Core/                             # 程序骨架与系统底层
│   ├── Program.cs                    # 程序入口，单例启动与配置读写
│   ├── NotchWindow.cs                # 主窗口逻辑、动画、托盘、消息队列调度
│   ├── Win32.cs                      # Win32 API 封装
│   ├── SystemSettingManager.cs       # 系统音量控制
│   ├── UpdateManager.cs              # GitHub 版本检测与更新弹窗
│   └── Logger.cs                     # 日志
├── Render/                           # 渲染与字体
│   ├── Renderer.cs                   # 岛体渲染主流程：尺寸/主题/模式参数 + Draw() 主流程
│   ├── Renderer.Layout.cs            # 岛体内容布局：按「显示内容」顺序表混排 / 穿透唤醒按钮
│   ├── Renderer.Plugin.cs            # 插件行绘制与详情页（命中、预算、顺序表）
│   ├── Renderer.Toast.cs             # 通知 Toast 与剪贴板面板
│   ├── Renderer.Media.cs             # 媒体/歌词/逐字/时间轴/硬件统计
│   ├── Renderer.Paint.cs             # 画笔池、字体、矢量路径与图标资源
│   ├── FontConfig.cs                 # 灵动岛字体统一配置中心
│   └── LyricsFont.cs                 # 歌词字体
├── Media/                            # 媒体、音频与歌词
│   ├── MediaController.cs            # 媒体会话识别、歌词 / 译文 / 封面获取与逐字歌词
│   ├── MediaLogoProvider.cs          # 媒体平台身份判定（AUMID 关键字归类）
│   ├── AppIconProvider.cs            # 按会话 AUMID 取应用自身图标（视频模式封面 / 音乐模式兜底）
│   ├── MediaAppLauncher.cs           # 双击媒体控制 → 跳回对应应用（窗口激活 + AUMID 兜底）
│   ├── JustSoloLyricClient.cs        # Just Solo LyricServer 歌词客户端
│   ├── AudioAnalyzer.cs              # WASAPI Loopback 音频频谱分析
│   ├── Audio.cs                      # NAudio 音频底层适配
│   ├── ToastSoundConfig.cs           # 通知提示音配置与校验（动态扫描 data\sound）
│   └── ToastSoundPlayer.cs           # 通知提示音播放队列
├── UI/                               # 自绘窗口
│   ├── ConsoleWindow.cs              # 设置窗口：状态字段、构造、窗口生命周期、消息泵、渲染主流程
│   ├── ConsoleWindow.Render.cs       # 设置窗口绘制：侧边栏、各页签、下拉浮层与控件绘制
│   ├── ConsoleWindow.WndProc.cs      # 设置窗口悬停命中（整窗热区）
│   ├── ConsoleWindow.Click.cs        # 设置窗口点击分派（顺序敏感的 if/else 链）
│   ├── ConsoleWindow.Backdrop.cs     # 亚克力/云母材质与窗口壳（材质窗、圆角、最小化）
│   ├── ConsoleWindow.Plugin.cs       # 插件中心与设置项辅助逻辑
│   ├── ConsoleWindow.Settings.cs     # 尺寸/通知内容/字体等设置项的读写
│   ├── ConsoleWindow.Paint.cs        # 设置窗口画笔池
│   └── TrayMenuWindow.cs             # 托盘菜单
├── Notify/                           # 消息通知通道
│   ├── Toast.cs                      # 系统 Toast 监听 + 本地 HTTP 推送接口
│   ├── ToastIconProvider.cs          # 通知图标解析（内置别名 / 链接 / base64 / 本地路径）
│   ├── ClipboardMonitor.cs           # 剪贴板链接监听
│   └── AppActivator.cs               # 通过 AUMID 激活应用
├── Plugin/                           # 插件系统（API、宿主、加载器、管理、窗口、布局）
├── data/image/                       # 通知内置图标（qq / windows）
├── data/sound/                       # 内置提示音（*.wav + sound.json 显示名覆盖）
├── NPS_PluginsAPI.md                 # 插件开发文档
├── build.ps1                         # 一键发布为单文件 exe
├── NPS_NotchPeninsula-logo.ico       # 应用图标
├── NotchPeninsula.csproj             # .NET 项目配置
├── NotchPeninsula.slnx               # 解决方案文件
├── msvcp140.dll                      # VC++ 运行库（随程序分发）
├── vcruntime140.dll                  # VC++ 运行库（随程序分发）
├── LICENSE                           # Apache 2.0
└── README.md                         # 项目说明
```

> 目录只是物理分类，**所有源码仍在 `namespace NotchPeninsula` 下**（C# 命名空间与文件夹无关）。
>   
> 移动/重命名文件不会影响类型引用，插件 DLL 也不受影响。

## 构建与运行

**环境要求**：Windows 10 / 11，.NET 10 SDK。

```bash
dotnet restore
dotnet build -c Release                                        # 本地构建
dotnet publish -c Release -r win-x64 --self-contained false     # 发布
```

推荐用仓库自带的打包脚本：先清理 `bin` / `obj` / `publish`，再发布为单文件 exe（约 35 MB，不含 .NET 运行时）。

```powershell
.\build.ps1            # 产物：publish\NotchPeninsula.exe
.\build.ps1 -Clean     # 仅清理，不打包
```

运行产物中的可执行文件即可，程序会驻留托盘区；目标机需自备 .NET 10 Desktop Runtime。

## 兼容性与说明

- 本项目是 Windows 桌面程序，不适合在 macOS/Linux 平台直接运行。
- 使用了 Windows 通知管理 API，因此需要在 Windows 10/11 环境中执行。
- 若你的系统未安装相应音频设备或输出设备，音频频谱可能会显示为空白或静态效果。

## 相关文档

- 详见WIKI：<https://github.com/GEORGEWWWU/NotchPeninsula/wiki>
- 插件市场：<https://nps.georgewu.top/market>

## 许可协议

本项目采用 Apache License 2.0 开源协议。详情见 [LICENSE](./LICENSE)。

---

<div align="center">
  
  <p><strong>NotchPeninsula</strong> · 让 Windows 顶部栏也能拥有“灵动岛”的体验</p>
  
</div>
