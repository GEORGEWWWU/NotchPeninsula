<div align="center">

<img src="./NPS_NotchPeninsula-logo.ico" alt="NotchPeninsula" width="180" />

<h1>NotchPeninsula</h1>
<p>专为 Windows 而生的灵动岛式媒体控制与通知增强工具</p>

<img alt="Windows" src="https://img.shields.io/badge/Windows-10%2F11-0078D6?logo=windows&logoColor=white" />
<img alt=".NET 10" src="https://img.shields.io/badge/.NET-10.0-512BD4?logo=dotnet&logoColor=white" />
<img alt="C#" src="https://img.shields.io/badge/C%23-9.0%2B-239120?logo=csharp&logoColor=white" />
<img alt="SkiaSharp" src="https://img.shields.io/badge/SkiaSharp-2.88-8A2BE2" />
<img alt="License" src="https://img.shields.io/badge/License-Apache%202.0-green.svg" />

<p>
  <a href="#-项目概览">项目概览</a> &nbsp; | &nbsp;
  <a href="#-功能特性">功能特性</a> &nbsp; | &nbsp;
  <a href="#-使用方式">使用方式</a> &nbsp; | &nbsp;
  <a href="#-构建与运行">构建与运行</a> &nbsp; | &nbsp;
  <a href="https://qm.qq.com/cgi-bin/qm/qr?k=i70z7rbl-VWpejQugvlXeARDUjwP7sIW&jump_from=webapi&authKey=b6Pj6zLuuCINDhafPJRttePdy3D45vvtWzcZ109LWoWYXkcKo8bNWI7fMhr+yV87" target="_blank">交流群 1080730621</a>
</p>

</div>

## 项目概览

NotchPeninsula 是一个面向 Win 10/11 的“刘海屏”风格桌面小组件，采用 Win32 + SkiaSharp + NAudio 的组合实现，核心目标是把系统媒体状态、通知消息和音频可视化整合到屏幕顶部的极窄悬浮区域中。

它会在桌面顶部生成一个轻量透明窗口，实时展示：

- 当前系统媒体播放状态（标题、艺术家、播放/暂停）
- 多平台音乐应用的媒体来源识别
- 实时音频频谱柱状图
- Windows Toast 通知气泡
- 可配置的自动隐藏与开机自启
- 托盘菜单快捷控制

这种设计适合单屏、窄边框布局，以及希望保留桌面空间同时拥有交互反馈的使用场景。

## 功能特性

### 1. 灵动岛式媒体控制

- 自动识别当前系统媒体会话
- 支持常见平台：通用媒体、网易云音乐、QQ音乐、酷狗、Spotify、Apple Music、Echo Music、LX Music
- 提供播放/暂停、上一曲、下一曲交互
- 允许无缝切换目标媒体来源

### 2. 实时音频可视化

- 通过 Wasapi Loopback 捕获系统输出音频
- 使用 Goertzel / 能量分析算法计算 5 组频段强度
- 并通过平滑动画渲染为可视化柱状图
- 在未播放媒体时仍可保留简洁的音量动态效果

### 3. Windows 通知显示

- 监听系统 Toast 通知
- 在界面中展示最近通知的标题和正文
- 自动折叠和淡出，避免遮挡桌面内容
- 可在设置中禁用通知展示

### 4. 自动隐藏与交互体验

- 支持在无媒体状态下自动隐藏到顶部
- 鼠标悬停时恢复展示
- 点击交互区可控制播放、上/下一曲、音量等
- 通过托盘菜单快速调出设置窗口

### 5. 系统级配置

- 开机自启
- 系统消息通知开关
- 媒体控制开关
- 自动隐藏开关
- 目标媒体平台配置
- 配置保存在 Windows 注册表中，开机后保持状态

## 使用方式

### 启动

在 Windows 上直接运行编译后的程序即可。程序启动后会驻留到托盘区，界面默认显示在屏幕顶部中央。

### 设置入口

右键托盘图标可看见菜单：

- 打开设置
- 开机自启
- 退出

在设置窗口中，可配置：

- 媒体控制开关
- 目标音乐平台
- 自动隐藏
- 系统通知显示
- 开机自启

### 权限要求

由于项目使用了 Windows 通知监听能力，首次运行时可能需要允许此应用访问通知：

- 设置 → 隐私和安全性 → 通知
- 打开对应权限开关

如果关闭了通知权限，Toast 监听将无法正常显示消息。

## 项目结构

```text
NotchPeninsula/
├── Program.cs                 # 程序入口，单例启动与配置加载
├── NotchWindow.cs             # 主要窗口逻辑、动画、托盘、交互
├── Renderer.cs                # UI 渲染与材质绘制
├── MediaController.cs         # 媒体会话识别与属性更新
├── AudioAnalyzer.cs           # 音频频谱分析
├── SystemSettingManager.cs    # 系统音量控制
├── ConsoleWindow.cs           # 设置窗口
├── toast.cs                   # Toast 通知监听
├── MediaLogoProvider.cs       # 媒体平台站标与 LOGO 管理
├── Win32.cs                   # Win32 API 封装
├── Logger.cs                  # 日志
├── data/image/                # 平台 logo / 图标资源
├── NPS_NotchPeninsula-logo.ico # 应用图标
├── NotchPeninsula.csproj      # .NET 项目配置
├── LICENSE                    # Apache 2.0
└── README.md                  # 项目说明
```

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

### 运行说明

运行发布产物中的可执行文件即可。该程序默认在后台托盘运行，右键托盘图标即可打开设置窗口。

## 兼容性与说明

- 本项目是 Windows 桌面程序，不适合在 macOS/Linux 平台直接运行。
- 使用了 Windows 通知管理 API，因此需要在 Windows 10/11 环境中执行。
- 若你的系统未安装相应音频设备或输出设备，音频频谱可能会显示为空白或静态效果。

## 许可协议

本项目采用 Apache License 2.0 开源协议。详情见 [LICENSE](./LICENSE)。

---

<div align="center">
  <p><strong>NotchPeninsula</strong> · 让 Windows 顶部栏也能拥有“灵动岛”的体验</p>
</div>
