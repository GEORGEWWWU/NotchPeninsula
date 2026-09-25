<div align="center">

<img src="./NPS_NotchPeninsula-logo.ico" alt="NotchPeninsula" width="180" />

<h1>NotchPeninsula</h1>
<p>专为 Windows 而生的刘海屏/灵动岛组件，极致内存占用与性能</p>

<img alt="Windows" src="https://img.shields.io/badge/Windows-10%2F11-0078D6?logo=windows&logoColor=white" />
<img alt=".NET 10" src="https://img.shields.io/badge/.NET-10.0-512BD4?logo=dotnet&logoColor=white" />
<img alt="C#" src="https://img.shields.io/badge/C%23-14.0-239120?logo=csharp&logoColor=white" />
<img alt="NAudio" src="https://img.shields.io/badge/NAudio-2.2-5C2D91" />
<img alt="SkiaSharp" src="https://img.shields.io/badge/SkiaSharp-2.88-8A2BE2" />
<img alt="Version" src="https://img.shields.io/badge/version-1.8.0-blue" />
<img alt="License" src="https://img.shields.io/badge/License-Apache%202.0-green.svg" />

<p>
  <a href="https://github.com/GEORGEWWWU/NotchPeninsula/wiki">进阶玩法</a> &nbsp; | &nbsp;
  <a href="https://github.com/GEORGEWWWU/NotchPeninsula/releases/latest">下载地址</a> &nbsp; | &nbsp;
  <a href="https://qm.qq.com/cgi-bin/qm/qr?k=i70z7rbl-VWpejQugvlXeARDUjwP7sIW&jump_from=webapi&authKey=b6Pj6zLuuCINDhafPJRttePdy3D45vvtWzcZ109LWoWYXkcKo8bNWI7fMhr+yV87" target="_blank">交流群 1080730621</a>
</p>

<img width="1500" height="306" alt="NotchPeninsula 项目展示组件展示" src="https://github.com/user-attachments/assets/88150409-d532-486b-8b92-853aebab1165" />
<img width="1000" height="174" alt="媒体控制器 自定义字体" src="https://github.com/user-attachments/assets/fc3c12bd-08dc-4191-9f52-b66465a0ffa1" />
<img width="1920" height="200" alt="NotchPeninsula 封面 1 3 0 效果" src="https://github.com/user-attachments/assets/b80857fa-58a5-4379-8f1c-cf75db704ab1" />




</div>

## 项目概览

NotchPeninsula 是一个面向 Win 10/11 的“刘海屏”风格桌面小组件，采用 Win32 + SkiaSharp + NAudio 的组合实现，核心目标是把系统媒体状态、通知消息和音频可视化整合到屏幕顶部的极窄悬浮区域中。

它会在桌面顶部生成一个轻量透明窗口，实时展示：

- 当前系统媒体播放状态（标题、艺术家、播放/暂停）
- 多平台音乐应用的媒体来源识别与桌面歌词
- 实时音频频谱柱状图
- 时间日期与 CPU / 内存硬件占用
- Windows Toast 通知气泡与可配置消息提示音
- 剪贴板链接识别与一键打开
- 可安装第三方插件扩展的自定义组件
- 可配置的自动隐藏、窗口置顶、鼠标穿透与自动更新检测
- 托盘菜单快捷控制

这种设计适合单屏、窄边框布局，以及希望保留桌面空间同时拥有交互反馈的使用场景。

## 功能特性

### 1. 媒体控制

- 自动识别系统媒体会话，覆盖浏览器、网易云音乐、QQ 音乐、酷狗、Spotify、Apple Music、Echo Music、LX Music 等常见平台
- 「浏览器媒体」只接管浏览器会话；「自动媒体 + 手动选择软件」可锁定指定进程且优先级最高，Pake / Tauri 等套壳播放器指定后也能正常出词
- 支持播放 / 暂停、上一曲 / 下一曲；展开交互模式下左键点击媒体区域可展开控制面板（含时间轴拖动与歌词延迟微调）
- 歌词按曲目信息联网获取，多个引擎依次回退；支持卡拉 OK 动效
- 歌词再长也不截断：岛体按真实文本长度自适应加宽（上限 1920，宁可溢出屏幕也不裁切）
- 鼠标离开灵动岛自动收起媒体面板与插件详情页；岛内右键按区域直达对应设置页

### 2. 实时音频可视化

- 通过 WASAPI Loopback 捕获系统输出，把音频能量分析成 5 组频段并平滑渲染为频谱柱
- 未播放媒体时保留简洁的音量动态效果
- 自动跟随系统默认输出设备，切换扬声器 / 耳机 / HDMI 后即时生效，无需重启

### 3. Windows 通知显示

- 监听系统 Toast 通知，展示标题与正文，自动折叠淡出
- 支持「缩略 / 紧凑 / 完整」三种呈现方式，可在通用设置中整体关闭
- 可选消息提示音（默认关闭）：内置 15 个音源，也可选本地音频；音量十档可调并支持试听
- 通知弹出期间临时禁用穿透与自动隐藏，保证通知可见、可点击

### 4. 时间日期与硬件占用

- 实时读取并平滑展示 CPU / 内存占用，并提供时间日期模块
- 岛上显示哪些内容、按什么次序，由「显示设置 → 显示内容」一张列表统一决定
- 只勾选一个就是传统的待机样式，多选则横向拼成一条完整信息栏

