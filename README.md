<div align="center">

<img src="./NPS_NotchPeninsula-logo.ico" alt="NotchPeninsula" width="180" />

<h1>NotchPeninsula</h1>
<p>专为 Windows 而生的刘海屏/灵动岛组件，极致内存占用与性能</p>

<img alt="Windows" src="https://img.shields.io/badge/Windows-10%2F11-0078D6?logo=windows&logoColor=white" />
<img alt=".NET 10" src="https://img.shields.io/badge/.NET-10.0-512BD4?logo=dotnet&logoColor=white" />
<img alt="C#" src="https://img.shields.io/badge/C%23-9.0%2B-239120?logo=csharp&logoColor=white" />
<img alt="NAudio" src="https://img.shields.io/badge/NAudio-2.2-5C2D91" />
<img alt="SkiaSharp" src="https://img.shields.io/badge/SkiaSharp-2.88-8A2BE2" />
<img alt="Version" src="https://img.shields.io/badge/version-1.7.0-blue" />
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
- Windows Toast 通知气泡
- 剪贴板链接识别与一键打开
- 可安装第三方插件扩展的自定义组件
- 可配置的自动隐藏、窗口置顶、鼠标穿透与自动更新检测
- 托盘菜单快捷控制

这种设计适合单屏、窄边框布局，以及希望保留桌面空间同时拥有交互反馈的使用场景。

## 功能特性

### 1. 媒体控制

- 自动识别当前系统媒体会话
- 支持常见平台：通用媒体、浏览器媒体、网易云音乐、QQ音乐、酷狗、Spotify、Apple Music、Echo Music、LX Music
- **浏览器媒体模式**：只接管浏览器（Chrome / Edge / Firefox / Brave / Opera / Vivaldi / QQ浏览器 / 360 / 搜狗等）的 SMTC 会话，
  其他播放器的会话一律不接管；命中后自动清理网页标题后缀、不请求歌词
- **通用媒体 + 手动选择软件**：直接锁定指定进程的 SMTC 会话，**优先级高于一切自动判定**。
  被锁定的软件不再走浏览器 / 视频平台识别，一律按普通媒体源请求歌词，校验命中即正常显示。
  据此，Pake / Tauri 等基于 WebView2（进程名含 `msedgewebview2`）的套壳播放器、
  以及进程名不含浏览器关键字的浏览器，手动指定后都能正常出歌词
- 支持播放/暂停、上一曲、下一曲
- 展开媒体面板：**展开交互模式**下左键点击岛体（组合模式除外）。直接交互模式不提供展开入口
- 岛内右键：整个原生媒体控制器区域（标题 / 歌词 / 频谱 / 播放按钮 / 空白）一律**只打开设置窗口**
- 展开的媒体控制面板 / 插件详情页：**鼠标离开灵动岛即自动收起**（鼠标停在岛上时不会自己消失）。
  其中插件详情页会延迟约 0.9s 再收，避免刚展开时鼠标恰好落在新面板之外被瞬间收掉
- 支持自动识别当前系统媒体会话，使用歌曲信息网络获取歌词
- 支持卡拉 OK 动效与歌词延迟微调
- 歌词过长时岛体自适应加宽（**不截断**：文本按真实长度撑宽，只受岛体总长上限 `1920` 约束；
  岛体允许长到铺满屏幕，宁可溢出屏幕也不裁切歌词）

### 2. 实时音频可视化

- 通过 Wasapi Loopback 捕获系统输出音频
- 使用 Goertzel / 能量分析算法计算 5 组频段强度
- 并通过平滑动画渲染为可视化柱状图
- 在未播放媒体时仍可保留简洁的音量动态效果
- 自动跟随系统默认输出设备：切换扬声器 / 耳机 / HDMI 后立即改用新设备监听，无需重启软件

### 3. Windows 通知显示

- 监听系统 Toast 通知
- 在界面中展示最近通知的标题和正文
- 自动折叠和淡出，避免遮挡桌面内容
- 穿透模式下通知弹出时会**临时禁用穿透**，保证通知可见、可点击；通知结束后穿透自动恢复
- 可在**通用设置**中禁用通知展示；「通知开关」与「通知内容」合并为同一张卡片（主从两行）
- 通知内容支持「缩略 / 紧凑 / 完整」三种呈现方式

### 4. 时间日期与硬件占用

