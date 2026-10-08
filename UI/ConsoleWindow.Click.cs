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
        private void OnLeftButtonDown(IntPtr hwnd, int clickY)
        {
            if (_hotkeyRecordingIndex >= 0 && _hoveredHotkeyRow != _hotkeyRecordingIndex)
                CancelHotkeyRecording();

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
            else if (_selectedTab == 1 && _hoveredDisplayModeIndex != -1)
            {
                bool wantStandby = _hoveredDisplayModeIndex == 0;
                if (Renderer.StandbyActive != wantStandby)
                {
                    Renderer.StandbyActive = wantStandby;
                    Program.SaveSetting("StandbyActive", wantStandby ? 1 : 0);
                    Render();
                }
            }
            else if (_selectedTab == 1 && _hoveredStandbySceneIndex != -1)
            {
                Renderer.StandbyScene = _hoveredStandbySceneIndex;
                Program.SaveSetting("StandbyScene", Renderer.StandbyScene);
                Render();
            }
            else if (_selectedTab == 1 && _standbyToggleHovered)
            {
                Renderer.StandbyToggleByDoubleClick = !Renderer.StandbyToggleByDoubleClick;
                Program.SaveSetting("StandbyToggleByDoubleClick",
                    Renderer.StandbyToggleByDoubleClick ? 1 : 0);
                Render();
            }
            else if (_selectedTab == 1 && _pageScrollbarHovered)
            {
                float pageMax = GetDisplayPageMaxScroll();
                GetPageScrollbarLayout(out float top, out float trackH);
                float contentH = TITLE_BAR_HEIGHT + DISPLAY_CARD_Y + DISPLAY_CARD_H + 20f;
                float thumbH = Math.Max(24f, trackH * HEIGHT / contentH);
                float travel = Math.Max(1f, trackH - thumbH);
                float ratio = Math.Clamp((clickY - top - thumbH / 2f) / travel, 0f, 1f);
                _displayPageScroll = ratio * pageMax;
                NotifyScrolled();   // 滚动条显形（停手后自动淡出）
                SyncHoverFromCursor();
                Render();
            }
            else if (_selectedTab == 1 && _listScrollbarHovered)
            {
                GetDisplayListLayout(out int visibleRows, out int maxFirstRow);
                if (maxFirstRow > 0 && visibleRows > 0)
                {
                    GetListScrollbarLayout(out float top, out float trackH);
                    float thumbH = Math.Max(18f,
                        trackH * visibleRows / PluginManager.Instance.DisplayItems.Count);
                    float travel = Math.Max(1f, trackH - thumbH);
                    float ratio = Math.Clamp((clickY - top - thumbH / 2f) / travel, 0f, 1f);
                    _displayScroll = (int)Math.Round(ratio * maxFirstRow);
                }
                NotifyScrolled();   // 滚动条显形（停手后自动淡出）
                SyncHoverFromCursor();
                Render();
            }
            else if (_hoveredTab == 0 && _selectedTab != 0) { _selectedTab = 0; CloseMarketDialog(); _marketCategoryOpen = false; _marketSearchFocused = false; CloseAllDropdowns(); Render(); }
            else if (_hoveredTab == 1 && _selectedTab != 1) { _selectedTab = 1; CloseMarketDialog(); _marketCategoryOpen = false; _marketSearchFocused = false; CloseAllDropdowns(); Render(); }
            else if (_hoveredTab == 2 && _selectedTab != 2) { _selectedTab = 2; CloseMarketDialog(); _marketCategoryOpen = false; _marketSearchFocused = false; CloseAllDropdowns(); Render(); }
            else if (_hoveredTab == 3 && _selectedTab != 3) { _selectedTab = 3; CloseMarketDialog(); _marketCategoryOpen = false; _marketSearchFocused = false; CloseAllDropdowns(); Render(); }
            else if (_hoveredTab == 4 && _selectedTab != 4) { _selectedTab = 4; CloseMarketDialog(); _marketCategoryOpen = false; _marketSearchFocused = false; CloseAllDropdowns(); Render(); }
            else if (_hoveredTab == 5 && _selectedTab != 5) { _selectedTab = 5; CloseMarketDialog(); _marketCategoryOpen = false; _marketSearchFocused = false; CloseAllDropdowns(); Render(); }
            else if (_hoveredTab == 6 && _selectedTab != 6)
            {
                _selectedTab = 6; _dropdownOpen = false; CloseMarketDialog();
                _marketCategoryOpen = false; _marketSearchFocused = false;
                RefreshPluginView();
                Render();
            }
            else if (_hoveredTab == 7 && _selectedTab != 7)
            {
                _selectedTab = 7; _dropdownOpen = false; CloseMarketDialog();
                _marketCategoryOpen = false; _marketSearchFocused = false;
                if (_marketError.Length > 0 && !_marketFetching) _marketTriedFetch = false;   // 上次拉取失败：这次重试
                EnsureMarketData();
                Render();
            }
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
            // 消息提示音：开关 / 下拉 / 音量下拉 / 两个按钮
            else if (_soundToggleHovered)
            {
                ToastSoundConfig.IsEnabled = !ToastSoundConfig.IsEnabled;
                Program.SaveSetting("ToastSoundEnabled", ToastSoundConfig.IsEnabled ? 1 : 0);
                if (!ToastSoundConfig.IsEnabled)
                {
                    ToastSoundPlayer.ClearQueue();
                    CloseAllDropdowns();
                }
                Render();
            }
            // 行 4 的四个控件都要求父开关「消息提示音」已打开
            else if (_toastSoundDropdownHovered && ToastSoundConfig.IsRowEnabled)
            {
                CloseAllDropdowns();
                ToastSoundConfig.RefreshBuiltins();
                _toastSoundDropdownOpen = true;
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

                UpdateValueString(updateIdx);

                if (updateIdx == 0) { Renderer.STANDBY_WIDTH = _customValues[0]; Program.SaveSetting("Custom_StandbyW", _customValues[0]); }
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
            else if (_selectedTab == 2 && _scanToggleHovered)
            {
                MediaController.IsLyricScanEnabled = !MediaController.IsLyricScanEnabled;
                Program.SaveSetting("LyricScanEnabled", MediaController.IsLyricScanEnabled ? 1 : 0);
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
            else if (_selectedTab == 2 && _hotkeyToggleHovered)
            {
                _hotkeyHint = "";
                MediaHotkeys.SetEnabled(!MediaHotkeys.IsEnabled);
                // 开启时若有键位被别的程序占着，把原因挂到卡片副标题上：
                if (MediaHotkeys.IsEnabled) _hotkeyHint = MediaHotkeys.LastError;
                Render();
            }
            else if (_selectedTab == 2 && _hoveredHotkeyRow >= 0)
            {
                _hotkeyHint = "";
                MediaHotkeys.SuspendRegistration();
                _hotkeyRecordingIndex = _hoveredHotkeyRow;
                if (_hwnd != IntPtr.Zero) Win32.SetFocus(_hwnd);
                Render();
            }
            else if (_autoHideToggleHovered)
            {
                // 总开关：只翻转自己，不改写下面三个模式的偏好。
                NotchWindow.IsAutoHideEnabled = !NotchWindow.IsAutoHideEnabled;
                Program.SaveSetting("AutoHide", NotchWindow.IsAutoHideEnabled ? 1 : 0);
                Render();
            }
            else if (_focusHideToggleHovered && NotchWindow.IsAutoHideEnabled)
            {
                // 需总开关已开启（穿透模式不影响）。
                NotchWindow.IsFocusAutoHideEnabled = !NotchWindow.IsFocusAutoHideEnabled;
                Program.SaveSetting("FocusAutoHide", NotchWindow.IsFocusAutoHideEnabled ? 1 : 0);
                Render();
            }
            else if (_pauseHideToggleHovered && NotchWindow.IsAutoHideEnabled)
            {
                NotchWindow.IsPauseAutoHideEnabled = !NotchWindow.IsPauseAutoHideEnabled;
                Program.SaveSetting("PauseAutoHide", NotchWindow.IsPauseAutoHideEnabled ? 1 : 0);
                Render();
            }
            else if (_fsHideToggleHovered && NotchWindow.IsAutoHideEnabled)
            {
                // 需总开关已开启（穿透模式不影响）。
                NotchWindow.IsFullscreenAutoHideEnabled = !NotchWindow.IsFullscreenAutoHideEnabled;
                Program.SaveSetting("FullscreenAutoHide", NotchWindow.IsFullscreenAutoHideEnabled ? 1 : 0);
                Render();
            }
            else if (_mediaExpToggleHovered)
            {
                Renderer.MediaInteractionMode = Renderer.MediaInteractionMode == 1 ? 0 : 1;
                if (Renderer.MediaInteractionMode == 0) NotchWindow.CloseMediaPanel();
                Program.SaveSetting("MediaInteractionMode", Renderer.MediaInteractionMode);
                Render();
            }
            else if (_appLaunchToggleHovered)
            {
                // 双击封面跳转开关：关掉后双击封面完全不消费、不做事；
                MediaController.IsAppLaunchEnabled = !MediaController.IsAppLaunchEnabled;
                Program.SaveSetting("MediaAppLaunchEnabled", MediaController.IsAppLaunchEnabled ? 1 : 0);
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
            // ---- 显示内容列表 ----
            else if (_selectedTab == 1 && !Renderer.StandbyActive
                && (_hoveredDisplayRow != -1 || _hoveredDisplayMoveUp != -1 || _hoveredDisplayMoveDown != -1))
            {
                var displayItems = PluginManager.Instance.DisplayItems;

                if (_hoveredDisplayRow != -1 && _hoveredDisplayRow < displayItems.Count)
                {
                    var item = displayItems[_hoveredDisplayRow];
                    PluginManager.Instance.SetDisplayed(item.Key, !item.IsShown);
                }
                else
                {
                    int rowIdx = _hoveredDisplayMoveUp != -1 ? _hoveredDisplayMoveUp : _hoveredDisplayMoveDown;
                    if (rowIdx >= 0 && rowIdx < displayItems.Count)
                    {
                        int delta = _hoveredDisplayMoveUp != -1 ? -1 : 1;
                        PluginManager.Instance.MoveDisplay(displayItems[rowIdx].Key, delta);
                    }
                }

                Render();
            }
            // ---- 我的插件交互（tab 6）----
            else if (_selectedTab == 6)
            {
                if (_marketDialog == MarketDialog.LoadFailed)
                {
                    if (_hoveredDialogClose || _hoveredDialogButton == 0) { CloseMarketDialog(true); }
                    else
                    {
                        bool inside = TryGetCursorClientPos(out int px, out int py)
                            && GetCurrentDialogRect().Contains(px, py);
                        if (!inside) CloseMarketDialog(true);
                    }
                }
                else if (_hoveredPluginAction != -1)
                {
                    switch (_hoveredPluginAction)
                    {
                        case 0: ImportPluginDll(); break;
                        case 1: PluginManager.Instance.OpenPluginsFolder(); break;
                        case 2:
                            //    省得用户自己找到左栏去切页签。
                            _selectedTab = 7;
                            _dropdownOpen = false;
                            CloseMarketDialog();
                            _marketCategoryOpen = false;
                            _marketSearchFocused = false;
                            if (_marketError.Length > 0 && !_marketFetching) _marketTriedFetch = false;
                            EnsureMarketData();
                            ResetPluginHover();
                            Render();
                            break;
                    }
                }
                else if (_hoveredPluginToggle != -1)
                {
                    var pe = GetPluginAt(_hoveredPluginToggle);
                    if (pe != null) { PluginManager.Instance.SetEnabled(pe, !pe.IsEnabled); ResetPluginHover(); RefreshPluginView(); Render(); }
                }
                else if (_hoveredPluginReload != -1)
                {
                    var pe = GetPluginAt(_hoveredPluginReload);
                    if (pe != null) { PluginManager.Instance.Reload(pe); ResetPluginHover(); RefreshPluginView(); Render(); }
                }
                else if (_hoveredPluginRemove != -1)
                {
                    var pe = GetPluginAt(_hoveredPluginRemove);
                    if (pe != null)
                    {
                        PluginManager.Instance.Remove(pe);
                        ResetPluginHover();
                        RefreshPluginView();
                        Render();
                    }
                }
            }
            // ---- 插件市场交互（tab 7）----
            else if (_selectedTab == 7)
            {
                if (_marketDialog == MarketDialog.LoadFailed)
                {
                    if (_hoveredDialogClose || _hoveredDialogButton == 0) { CloseMarketDialog(true); }
                    else
                    {
                        bool inside = TryGetCursorClientPos(out int px, out int py)
                            && GetCurrentDialogRect().Contains(px, py);
                        if (!inside) CloseMarketDialog(true);
                    }
                }
                else if (_marketDialogIndex != -1)
                {
                    var mp = GetMarketAt(_marketDialogIndex);
                    if (mp == null) { CloseMarketDialog(true); }
                    else if (_hoveredDialogClose)
                    {
                        CloseMarketDialog(true);
                    }
                    else if (_marketDialog == MarketDialog.Rate
                             && _rateMine <= 0 && !_rateLoading && _rateStars > 0)
                    {
                        SubmitRating(mp, _rateStars);
                    }
                    else
                    {
                        // 点弹窗内其它位置：不关。点弹窗外：关。
                        bool inside = TryGetCursorClientPos(out int px, out int py)
                            && GetCurrentDialogRect().Contains(px, py);
                        if (!inside) CloseMarketDialog(true);
                    }
                }
                else if (_marketCategoryOpen)
                {
                    if (_hoveredMarketCategoryIndex >= 0 && _hoveredMarketCategoryIndex < MarketCategories.Length)
                    {
                        string key = MarketCategories[_hoveredMarketCategoryIndex].Key;
                        _marketCategoryOpen = false;
                        if (!string.Equals(key, _marketCategoryKey, StringComparison.OrdinalIgnoreCase))
                        {
                            _marketCategoryKey = key;
                            RefreshMarketFilter();
                        }
                    }
                    else
                    {
                        _marketCategoryOpen = false;
                    }
                    Render();
                }
                else if (_hoveredMarketCategoryIndex == -2)   // 分类按钮
                {
                    _marketCategoryOpen = true;
                    _marketSearchFocused = false;
                    Render();
                }
                else if (_hoveredMarketRefresh)               // 刷新按钮：重新拉取市场数据
                {
                    if (_marketSearchFocused) _marketSearchFocused = false;
                    if (!_marketFetching)                     // 拉取中点了也不重入
                    {
                        _marketTriedFetch = false;            // 放开 EnsureMarketData 的幂等闸
                        _marketError = "";
                        _marketScroll = 0;
                        EnsureMarketData();
                    }
                    Render();
                }
                else if (_hoveredMarketChk)                   // 「只看已安装」复选框
                {
                    _marketOnlyInstalled = !_marketOnlyInstalled;
                    RefreshMarketFilter();
                    Render();
                }
                else if (IsInMarketSearchBox())               // 搜索框：聚焦，交给 WM_CHAR / IME 输入
                {
                    //    用户能看到光标却打不出字。
                    if (_hwnd != IntPtr.Zero) Win32.SetFocus(_hwnd);
                    if (TryGetCursorClientPos(out int sx, out _)) _marketSearchCaret = MarketSearchIndexAtX(sx);
                    _marketSearchSelAnchor = _marketSearchCaret;
                    _marketSearchViewFrozen = MarketSearchViewStart();
                    _marketSearchDragging = true;
                    _marketSearchFocused = true;
                    Render();
                }
                else if (_hoveredMarketInstall != -1)
                {
                    if (_marketSearchFocused) { _marketSearchFocused = false; Render(); }
                    var mp = GetMarketAt(_hoveredMarketInstall);
                    if (mp != null) StartMarketInstall(mp);
                }
                else if (_hoveredMarketUninstall != -1)
                {
                    // 不要一边弹确认、一边直接执行。
                    if (_marketSearchFocused) { _marketSearchFocused = false; }
                    var mp = GetMarketAt(_hoveredMarketUninstall);
                    if (mp != null) UninstallMarketPlugin(mp);   // 内部会刷新列表 + Render
                }
                else if (_hoveredMarketDetail != -1)
                {
                    if (_marketSearchFocused) { _marketSearchFocused = false; Render(); }
                    _marketDialog = MarketDialog.Detail;
                    _marketDialogIndex = _hoveredMarketDetail;
                    Render();
                }
                else
                {
                    // 点空白：退出搜索聚焦
                    if (_marketSearchFocused) { _marketSearchFocused = false; Render(); }
                }
            }
        }
    }
}