### 5. 剪贴板链接识别

- 事件驱动监听剪贴板，稳态零 CPU / 零额外内存占用
- 复制链接后自动弹出面板，点击即可用默认浏览器打开；可在通用设置中关闭
- 面板弹出期间临时禁用穿透，保证「打开」按钮可点击

### 6. Just Solo 歌词

- 接入 Just Solo LyricServer，实时获取带时间轴的歌词与服务端推送的频谱
- 音量操作只有一个出口：连上 Just Solo 就调播放器音量，没连上才调系统主音量
- 两侧音量互不干扰，改哪边只镜像哪边

### 7. 插件系统

- 插件以独立 dll 放入程序同级 `plugins` 目录，启动时自动加载，支持热重载
- 开放组件、详情页、定时刷新、提醒、自定义窗口五类扩展点
- 内置「插件中心」：导入 / 启用 / 禁用 / 热重载 / 移除，列表显示运行状态、版本号与作者
- **「启用」与「显示」相互独立**：取消显示只是把插件从岛上收起，插件仍在后台运行；启用会自动勾上显示，禁用则自动收起
- 内置插件市场入口；开发文档见 [NPS_PluginsAPI.md](./NPS_PluginsAPI.md)

### 8. 外观与个性化

- 主题模式（深色 / 浅色 / 跟随系统）与背景不透明度
- 多种刘海样式、底部圆角、各状态尺寸与全局 DPI 缩放可调
- 灵动岛字体可整体替换为自定义字体，缺字自动回退
- 窗口置顶与鼠标穿透；多显示器下可指定目标显示器
- 设置窗口自动套用系统材质（Win11 云母 / Win10 亚克力），系统不支持时回退纯色暗色外观

### 9. 自动隐藏与交互体验

- 无媒体状态下自动隐藏到屏幕顶部，鼠标悬停即恢复
- 两个互斥的附属开关（默认关闭，需先开启自动隐藏）：
  - **暂停播放后自动隐藏**：媒体暂停 / 停止时也把岛体收起
  - **全屏自动隐藏**：检测到全屏应用（视频、游戏、演示）时无条件让位，正在播放也隐藏
- 穿透模式下自动隐藏强制失效，关闭穿透后原设置自动恢复
- 通知、剪贴板面板、插件详情页展开期间一律不隐藏，保证不漏消息
- 点击交互区控制播放与切歌，托盘菜单可快速调出设置窗口

## 使用方式

### 启动

在 Windows 上直接运行编译后的程序即可。程序启动后会驻留到托盘区，界面默认显示在屏幕顶部中央。

### 设置入口

右键托盘图标可看见菜单：

- 打开设置
- 开机自启
- 退出

在设置窗口中，按左侧导航分类配置（共 7 个页签）：

- **个性化中心**：主题、背景不透明度、刘海样式与圆角、各状态尺寸、DPI 缩放
- **通用设置**：开机自启、窗口置顶、系统消息通知（通知开关 / 内容模式 / 消息提示音）、剪贴板链接检测、灵动岛字体
- **显示设置**：刘海形态、目标显示器、**显示内容**（一张列表勾选岛上显示哪些内容并调整先后次序，内置模块带蓝色「（内置）」标记）
- **媒体设置**：媒体控制、目标媒体平台、歌词与卡拉 OK、歌词延迟
- **交互设置**：自动隐藏（含两个互斥附属开关）、媒体交互方式、鼠标穿透
- **插件中心**：导入 / 启用 / 禁用 / 热重载 / 移除插件、打开插件目录、进入插件市场（显示与排序见「显示设置」）
- **关于软件**：版本信息与相关链接

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
│   ├── Renderer.Media.cs             # 媒体/歌词/卡拉OK/时间轴/硬件统计
│   ├── Renderer.Paint.cs             # 画笔池、字体、矢量路径与图标资源
│   ├── FontConfig.cs                 # 灵动岛字体统一配置中心
│   └── LyricsFont.cs                 # 歌词字体
├── Media/                            # 媒体、音频与歌词
│   ├── MediaController.cs            # 媒体会话识别、歌词与卡拉 OK
│   ├── MediaLogoProvider.cs          # 媒体平台站标与 LOGO 管理
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
├── data/image/                       # 平台 logo / 图标资源
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
> 移动/重命名文件不会影响类型引用，插件 DLL 也不受影响。

## 构建与运行

### 环境要求

- Windows 10 / 11
- .NET 10 SDK
- Visual Studio 2022 或 .NET CLI

### 本地构建

```bash
dotnet restore
dotnet build -c Release
```

### 发布

```bash
dotnet publish -c Release -r win-x64 --self-contained false
```

生成的发布文件通常位于：

```text
bin\Release\net10.0-windows10.0.19041.0\win-x64\publish\
```

### 一键打包（推荐）

仓库自带 PowerShell 打包脚本，会先清理 `bin` / `obj` / `publish`，再发布为单文件 exe（约 35 MB，不内置 .NET 运行时）：

```powershell
.\build.ps1
```

产物位于 `publish\NotchPeninsula.exe`，目标机需自备 .NET 10 Desktop Runtime。仅清理不打包可执行 `.\build.ps1 -Clean`。

### 运行说明

运行发布产物中的可执行文件即可。该程序默认在后台托盘运行，右键托盘图标即可打开设置窗口。

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