- 待机状态下显示时间日期，或选择留白
- 实时读取并平滑展示 CPU、内存占用
- **组合模式**：把时间日期、硬件占用、媒体控制器（含频谱）横向拼成一条完整信息栏

### 5. 剪贴板链接识别

- 事件驱动监听剪贴板（`WM_CLIPBOARDUPDATE`），稳态零 CPU / 零额外内存占用
- 复制整段链接后自动在岛体弹出面板，点击即可用默认浏览器打开
- 穿透模式下弹出链接面板时会**临时禁用穿透**，保证「打开」按钮可点击；面板关闭后穿透自动恢复
- 可在**通用设置**中关闭（该开关原在「交互设置」，2026-09-20 移入通用设置）

### 6. Just Solo歌词

- 支持通过 Just Solo LyricServer（`ws://127.0.0.1:47290`）实时获取带时间轴的歌词
- 协议细节见WIKI中的文档
- 支持使用服务端推送的实时频谱

### 7. 插件系统

- 插件以独立 dll 形式放在程序同级 `plugins` 目录，启动时自动加载
- 支持组件、详情页、定时刷新、提醒、自定义窗口五类扩展点
- 内置「插件中心」：导入 / 启用 / 禁用 / 热重载 / 移除 / 调整显示顺序
- 内置插件市场入口，可直接下载、分享第三方插件
- 开发文档见 [NPS_PluginsAPI.md](./NPS_PluginsAPI.md)

### 8. 外观与个性化

- 主题模式（深色 / 浅色 / 跟随系统）与背景不透明度调节
- 多种刘海样式与底部圆角自定义
- 待机 / 媒体 / 通知各状态尺寸与全局 DPI 缩放可调
- 灵动岛字体可整体切换为自定义字体，缺字自动回退
- **窗口置顶**与**鼠标穿透**模式
- 多显示器环境下可指定目标显示器

### 9. 自动隐藏与交互体验

- 支持在无媒体状态下自动隐藏到顶部
- 鼠标悬停时恢复展示
- **两个附属开关（二选一，默认都关闭）**——都用来放宽「什么情况下允许隐藏」的条件：
  - **暂停播放后自动隐藏**：媒体会话仍在但处于**暂停 / 停止**状态时，也把岛体藏起来
  - **全屏自动隐藏**：检测到**全屏应用**（全屏视频、全屏游戏，含独占模式 D3D、演示文稿模式）时
    **无条件让位**，即使音乐正在播放也隐藏
  - 两者只能开一个（开启一个会自动关闭另一个）；可用前提是已开启自动隐藏且未开穿透模式，
    否则置灰；关闭自动隐藏时它们会一并关闭
  - 全屏检测用系统接口 `SHQueryUserNotificationState` 一次调用完成，并做 0.8s 节流；
    **未开启该开关时不会产生任何系统调用**
- **穿透模式下自动隐藏强制失效**：设置里的开关会置灰并显示为关闭，岛体不会真的躲起来；
  关掉穿透模式后，原先的自动隐藏设置自动恢复
- 通知弹窗、剪贴板链接面板、插件详情页展开期间**一律不隐藏**，与自动隐藏开关无关
  —— 包括全屏状态下：消息来了岛体照样弹出来，不会漏通知
- 点击交互区可控制播放、上/下一曲、音量等
- 通过托盘菜单快速调出设置窗口

## 使用方式

### 启动

在 Windows 上直接运行编译后的程序即可。程序启动后会驻留到托盘区，界面默认显示在屏幕顶部中央。

### 设置入口

右键托盘图标可看见菜单：

- 打开设置
- 开机自启
- 退出

在设置窗口中，按左侧导航分类配置：

- **背景材质**：设置窗口默认尝试启用原生系统背景材质（Windows 11 为云母，Windows 10 为亚克力）；若系统不支持或调用失败，自动回退到原有暗色材质。
- **个性化中心**：主题、背景不透明度、刘海样式与圆角、各状态尺寸、DPI 缩放
- **通用设置**：开机自启、窗口置顶、消息通知（通知开关与内容模式合并在同一张卡片）、
  剪贴板链接检测、灵动岛字体
- **显示设置**：待机内容（时间日期 / 空白）、目标显示器、组合模式
- **媒体设置**：媒体控制、目标音乐平台、歌词与卡拉 OK、歌词延迟
- **交互设置**：自动隐藏（含「暂停播放后自动隐藏」「全屏自动隐藏」两个互斥附属开关）、
  媒体交互方式、鼠标穿透
