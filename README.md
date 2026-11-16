<div align="center">
  <img src="./NPS_NotchPeninsula-logo.ico" alt="" width="96" />
  <h1>NotchPeninsula</h1>
  <p>Windows 桌面上的刘海与灵动岛，把媒体、歌词、通知和常用组件集中在屏幕顶部。</p>
  <p>
    <a href="https://nps.georgewu.top/">官方网站</a> &nbsp;|&nbsp;
    <a href="https://github.com/GEORGEWWWU/NotchPeninsula/wiki">进阶玩法</a> &nbsp;|&nbsp;
    <a href="https://github.com/GEORGEWWWU/NotchPeninsula/releases/latest">下载地址</a> &nbsp;|&nbsp;
    想获取最新测试版？加入 <a href="https://qm.qq.com/cgi-bin/qm/qr?k=i70z7rbl-VWpejQugvlXeARDUjwP7sIW&amp;jump_from=webapi&amp;authKey=b6Pj6zLuuCINDhafPJRttePdy3D45vvtWzcZ109LWoWYXkcKo8bNWI7fMhr+yV87">QQ交流群：1080730621</a>
  </p>
</div>

<img src="https://github.com/user-attachments/assets/88150409-d532-486b-8b92-853aebab1165" alt="NotchPeninsula 桌面组件展示" width="100%" />

## 能做什么

- **媒体与歌词** · 控制系统媒体播放，查看逐字歌词、译文和实时音频频谱。
- **通知与实况窗** · 在岛内查看 Windows 通知，用实况窗展示媒体或插件进度。
- **桌面个性化** · 切换刘海或灵动岛，选择亚克力或液态玻璃，设置待机、自动隐藏、快捷键和多显示器。
- **插件扩展** · 从内置市场安装和更新插件，也可导入 DLL 或 ZIP。

<img src="https://github.com/user-attachments/assets/fce46aa3-3abc-47f0-937e-f0b092c32807" alt="液态玻璃与实况窗" width="100%" />

<img src="https://github.com/user-attachments/assets/b80857fa-58a5-4379-8f1c-cf75db704ab1" alt="刘海与灵动岛样式展示" width="100%" />

## 开始使用

1. 在 [下载页](https://github.com/GEORGEWWWU/NotchPeninsula/releases/latest) 获取 Windows x64 版本。支持 Windows 10 / 11，需安装 .NET 10 Desktop Runtime。
2. 双击 exe，按首次启动引导选择外观、显示器和开机自启设置。
3. 右键托盘图标打开设置。可在「个性化中心」更换材质，在「显示设置」调整岛内内容，在「插件市场」安装扩展。

## 开发

在 Windows 上安装 .NET 10 SDK。用 Visual Studio 打开 `NotchPeninsula.slnx`，按 F5 调试。运行 `.uild.ps1` 打包，产物位于 `publishNotchPeninsula.exe`。

查阅 [插件开发文档](./NPS_PluginsAPI.md) 或 [Apache 2.0 许可证](./LICENSE)。

### 原生亚克力维护约束

- Windows 11 选中「亚克力」时，普通发布和 F5 调试均使用 Windows Composition `CreateHostBackdropBrush`。`build.ps1` 固定传 `-p:NativeAcrylicPreview=true`，让 exe 双击时直接选中原生亚克力，效果等同于启动时传 `--native-acrylic`。
- 保留刘海和灵动岛的轮廓裁切。**禁止用 GDI、DXGI 或其他抓屏方式替换原生背板，也禁止在原生宿主或裁切失败时回退到抓屏。** 失败时只保留色调并记录异常。Windows 10 和液态玻璃沿用各自的兼容路径。
- 排查时核对实际运行的 exe、启动参数和同一次运行的日志。「已建立 Windows Composition 背板宿主」表示宿主已建立。「[亚克力/原分辨率] 抓屏 ok」表示本次亚克力绘制走了旧抓屏背板。
