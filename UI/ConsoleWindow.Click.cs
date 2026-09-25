using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using SkiaSharp;
using System.Diagnostics;
using NotchPeninsula.Plugins;

namespace NotchPeninsula
{
    public partial class ConsoleWindow
    {
        // 鼠标左键按下：整窗点击分派。
        //
        // ⚠️ 这是一条**顺序敏感**的 if / else if 链：先匹配到的分支执行，后面的不再看。
        //    越靠前的分支优先级越高（关闭按钮 > 最小化 > 标题栏拖拽 > 收下拉 > 切页签 > 卡片控件…）。
        //    调整任何分支的位置都等于改行为，所以整条链保持平铺，不要拆散。
        private void OnLeftButtonDown(IntPtr hwnd, int clickY)
        {

            if (_closeHovered)
            {
                if (_backdropHwnd != IntPtr.Zero)
                {
                    Win32.DestroyWindow(_backdropHwnd);
                    _backdropHwnd = IntPtr.Zero;
                }
                Win32.DestroyWindow(hwnd);
            }
            else if (_minHovered)
            {
                MinimizeToTaskbar();
            }
            else if (clickY <= TITLE_BAR_HEIGHT)
            {
                Win32.ReleaseCapture();
                Win32.SendMessage(hwnd, Win32.WM_NCLBUTTONDOWN, Win32.HTCAPTION, 0);
            }
            else if (_dropdownOpen && _hoveredDropdownIndex == -1)
            {
                CloseAllDropdowns(); Render(); // 点击菜单外部收起浮窗
            }
            else if (_monitorDropdownOpen && _hoveredMonitorDropdownIndex == -1) { CloseAllDropdowns(); Render(); }
            else if (_toastModeDropdownOpen && _hoveredToastModeIndex == -1) { CloseAllDropdowns(); Render(); }
            else if (_toastSoundDropdownOpen && _hoveredToastSoundIndex == -1) { CloseAllDropdowns(); Render(); }
            else if (_soundVolumeDropdownOpen && _hoveredSoundVolumeIndex == -1) { CloseAllDropdowns(); Render(); }
            else if (_matchModeDropdownOpen && _hoveredMatchModeIndex == -1) { CloseAllDropdowns(); Render(); }
            else if (_appDropdownOpen && _hoveredAppIndex == -1) { CloseAllDropdowns(); Render(); }
            else if (_selectedTab == 1 && _hoveredStyleIndex != -1)
            {
                Renderer.NotchStyle = _hoveredStyleIndex;
                Program.SaveSetting("NotchStyle", _hoveredStyleIndex);
                Render();
            }
            else if (_hoveredTab == 0 && _selectedTab != 0) { _selectedTab = 0; CloseAllDropdowns(); Render(); }
            else if (_hoveredTab == 1 && _selectedTab != 1) { _selectedTab = 1; CloseAllDropdowns(); Render(); }
            else if (_hoveredTab == 2 && _selectedTab != 2) { _selectedTab = 2; CloseAllDropdowns(); Render(); }
            else if (_hoveredTab == 3 && _selectedTab != 3) { _selectedTab = 3; CloseAllDropdowns(); Render(); }
            else if (_hoveredTab == 4 && _selectedTab != 4) { _selectedTab = 4; CloseAllDropdowns(); Render(); }
            else if (_hoveredTab == 5 && _selectedTab != 5) { _selectedTab = 5; CloseAllDropdowns(); Render(); }
            else if (_hoveredTab == 6 && _selectedTab != 6) { _selectedTab = 6; _dropdownOpen = false; RefreshPluginView(); Render(); }
            else if (_monitorDropdownHovered) { _monitorDropdownOpen = true; Render(); }
            else if (_monitorDropdownOpen && _hoveredMonitorDropdownIndex != -1)
            {
                Renderer.TargetMonitorIndex = _hoveredMonitorDropdownIndex;
                Program.SaveSetting("TargetMonitorIndex", Renderer.TargetMonitorIndex);
                _monitorDropdownOpen = false;
                Render();
            }
            else if (_toastModeDropdownHovered) { _toastModeDropdownOpen = true; Render(); }
            else if (_toastModeDropdownOpen && _hoveredToastModeIndex != -1)
            {
                ApplyToastContentMode(_hoveredToastModeIndex);
                _toastModeDropdownOpen = false;
                Render();
            }
            // 🎵 消息提示音：开关 / 下拉 / 音量下拉 / 两个按钮
            //    （必须排在剪贴板、字体等通用开关分支之前，否则会被后者抢先吃掉）
            else if (_soundToggleHovered)
            {
                ToastSoundConfig.IsEnabled = !ToastSoundConfig.IsEnabled;
                Program.SaveSetting("ToastSoundEnabled", ToastSoundConfig.IsEnabled ? 1 : 0);
                // 关闭时把还在排队的提示音清掉，避免「开关已经关了、耳朵里还在响」
                if (!ToastSoundConfig.IsEnabled)
                {
                    ToastSoundPlayer.ClearQueue();
                    // 🎵 第 4 行整行是这条开关的附属：关掉它，附属设置行立刻置灰、不吃指针，
                    //    所以这时还开着的浮层（提示音下拉 / 音量下拉）必须一起收掉，
                    //    否则会留下一个「盖在禁用区域上、却还能点」的浮窗。
                    CloseAllDropdowns();
                }
                Render();
            }
            // 🎵 行 4 的四个控件都要求父开关「消息提示音」已打开
            //    （判据与绘制侧置灰、命中侧不吃指针同源，见 ToastSoundConfig.IsRowEnabled）
            else if (_toastSoundDropdownHovered && ToastSoundConfig.IsRowEnabled)
            {
                CloseAllDropdowns();
                // 每次展开都重扫一遍目录：新丢进 data\sound 的文件不用重启就能看到。
                // RefreshBuiltins 内部会顺手把当前选择「按文件名身份」重新对齐一次
                // （增删 wav 造成的位置漂移 / 索引越界都在那里自愈），所以这里不用再补。
                ToastSoundConfig.RefreshBuiltins();
                _toastSoundDropdownOpen = true;
                // 展开时把滚动位置定到「当前选中项可见」处；**之后滚动完全交给滚轮**，
                // 绘制与命中都不再抢回选中项（那是「滚不动」的元凶）。
                ScrollToastSoundMenuToSelected();
                Render();
            }
            else if (_toastSoundDropdownOpen && _hoveredToastSoundIndex != -1)
            {
                ApplyToastSound(_hoveredToastSoundIndex);
                _toastSoundDropdownOpen = false;
                Render();
            }
            else if (_soundVolumeDropdownHovered && ToastSoundConfig.IsSourceReady)
            {
                CloseAllDropdowns();
                _soundVolumeDropdownOpen = true;
                Render();
            }
            else if (_soundVolumeDropdownOpen && _hoveredSoundVolumeIndex != -1)
            {
                ApplyToastSoundVolume(_hoveredSoundVolumeIndex);
                _soundVolumeDropdownOpen = false;
                Render();
            }
            else if (_soundPreviewHovered && ToastSoundConfig.IsSourceReady) { PreviewToastSound(); }
            else if (_soundResetHovered && ToastSoundConfig.IsSourceReady) { ResetToastSound(); }
            else if (_selectedTab == 2 && _matchModeDropdownHovered)
            {
                CloseAllDropdowns();
                _matchModeDropdownOpen = true;
                Render();
            }
            else if (_selectedTab == 2 && _matchModeDropdownOpen && _hoveredMatchModeIndex != -1)
            {
                MediaController.IsManualSessionMatch = _hoveredMatchModeIndex == 1;
                Program.SaveSetting("ManualSessionMatch", MediaController.IsManualSessionMatch ? 1 : 0);
                _matchModeDropdownOpen = false;
                _ = MediaController.Instance?.ForceRefresh();
                Render();
            }
            else if (_selectedTab == 2 && _appDropdownHovered)
            {
                // 展开时现取一次活动会话列表，保证「所有 SMTC 活动」是最新的
                _appOptions = MediaController.Instance?.GetAvailableAppIds() ?? Array.Empty<string>();
                CloseAllDropdowns();
                _appDropdownOpen = true;
                Render();
            }
            else if (_selectedTab == 2 && _appDropdownOpen && _hoveredAppIndex != -1 && _hoveredAppIndex < _appOptions.Length)
            {
                MediaController.ManualSessionAppId = _appOptions[_hoveredAppIndex];
                Program.SaveSetting("ManualSessionAppId", MediaController.ManualSessionAppId);
                _appDropdownOpen = false;
                _ = MediaController.Instance?.ForceRefresh();
                Render();
            }
            else if (_selectedTab == 5 && (_hoveredMinusIndex != -1 || _hoveredPlusIndex != -1 || _hoveredResetIndex != -1))
            {
                int updateIdx;
                if (_hoveredResetIndex != -1)
                {
                    updateIdx = _hoveredResetIndex;
                    float[] defaultVals = { 125f, 29f, 250f, 35f, 260f, 55f, 1.0f, 12f };
                    _customValues[updateIdx] = defaultVals[updateIdx];

                    // 完整模式重置消息通知尺寸时，同样拦截至完整模式最小限制，避免缩得放不下应用名
                    if (Renderer.IsToastFullMode)
                    {
                        if (updateIdx == 4 && _customValues[4] < Renderer.FULL_TOAST_MIN_WIDTH) _customValues[4] = Renderer.FULL_TOAST_MIN_WIDTH;
                        if (updateIdx == 5 && _customValues[5] < Renderer.FULL_TOAST_MIN_HEIGHT) _customValues[5] = Renderer.FULL_TOAST_MIN_HEIGHT;
                    }
                }
                else
                {
                    updateIdx = _hoveredMinusIndex != -1 ? _hoveredMinusIndex : _hoveredPlusIndex;
                    float delta = _hoveredPlusIndex != -1 ? (updateIdx == 6 ? 0.05f : 5f) : (updateIdx == 6 ? -0.05f : -5f);
                    if (updateIdx == 7)
                        _customValues[updateIdx] = Math.Clamp(_customValues[updateIdx] + delta, 0f, 28f);
                    else
                    {
                        float minLimit = updateIdx == 6 ? 0.5f : 20f;
                        // 完整模式下，消息通知最小尺寸必须能容纳应用名
                        if (Renderer.IsToastFullMode)
                        {
                            if (updateIdx == 4) minLimit = Renderer.FULL_TOAST_MIN_WIDTH;
                            if (updateIdx == 5) minLimit = Renderer.FULL_TOAST_MIN_HEIGHT;
                        }
                        _customValues[updateIdx] = Math.Max(minLimit, _customValues[updateIdx] + delta);
                    }
                }

                // 数值变动时才更新字符串缓存，避免渲染循环产生 GC 垃圾
                UpdateValueString(updateIdx);

                if (updateIdx == 0) { Renderer.STANDBY_WIDTH = _customValues[0]; Program.SaveSetting("Custom_StandbyW", _customValues[0]); }
                else if (updateIdx == 1) { Renderer.BASE_HEIGHT = _customValues[1]; Program.SaveSetting("Custom_BaseH", _customValues[1]); }
                else if (updateIdx == 2) { Renderer.MEDIA_WIDTH = _customValues[2]; Program.SaveSetting("Custom_MediaW", _customValues[2]); }
                else if (updateIdx == 3) { Renderer.MEDIA_HEIGHT = _customValues[3]; Program.SaveSetting("Custom_MediaH", _customValues[3]); }
                else if (updateIdx == 4) { Renderer.TOAST_WIDTH = _customValues[4]; Program.SaveSetting("Custom_ToastW", _customValues[4]); }
                else if (updateIdx == 5) { Renderer.TOAST_HEIGHT = _customValues[5]; Program.SaveSetting("Custom_ToastH", _customValues[5]); }
                else if (updateIdx == 6) { Renderer.GLOBAL_DPI = _customValues[6]; Program.SaveSetting("Custom_Dpi", _customValues[6]); }
                else if (updateIdx == 7) { Renderer.NOTCH_BOTTOM_RADIUS = _customValues[7]; Program.SaveSetting("Custom_NotchBottomR", _customValues[7]); }

                Render();
            }
            else if (_selectedTab == 5 && _hoveredThemeIndex != -1)
            {
                Renderer.ThemeMode = _hoveredThemeIndex;
                Renderer.ApplyThemeColors(); // 立即反转画笔颜色
                Program.SaveSetting("ThemeMode", _hoveredThemeIndex);
                Render(); // 刷新控制台UI
            }
            else if (_selectedTab == 5 && _hoveredOpacityIndex != -1)
            {
                Renderer.BgOpacityLevel = _hoveredOpacityIndex;
                Renderer.ApplyThemeColors(); // 立刻应用透明度
                Program.SaveSetting("BgOpacityLevel", _hoveredOpacityIndex); // 持久化保存
                Render(); // 刷新UI
            }
            else if (_selectedTab == 4 && _hoveredLinkIndex != -1)
            {
                string[] urls = [
                    "https://github.com/GEORGEWWWU/NotchPeninsula/releases", // 0: 检测更新
                    "https://github.com/GEORGEWWWU/NotchPeninsula",          // 1: 仓库地址
                    "https://georgewu.top/"                                  // 2: Ryen主页
                ];
                try
                {
                    // .NET 5+ 环境下，调用浏览器打开网页必须指定 UseShellExecute = true
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = urls[_hoveredLinkIndex],
                        UseShellExecute = true
                    });
                }
                catch { /* 防止没装浏览器的极端环境崩溃 */ }
            }
            else if (_toggleHovered)
            {
                bool newState = !_isAutoStartEnabled;
                NotchWindow.ToggleAutoStart(newState, false);
                _isAutoStartEnabled = newState;
                Render();
            }
            else if (_toastToggleHovered)
            {
                // 切换状态
                NotchWindow.IsToastEnabled = !NotchWindow.IsToastEnabled;
                // 保存设置
                Program.SaveSetting("ToastEnabled", NotchWindow.IsToastEnabled ? 1 : 0);
                Render();
            }
            else if (_topmostToggleHovered)
            {
                NotchWindow.IsTopmostEnabled = !NotchWindow.IsTopmostEnabled;
                Program.SaveSetting("TopmostEnabled", NotchWindow.IsTopmostEnabled ? 1 : 0);
                // 直接调用底层 API 热重载层级，无需重启和重建画布
                Win32.SetWindowPos(NotchWindow.InstanceHandle, NotchWindow.IsTopmostEnabled ? Win32.HWND_TOPMOST : Win32.HWND_NOTOPMOST, 0, 0, 0, 0, Win32.SWP_NOMOVE_NOSIZE);
                Render();
            }
            else if (_fontPickHovered)
            {
                PickCustomFont();
            }
            else if (_fontResetHovered)
            {
                ResetCustomFont();
            }
            else if (_mediaToggleHovered)
            {
                MediaController.IsMediaControlEnabled = !MediaController.IsMediaControlEnabled;
                // 保存媒体控制开关 (转换为0/1)
                Program.SaveSetting("MediaControl", MediaController.IsMediaControlEnabled ? 1 : 0);

                _ = MediaController.Instance?.ForceRefresh();
                Render();
            }
            else if (_selectedTab == 2 && _lyricToggleHovered)
            {
                MediaController.IsLyricsEnabled = !MediaController.IsLyricsEnabled;
                Program.SaveSetting("LyricsEnabled", MediaController.IsLyricsEnabled ? 1 : 0);
                Render();
            }
            else if (_selectedTab == 2 && _transToggleHovered)
            {
                MediaController.IsTranslationEnabled = !MediaController.IsTranslationEnabled;
                Program.SaveSetting("TranslationEnabled", MediaController.IsTranslationEnabled ? 1 : 0);
                Render();
            }
            else if (_selectedTab == 2 && _karaokeToggleHovered)
            {
                MediaController.IsKaraokeEnabled = !MediaController.IsKaraokeEnabled;
                Program.SaveSetting("KaraokeEnabled", MediaController.IsKaraokeEnabled ? 1 : 0);
                Render();
            }
            else if (_selectedTab == 2 && (_lyricMinusHovered || _lyricPlusHovered || _lyricResetHovered))
            {
                if (_lyricResetHovered) MediaController.LyricDelayOffset = 0f;
                else if (_lyricMinusHovered) MediaController.LyricDelayOffset -= 0.1f;
                else if (_lyricPlusHovered) MediaController.LyricDelayOffset += 0.1f;

                // 避免浮点数精度爆炸，固定为 1 位小数
                MediaController.LyricDelayOffset = (float)Math.Round(MediaController.LyricDelayOffset, 1);
                Program.SaveSetting("LyricDelayOffset", MediaController.LyricDelayOffset);
                Render();
            }
            else if (_autoHideToggleHovered && !Renderer.PassthroughModeEnabled)
            {
                NotchWindow.IsAutoHideEnabled = !NotchWindow.IsAutoHideEnabled;
                // 保存自动隐藏开关
                Program.SaveSetting("AutoHide", NotchWindow.IsAutoHideEnabled ? 1 : 0);

                // 🎵🖥 级联关闭：自动隐藏是父开关，关掉它时两个附属开关必须一起关，
                //    否则会留下「自动隐藏已关、岛体却因暂停/全屏而躲」的矛盾状态。
                //    （反向不级联：重新开启自动隐藏**不会**自动打开附属开关，保持「默认关闭」。）
                if (!NotchWindow.IsAutoHideEnabled)
                {
                    if (NotchWindow.IsPauseAutoHideEnabled)
                    {
                        NotchWindow.IsPauseAutoHideEnabled = false;
                        Program.SaveSetting("PauseAutoHide", 0);
                    }
                    if (NotchWindow.IsFullscreenAutoHideEnabled)
                    {
                        NotchWindow.IsFullscreenAutoHideEnabled = false;
                        Program.SaveSetting("FullscreenAutoHide", 0);
                    }
                }

                Render();
            }
            else if (_pauseHideToggleHovered && !Renderer.PassthroughModeEnabled && NotchWindow.IsAutoHideEnabled)
            {
                // 🎵 「暂停播放后自动隐藏」：可用前提是自动隐藏已开启且非穿透模式
                //    （与 hover 判定同源，双保险，防止状态不同步时被点到）
                NotchWindow.IsPauseAutoHideEnabled = !NotchWindow.IsPauseAutoHideEnabled;
                Program.SaveSetting("PauseAutoHide", NotchWindow.IsPauseAutoHideEnabled ? 1 : 0);

                // 🔒 互斥：两个附属开关都只是「放宽允许隐藏的条件」，同时开着会让
                //    「到底因为哪条才藏的」变得难以预期 —— 面板上只允许开一个。
                //    开启本项时顺手关掉另一项并写回注册表。
                if (NotchWindow.IsPauseAutoHideEnabled && NotchWindow.IsFullscreenAutoHideEnabled)
                {
                    NotchWindow.IsFullscreenAutoHideEnabled = false;
                    Program.SaveSetting("FullscreenAutoHide", 0);
                }

                Render();
            }
            else if (_fsHideToggleHovered && !Renderer.PassthroughModeEnabled && NotchWindow.IsAutoHideEnabled)
            {
                // 🖥 「全屏自动隐藏」：可用前提同上（自动隐藏已开启 + 非穿透模式）
                NotchWindow.IsFullscreenAutoHideEnabled = !NotchWindow.IsFullscreenAutoHideEnabled;
                Program.SaveSetting("FullscreenAutoHide", NotchWindow.IsFullscreenAutoHideEnabled ? 1 : 0);

                // 🔒 互斥：同上，开启本项时关掉另一项
                if (NotchWindow.IsFullscreenAutoHideEnabled && NotchWindow.IsPauseAutoHideEnabled)
                {
                    NotchWindow.IsPauseAutoHideEnabled = false;
                    Program.SaveSetting("PauseAutoHide", 0);
                }

                Render();
            }
            else if (_mediaExpToggleHovered)
            {
                Renderer.MediaInteractionMode = Renderer.MediaInteractionMode == 1 ? 0 : 1;
                // 关闭展开交互时强制收起媒体面板（面板开合统一走 NotchWindow 的那套管理）
                if (Renderer.MediaInteractionMode == 0) NotchWindow.CloseMediaPanel();
                Program.SaveSetting("MediaInteractionMode", Renderer.MediaInteractionMode);
                Render();
            }
            else if (_passToggleHovered)
            {
                Renderer.PassthroughModeEnabled = !Renderer.PassthroughModeEnabled;
                Program.SaveSetting("PassthroughMode", Renderer.PassthroughModeEnabled ? 1 : 0);
                Renderer.ApplyThemeColors(); // 立刻刷新基底色
                Render();
            }
            else if (_clipboardToggleHovered)
            {
                // 📋 剪贴板链接检测开关（关闭后正在展示的链接会由渲染循环立即收起）
                NotchWindow.IsClipboardEnabled = !NotchWindow.IsClipboardEnabled;
                Program.SaveSetting("ClipboardEnabled", NotchWindow.IsClipboardEnabled ? 1 : 0);
                Render();
            }
            else if (_dropdownHovered)
            {
                _dropdownOpen = true; Render();
            }
            else if (_dropdownOpen && _hoveredDropdownIndex != -1)
            {
                _selectedPlatformIndex = _hoveredDropdownIndex;
                MediaController.TargetPlatform = _platforms[_selectedPlatformIndex].Id;

                // 保存目标媒体平台字符串
                Program.SaveSetting("TargetPlatform", MediaController.TargetPlatform);

                _ = MediaController.Instance?.ForceRefresh();
                _dropdownOpen = false;
                Render();
            }
            // ===== 显示内容列表 =====
            // 顺序项同时含内置模块与插件，统一走 PluginManager 那张顺序表 ——
            // 所以这里不需要（也不该）再区分「原生模块」与「插件」两套逻辑。
            else if (_selectedTab == 1 && (_hoveredDisplayRow != -1 || _hoveredDisplayMoveUp != -1 || _hoveredDisplayMoveDown != -1))
            {
                var displayItems = PluginManager.Instance.DisplayItems;

                if (_hoveredDisplayRow != -1 && _hoveredDisplayRow < displayItems.Count)
                {
                    // 勾选 / 取消勾选：内置模块写 CompShow*，插件走启用 / 禁用
                    var item = displayItems[_hoveredDisplayRow];
                    PluginManager.Instance.SetDisplayed(item.Key, !item.IsShown);
                }
                else
                {
                    // 上 / 下移动（与相邻行换位，持久化后灵动岛下一帧即生效）
                    int rowIdx = _hoveredDisplayMoveUp != -1 ? _hoveredDisplayMoveUp : _hoveredDisplayMoveDown;
                    if (rowIdx >= 0 && rowIdx < displayItems.Count)
                    {
                        int delta = _hoveredDisplayMoveUp != -1 ? -1 : 1;
                        PluginManager.Instance.MoveDisplay(displayItems[rowIdx].Key, delta);
                    }
                }

                Render();
            }
            // ===== 插件中心交互 =====
            else if (_selectedTab == 6 && _hoveredPluginAction != -1)
            {
                switch (_hoveredPluginAction)
                {
                    case 0: ImportPluginDll(); break;
                    case 1: PluginManager.Instance.OpenPluginsFolder(); break;
                    case 2: PluginManager.Instance.OpenMarketplace(); break;
                }
            }
            else if (_selectedTab == 6 && _hoveredPluginToggle != -1)
            {
                var pe = GetPluginAt(_hoveredPluginToggle);
                if (pe != null) { PluginManager.Instance.SetEnabled(pe, !pe.IsEnabled); ResetPluginHover(); RefreshPluginView(); Render(); }
            }
            else if (_selectedTab == 6 && _hoveredPluginReload != -1)
            {
                var pe = GetPluginAt(_hoveredPluginReload);
                if (pe != null) { PluginManager.Instance.Reload(pe); ResetPluginHover(); RefreshPluginView(); Render(); }
            }
            else if (_selectedTab == 6 && _hoveredPluginRemove != -1)
            {
                var pe = GetPluginAt(_hoveredPluginRemove);
                if (pe != null) { PluginManager.Instance.Remove(pe); ResetPluginHover(); RefreshPluginView(); Render(); }
            }
            // 注：插件位置的 ← / → 已于 2026-09-25 移除，排序统一走「显示设置 → 显示内容」。
        }
    }
}