- **插件中心**：导入 / 启用 / 禁用 / 热重载 / 移除插件、打开插件目录、进入插件市场
- **关于软件**：版本信息与相关链接

### 权限要求

由于项目使用了 Windows 通知监听能力，首次运行时可能需要允许此应用访问通知：

- 设置 → 隐私和安全性 → 通知
- 打开对应权限开关

如果关闭了通知权限，Toast 监听将无法正常显示消息。

## 项目结构

```text
NotchPeninsula/
├── Core/                       # 程序骨架与系统底层
│   ├── Program.cs              # 程序入口，单例启动与配置读写
│   ├── NotchWindow.cs          # 主窗口逻辑、动画、托盘、消息队列调度
│   ├── Win32.cs                # Win32 API 封装
│   ├── SystemSettingManager.cs # 系统音量控制
│   ├── UpdateManager.cs        # GitHub 版本检测与更新弹窗
│   └── Logger.cs               # 日志
├── Render/                     # 渲染与字体
│   ├── Renderer.cs             # 岛体渲染主流程：尺寸/主题/模式参数 + Draw() 主流程
│   ├── Renderer.Layout.cs      # 岛体内容布局：组合模式混排 / 非组合模式原生布局 / 穿透唤醒按钮
│   ├── Renderer.Plugin.cs      # 插件行绘制与详情页（命中、预算、顺序表）
│   ├── Renderer.Toast.cs       # 通知 Toast 与剪贴板面板
│   ├── Renderer.Media.cs       # 媒体/歌词/卡拉OK/时间轴/硬件统计
│   ├── Renderer.Paint.cs       # 画笔池、字体、矢量路径与图标资源
│   ├── FontConfig.cs           # 灵动岛字体统一配置中心
│   └── LyricsFont.cs           # 歌词字体
├── Media/                      # 媒体、音频与歌词
│   ├── MediaController.cs      # 媒体会话识别、歌词与卡拉 OK
│   ├── MediaLogoProvider.cs    # 媒体平台站标与 LOGO 管理
│   ├── JustSoloLyricClient.cs  # Just Solo LyricServer 歌词客户端
│   ├── AudioAnalyzer.cs        # WASAPI Loopback 音频频谱分析
│   └── Audio.cs                # NAudio 音频底层适配
├── UI/                         # 自绘窗口
│   ├── ConsoleWindow.cs        # 设置窗口：状态字段、构造、窗口生命周期、消息泵、渲染主流程
│   ├── ConsoleWindow.Render.cs # 设置窗口绘制：侧边栏、各页签、下拉浮层与控件绘制
│   ├── ConsoleWindow.WndProc.cs# 设置窗口悬停命中（整窗热区）
│   ├── ConsoleWindow.Click.cs  # 设置窗口点击分派（顺序敏感的 if/else 链）
│   ├── ConsoleWindow.Backdrop.cs # 亚克力/云母材质与窗口壳（材质窗、圆角、最小化）
│   ├── ConsoleWindow.Plugin.cs # 插件中心与设置项辅助逻辑
│   ├── ConsoleWindow.Settings.cs # 尺寸/通知内容/字体等设置项的读写
│   ├── ConsoleWindow.Paint.cs  # 设置窗口画笔池
│   └── TrayMenuWindow.cs       # 托盘菜单
├── Notify/                     # 消息通知通道
│   ├── Toast.cs                # 系统 Toast 监听 + 本地 HTTP 推送接口
│   ├── ToastIconProvider.cs    # 通知图标解析（内置别名 / 链接 / base64 / 本地路径）
│   ├── ClipboardMonitor.cs     # 剪贴板链接监听
│   └── AppActivator.cs         # 通过 AUMID 激活应用
├── Plugin/                     # 插件系统（API、宿主、加载器、管理、窗口、布局）
├── data/image/                 # 平台 logo / 图标资源
├── NPS_PluginsAPI.md           # 插件开发文档
├── build.ps1                   # 一键发布为单文件 exe
├── NPS_NotchPeninsula-logo.ico # 应用图标
├── NotchPeninsula.csproj       # .NET 项目配置
├── LICENSE                     # Apache 2.0
└── README.md                   # 项目说明
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

产物位于 `publish\NotchPeninsula.exe`，目标机需自备 .NET 10 Desktop Runtime。仅清理不打包可执行 `\build.ps1 -Clean`。

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
