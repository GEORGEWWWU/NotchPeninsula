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
        //    因此它是一个整体，不要再按页签拆开。
        private void OnMouseMove(int x, int y, bool dragging)
        {

            bool newMinHovered = x >= WIDTH - 92 && x < WIDTH - 46 && y <= TITLE_BAR_HEIGHT;
            bool newCloseHovered = x >= WIDTH - 46 && x <= WIDTH && y <= TITLE_BAR_HEIGHT;

            // Tab Hover 判定
            int newHoveredTab = -1;
            if (x >= 10 && x <= 170 && y >= TITLE_BAR_HEIGHT + 10 && y <= TITLE_BAR_HEIGHT + 46) newHoveredTab = 5;      // 1. 个性化中心
            else if (x >= 10 && x <= 170 && y >= TITLE_BAR_HEIGHT + 60 && y <= TITLE_BAR_HEIGHT + 96) newHoveredTab = 0; // 2. 通用设置
            else if (x >= 10 && x <= 170 && y >= TITLE_BAR_HEIGHT + 100 && y <= TITLE_BAR_HEIGHT + 136) newHoveredTab = 1; // 3. 显示设置
            else if (x >= 10 && x <= 170 && y >= TITLE_BAR_HEIGHT + 140 && y <= TITLE_BAR_HEIGHT + 176) newHoveredTab = 2; // 4. 媒体设置
            else if (x >= 10 && x <= 170 && y >= TITLE_BAR_HEIGHT + 180 && y <= TITLE_BAR_HEIGHT + 216) newHoveredTab = 3; // 5. 交互设置
            else if (x >= 10 && x <= 170 && y >= TITLE_BAR_HEIGHT + 230 && y <= TITLE_BAR_HEIGHT + 266) newHoveredTab = 6; // 6. 我的插件
            else if (x >= 10 && x <= 170 && y >= TITLE_BAR_HEIGHT + 270 && y <= TITLE_BAR_HEIGHT + 306) newHoveredTab = 7; // 7. 插件市场
            else if (x >= 10 && x <= 170 && y >= TITLE_BAR_HEIGHT + 320 && y <= TITLE_BAR_HEIGHT + 356) newHoveredTab = 4; // 8. 关于软件

            int newHoveredTheme = -1;
            int newHoveredOpacityIndex = -1;
            int newHoverMinus = -1, newHoverPlus = -1, newHoverReset = -1;
            if (_selectedTab == 5)
            {
                float themeSegY = TITLE_BAR_HEIGHT + THEME_SEG_Y;
                if (y >= themeSegY && y <= themeSegY + SEG_H
                    && x >= THEME_SEG_X && x <= THEME_SEG_X + THEME_SEG_W)
                    newHoveredTheme = Math.Min(2, (int)((x - THEME_SEG_X) / (THEME_SEG_W / 3f)));

                float sliderY = TITLE_BAR_HEIGHT + OPACITY_CARD_Y + OPACITY_SLIDER_DY;
                float sliderX = CONTENT_TEXT_X;
                float sliderW = (WIDTH - CONTENT_TEXT_RM) - CONTENT_TEXT_X;

                if (!Renderer.PassthroughModeEnabled)
                {
                    // 放宽 Y 轴的判定区域，提升拖拽时的手感，防止手抖断触
                    if (x >= sliderX - 20 && x <= sliderX + sliderW + 20 && y >= sliderY - 20 && y <= sliderY + 20)
                    {
                        newHoveredOpacityIndex = (int)Math.Round((x - sliderX) / (sliderW / 4));
                        if (newHoveredOpacityIndex < 0) newHoveredOpacityIndex = 0;
                        if (newHoveredOpacityIndex > 4) newHoveredOpacityIndex = 4;

                        if (dragging)
                        {
                            if (Renderer.BgOpacityLevel != newHoveredOpacityIndex)
                            {
                                Renderer.BgOpacityLevel = newHoveredOpacityIndex;
                                Renderer.ApplyThemeColors();
                                Program.SaveSetting("BgOpacityLevel", newHoveredOpacityIndex);
                                Render(); // 数据一旦跨越档位，立刻触发重绘，实现跟手滑动
                            }
                        }
                    }
                } 

                int newHintRow = -1;
                if (x >= CONTENT_L && x <= WIDTH - CONTENT_RM)
                {
                    float hy = GetBtnY(7);
                    if (y >= hy - 6 && y <= hy + 30) newHintRow = 7;
                }
                if (newHintRow != _hintRow)
                {
                    _hintRow = newHintRow;
                    StartDisplayHoverAnim();   // 与列表行底共用同一张 16ms 表，跑完自己停
                }

                for (int i = 0; i < 8; i++)
                {
                    // 无控件的项不吃指针（与绘制侧保持一致）：
                    if (i == 0 || i == 1 || i == 2 || i == 4) continue;

                    float btnY = GetBtnY(i);
                    float rightX = WIDTH - CONTENT_TEXT_RM; // 保持原有变量不动
                    if (x >= rightX - 175 && x <= rightX - 145 && y >= btnY && y <= btnY + 24) newHoverMinus = i;
                    if (x >= rightX - 80 && x <= rightX - 50 && y >= btnY && y <= btnY + 24) newHoverPlus = i;
                    if (x >= rightX - 40 && x <= rightX && y >= btnY && y <= btnY + 24) newHoverReset = i;
                }
            }

            int newHoveredStyleIndex = -1;
            int newHoveredDisplayModeIndex = -1;  // 显示模式：0 = 待机模式 / 1 = 普通模式
            int newHoveredStandbySceneIndex = -1; // 待机模式：三个场景选项（1 / 2 / 3）
            bool newStandbyToggleHovered = false; // 待机模式：「双击空白切换」开关
            bool newPageScrollbarHovered = false; // 显示设置：整页滚动条
            bool newListScrollbarHovered = false; // 显示设置：「显示内容」列表滚动条
            int newHoveredDisplayRow = -1;        // 「显示内容」列表：悬停在行本体上
            int newHoveredDisplayMoveUp = -1;     // 该行 ∧ 的悬停
            int newHoveredDisplayMoveDown = -1;   // 该行 ∨ 的悬停
            bool newToggleHovered = false;
            bool newToastToggleHovered = false;
            bool newTopmostToggleHovered = false;
            bool newMediaToggleHovered = false;
            bool newAutoHideToggleHovered = false;
            bool newFocusHideToggleHovered = false; // 「当焦点离开时自动隐藏岛」
            bool newPauseHideToggleHovered = false; // 「暂停播放后自动隐藏」
            bool newFsHideToggleHovered = false;    // 「全屏自动隐藏」
            bool newDropdownHovered = false;
            int newHoveredDropdownIndex = -1;
            bool newMatchModeDropdownHovered = false;
            int newHoveredMatchModeIndex = -1;
            bool newAppDropdownHovered = false;
            int newHoveredAppIndex = -1;
            bool newMediaExpToggleHovered = false;
            bool newAppLaunchToggleHovered = false; // 「双击媒体控制跳转应用」（媒体交互方式下面一格）
            bool newPassToggleHovered = false;
            bool newClipboardToggleHovered = false;
            bool newMonitorDropdownHovered = false;
            int newHoveredMonitorDropdownIndex = -1;
            bool newToastModeDropdownHovered = false;
            int newHoveredToastModeIndex = -1;
            bool newToastSoundDropdownHovered = false;
            int newHoveredToastSoundIndex = -1;
            bool newSoundVolumeDropdownHovered = false;
            int newHoveredSoundVolumeIndex = -1;
            bool newSoundToggleHovered = false;
            bool newSoundPreviewHovered = false;
            bool newSoundResetHovered = false;
            int newHoveredPluginAction = -1;
            int newHoveredPluginToggle = -1;
            int newHoveredPluginReload = -1;
            int newHoveredPluginRemove = -1;
            int newHoveredMarketInstall = -1;
            int newHoveredMarketUninstall = -1;
            int newHoveredMarketDetail = -1;
            int newHoveredMarketCategoryIndex = -1;
            bool newHoveredDialogClose = false;
            int newHoveredDialogButton = -1;
            double newRateStars = 0;
            bool newHoveredMarketChk = false;
            bool newMarketSearchHovered = false;
            bool newMarketRefreshHovered = false;
            bool newFontPickHovered = false;
            bool newFontResetHovered = false;

            if (_selectedTab == 0) // 通用设置
            {
                // 开机自启
                if (x >= WIDTH - 80 && x <= WIDTH - 30 && y >= TITLE_BAR_HEIGHT + 32 && y <= TITLE_BAR_HEIGHT + 52)
                    newToggleHovered = true;
                // 窗口置顶开关
                if (x >= WIDTH - 80 && x <= WIDTH - 30 && y >= TITLE_BAR_HEIGHT + 104 && y <= TITLE_BAR_HEIGHT + 124)
                    newTopmostToggleHovered = true;

                if (x >= WIDTH - 80 && x <= WIDTH - 30
                    && y >= TITLE_BAR_HEIGHT + TOAST_TOGGLE_ROW_Y && y <= TITLE_BAR_HEIGHT + TOAST_TOGGLE_ROW_Y + TOGGLE_TRACK_H)
                    newToastToggleHovered = true;

                // 系统消息通知卡第 2 行：消息通知内容下拉
                if (!_toastModeDropdownOpen
                    && x >= TOAST_MODE_CTRL_X && x <= TOAST_MODE_CTRL_X + TOAST_MODE_CTRL_W
                    && y >= TITLE_BAR_HEIGHT + TOAST_MODE_ROW_Y && y <= TITLE_BAR_HEIGHT + TOAST_MODE_ROW_Y + TOAST_MODE_ROW_H)
                    newToastModeDropdownHovered = true;
                if (_toastModeDropdownOpen)
                {
                    float menuTop = TITLE_BAR_HEIGHT + TOAST_MODE_ROW_Y + TOAST_MODE_ROW_H + 2;
                    if (x >= TOAST_MODE_CTRL_X && x <= TOAST_MODE_CTRL_X + TOAST_MODE_CTRL_W
                        && y >= menuTop && y < menuTop + _toastModeOptions.Length * 26)
                        newHoveredToastModeIndex = (int)((y - menuTop) / 26);
                }

                if (x >= WIDTH - 80 && x <= WIDTH - 30
                    && y >= TITLE_BAR_HEIGHT + SOUND_TOGGLE_ROW_Y && y <= TITLE_BAR_HEIGHT + SOUND_TOGGLE_ROW_Y + TOGGLE_TRACK_H)
                    newSoundToggleHovered = true;

                // 行 4 的「提示音设置」是父开关「消息提示音」的附属：
                bool soundRowEnabled = ToastSoundConfig.IsRowEnabled;
                bool soundReady = ToastSoundConfig.IsSourceReady;

                if (soundRowEnabled && !_toastSoundDropdownOpen
                    && x >= SOUND_CTRL_X && x <= SOUND_CTRL_X + SOUND_CTRL_W
                    && y >= TITLE_BAR_HEIGHT + SOUND_BOX_Y && y <= TITLE_BAR_HEIGHT + SOUND_BOX_Y + SOUND_ROW_H)
                    newToastSoundDropdownHovered = true;
                if (_toastSoundDropdownOpen)
                {
                    //    才会出现「滚两下就断」。
                    GetToastSoundMenuLayout(out float menuTop, out int visible, out int maxFirst);
                    int first = Math.Clamp(_dropdownScroll, 0, maxFirst);
                    if (x >= SOUND_CTRL_X && x <= SOUND_CTRL_X + SOUND_CTRL_W
                        && y >= menuTop && y < menuTop + visible * DROPDOWN_ROW_H)
                        newHoveredToastSoundIndex = first + (int)((y - menuTop) / DROPDOWN_ROW_H);
                }

                if (soundReady && !_soundVolumeDropdownOpen
                    && x >= SOUND_VOL_X && x <= SOUND_VOL_X + SOUND_VOL_W
                    && y >= TITLE_BAR_HEIGHT + SOUND_BOX_Y && y <= TITLE_BAR_HEIGHT + SOUND_BOX_Y + SOUND_ROW_H)
                    newSoundVolumeDropdownHovered = true;
                if (soundReady && _soundVolumeDropdownOpen)
                {
                    GetVolumeMenuLayout(out float menuTopV, out float menuBottom, out _);
                    if (x >= SOUND_VOL_X && x <= SOUND_VOL_X + SOUND_VOL_W
                        && y >= menuTopV && y < menuBottom)
                        newHoveredSoundVolumeIndex = (int)((y - menuTopV) / DROPDOWN_ROW_H);
                }

                if (soundReady && y >= TITLE_BAR_HEIGHT + SOUND_BTN_Y && y <= TITLE_BAR_HEIGHT + SOUND_BTN_Y + SOUND_BTN_H)
                {
                    if (x >= SOUND_PREVIEW_X && x <= SOUND_PREVIEW_X + SOUND_BTN_W) newSoundPreviewHovered = true;
                    if (x >= SOUND_RESET_X && x <= SOUND_RESET_X + SOUND_BTN_W) newSoundResetHovered = true;
                }

                if (x >= WIDTH - 80 && x <= WIDTH - 30
                    && y >= TITLE_BAR_HEIGHT + CLIPBOARD_CARD_Y + ROW_ANCHOR_Y - TOGGLE_TRACK_H / 2f
                    && y <= TITLE_BAR_HEIGHT + CLIPBOARD_CARD_Y + ROW_ANCHOR_Y + TOGGLE_TRACK_H / 2f)
                    newClipboardToggleHovered = true;

                // 切换灵动岛字体：[选择字体…] 与 [重置] 两个按钮
                if (y >= TITLE_BAR_HEIGHT + FONT_BTN_Y && y <= TITLE_BAR_HEIGHT + FONT_BTN_Y + FONT_BTN_H)
                {
                    if (x >= FONT_PICK_X && x <= FONT_PICK_X + FONT_PICK_W) newFontPickHovered = true;
                    if (x >= FONT_RESET_X && x <= FONT_RESET_X + FONT_RESET_W) newFontResetHovered = true;
                }
            }
            else if (_selectedTab == 1) // 显示设置
            {
                float page = -_displayPageScroll;

                float styleY = TITLE_BAR_HEIGHT + STYLE_OPT_Y + page;
                if (y >= styleY && y <= styleY + STYLE_OPT_H)
                {
                    if (x >= STYLE_OPT_X && x <= STYLE_OPT_X + STYLE_OPT_W) newHoveredStyleIndex = 0;
                    else
                    {
                        float styleX1 = STYLE_OPT_X + STYLE_OPT_W + STYLE_OPT_GAP;
                        if (x >= styleX1 && x <= styleX1 + STYLE_OPT_W) newHoveredStyleIndex = 1;
                    }
                }

                float mdY = TITLE_BAR_HEIGHT + MONITOR_ROW_Y + (ROW_H - MONITOR_DD_H) / 2f + page;
                float mdX = WIDTH - CONTENT_RM - MONITOR_DD_W;
                if (!_monitorDropdownOpen && x >= mdX && x <= WIDTH - CONTENT_RM && y >= mdY && y <= mdY + MONITOR_DD_H)
                    newMonitorDropdownHovered = true;

                if (_monitorDropdownOpen)
                {
                    float listY = mdY + MONITOR_DD_H + 2f;
                    if (x >= mdX && x <= WIDTH - CONTENT_RM && y >= listY && y < listY + _monitorOptions.Length * 26)
                        newHoveredMonitorDropdownIndex = (int)((y - listY) / 26);
                }

                float modeSegY = TITLE_BAR_HEIGHT + MODE_ROW_Y + (ROW_H - SEG_H) / 2f + page;
                if (y >= modeSegY && y <= modeSegY + SEG_H && x >= MODE_SEG_X && x <= MODE_SEG_X + MODE_SEG_W)
                    newHoveredDisplayModeIndex = Math.Min(1, (int)((x - MODE_SEG_X) / (MODE_SEG_W / 2f)));

                float sceneSegY = TITLE_BAR_HEIGHT + SCENE_ROW_Y + (ROW_H - SEG_H) / 2f + page;
                if (y >= sceneSegY && y <= sceneSegY + SEG_H && x >= SCENE_SEG_X && x <= SCENE_SEG_X + SCENE_SEG_W)
                    newHoveredStandbySceneIndex = Math.Min(2, (int)((x - SCENE_SEG_X) / (SCENE_SEG_W / 3f))) + 1;

                float modeToggleCy = TITLE_BAR_HEIGHT + TOGGLE_ROW_Y + page + ROW_ANCHOR_Y;
                if (x >= WIDTH - 80 && x <= WIDTH - 30
                    && y >= modeToggleCy - TOGGLE_TRACK_H / 2f && y <= modeToggleCy + TOGGLE_TRACK_H / 2f)
                    newStandbyToggleHovered = true;

                GetDisplayListLayout(out int displayVisibleRows, out int displayMaxFirstRow);
                _displayScroll = Math.Clamp(_displayScroll, 0, displayMaxFirstRow);
                float displayRowTop = TITLE_BAR_HEIGHT + DISPLAY_CARD_Y + DISPLAY_FIRST_ROW_Y + page;
                float displayRowBottom = TITLE_BAR_HEIGHT + DISPLAY_CARD_Y + DISPLAY_CARD_H + page;
                if (!Renderer.StandbyActive
                    && x >= DISPLAY_ITEM_L && x <= DISPLAY_ITEM_R && y >= displayRowTop && y <= displayRowBottom)
                {
                    int rowIdx = _displayScroll + (int)((y - displayRowTop) / DISPLAY_ROW_H);
                    if (rowIdx < _displayScroll + displayVisibleRows)
                    {
                        if (x >= DISPLAY_MOVE_UP_X && x <= DISPLAY_MOVE_UP_X + SORT_TRI_W) newHoveredDisplayMoveUp = rowIdx;
                        else if (x >= DISPLAY_MOVE_DOWN_X && x <= DISPLAY_MOVE_DOWN_X + SORT_TRI_W) newHoveredDisplayMoveDown = rowIdx;
                        else newHoveredDisplayRow = rowIdx;
                    }
                }

                if (GetDisplayPageMaxScroll() > 0f)
                {
                    GetPageScrollbarLayout(out float pageBarTop, out float pageBarH);
                    if (x >= WIDTH - 14 && x <= WIDTH && y >= pageBarTop && y <= pageBarTop + pageBarH)
                        newPageScrollbarHovered = true;
                }

                GetListScrollbarLayout(out float listBarTop, out float listBarH);
                if (listBarH > 0f && ScrollBarAlpha() > 0.01f
                    && x >= WIDTH - 20 && x <= WIDTH - 8
                    && y >= listBarTop && y <= listBarTop + listBarH)
                    newListScrollbarHovered = true;

                var displayCardHit = new SKRect(CONTENT_L, TITLE_BAR_HEIGHT + DISPLAY_CARD_Y + page,
                    WIDTH - CONTENT_RM, TITLE_BAR_HEIGHT + DISPLAY_CARD_Y + DISPLAY_CARD_H + page);
                bool inDisplayCard = x >= displayCardHit.Left && x <= displayCardHit.Right
                    && y >= displayCardHit.Top && y <= displayCardHit.Bottom;

                bool newWheelPriorityList = inDisplayCard
                    ? true
                    : (!newPageScrollbarHovered && !newListScrollbarHovered ? false : _wheelPriorityList);

                // 在卡片里攒的格数在出卡片后突然把整页顶走一段。
                if (newWheelPriorityList != _wheelPriorityList) _displayWheelCarry = 0;
                _wheelPriorityList = newWheelPriorityList;
            }
            else if (_selectedTab == 2) // 媒体设置
            {
                bool anyPopupOpen = _dropdownOpen || _matchModeDropdownOpen || _appDropdownOpen;

                // 媒体控制
                if (!anyPopupOpen && x >= WIDTH - 80 && x <= WIDTH - 30 && y >= TITLE_BAR_HEIGHT + 32 && y <= TITLE_BAR_HEIGHT + 52)
                    newMediaToggleHovered = true;

                if (!anyPopupOpen && x >= WIDTH - 140 && x <= WIDTH - 30 && y >= TITLE_BAR_HEIGHT + 98 && y <= TITLE_BAR_HEIGHT + 128)
                    newDropdownHovered = true;

                if (_dropdownOpen)
                {
                    if (x >= WIDTH - 140 && x <= WIDTH - 30 && y >= TITLE_BAR_HEIGHT + 130 && y < TITLE_BAR_HEIGHT + 130 + _platforms.Length * 26)
                        newHoveredDropdownIndex = (y - (TITLE_BAR_HEIGHT + 130)) / 26;
                }

                bool matchRowEnabled = MediaController.TargetPlatform == "other";
                newMatchModeDropdownHovered = !anyPopupOpen && matchRowEnabled
                    && x >= MATCH_MODE_X && x <= MATCH_MODE_X + MATCH_BOX_W && y >= MATCH_ROW_Y && y <= MATCH_ROW_Y + MATCH_BOX_H;
                newAppDropdownHovered = !anyPopupOpen && matchRowEnabled && MediaController.IsManualSessionMatch
                    && x >= MATCH_APP_X && x <= MATCH_APP_X + MATCH_BOX_W && y >= MATCH_ROW_Y && y <= MATCH_ROW_Y + MATCH_BOX_H;

                if (_matchModeDropdownOpen
                    && x >= MATCH_MODE_X && x <= MATCH_MODE_X + MATCH_BOX_W
                    && y >= MATCH_MENU_Y && y < MATCH_MENU_Y + _matchModeOptions.Length * 26)
                    newHoveredMatchModeIndex = (int)((y - MATCH_MENU_Y) / 26);

                if (_appDropdownOpen)
                {
                    int appRows = Math.Clamp(_appOptions.Length, 1, 8);
                    float appMenuX = MATCH_MENU_RIGHT - APP_MENU_W;
                    if (x >= appMenuX && x <= MATCH_MENU_RIGHT && y >= MATCH_MENU_Y && y < MATCH_MENU_Y + appRows * 26)
                        newHoveredAppIndex = (int)((y - MATCH_MENU_Y) / 26);
                }

                // 歌词设置卡片（坐标常量与 Render() 同源）
                float lyricY = LYRIC_CARD_Y;
                bool newLyricToggleHovered = !anyPopupOpen && (x >= WIDTH - 80 && x <= WIDTH - 30 && y >= lyricY + 37 && y <= lyricY + 57);
                bool newTransToggleHovered = !anyPopupOpen && (x >= WIDTH - 80 && x <= WIDTH - 30 && y >= lyricY + 77 && y <= lyricY + 97);
                bool newScanToggleHovered = !anyPopupOpen && (x >= WIDTH - 80 && x <= WIDTH - 30 && y >= lyricY + 117 && y <= lyricY + 137);

                // 延迟补偿按钮整行排在最后（歌词卡片里第四行）
                float btnY = lyricY + 147;
                float cardRightX = WIDTH - CONTENT_TEXT_RM;
                bool newLyricMinusHovered = !anyPopupOpen && (x >= cardRightX - 175 && x <= cardRightX - 145 && y >= btnY && y <= btnY + 24);
                bool newLyricPlusHovered = !anyPopupOpen && (x >= cardRightX - 80 && x <= cardRightX - 50 && y >= btnY && y <= btnY + 24);
                bool newLyricResetHovered = !anyPopupOpen && (x >= cardRightX - 40 && x <= cardRightX && y >= btnY && y <= btnY + 24);

                if (newLyricToggleHovered != _lyricToggleHovered || newTransToggleHovered != _transToggleHovered || newScanToggleHovered != _scanToggleHovered || newLyricMinusHovered != _lyricMinusHovered || newLyricPlusHovered != _lyricPlusHovered || newLyricResetHovered != _lyricResetHovered)
                {
                    _lyricToggleHovered = newLyricToggleHovered;
                    _transToggleHovered = newTransToggleHovered;
                    _scanToggleHovered = newScanToggleHovered;
                    _lyricMinusHovered = newLyricMinusHovered;
                    _lyricPlusHovered = newLyricPlusHovered;
                    _lyricResetHovered = newLyricResetHovered;
                    Render();
                }

                bool newHotkeyToggleHovered = !anyPopupOpen
                    && x >= WIDTH - 80 && x <= WIDTH - 30
                    && y >= HOTKEY_CARD_Y + 12 && y <= HOTKEY_CARD_Y + 32;

                int newHoveredHotkeyRow = -1;
                if (!anyPopupOpen && x >= HOTKEY_BOX_X && x <= HOTKEY_BOX_RIGHT)
                {
                    for (int i = 0; i < MediaHotkeys.Count; i++)
                    {
                        float rowTop = HOTKEY_CARD_Y + HOTKEY_HEAD_H + i * HOTKEY_ROW_H + 4;
                        if (y >= rowTop && y <= rowTop + HOTKEY_BOX_H) { newHoveredHotkeyRow = i; break; }
                    }
                }

                if (newHotkeyToggleHovered != _hotkeyToggleHovered || newHoveredHotkeyRow != _hoveredHotkeyRow)
                {
                    _hotkeyToggleHovered = newHotkeyToggleHovered;
                    _hoveredHotkeyRow = newHoveredHotkeyRow;
                    Render();
                }
            }
            else if (_selectedTab == 3) // 交互设置
            {
                // 本段的 y 值必须与下面 tab 3 的渲染保持同步
                // 行 1：自动隐藏总开关
                if (x >= WIDTH - 80 && x <= WIDTH - 30 && y >= TITLE_BAR_HEIGHT + 32 && y <= TITLE_BAR_HEIGHT + 52)
                    newAutoHideToggleHovered = true;
                // 行 2：当焦点离开时自动隐藏岛
                if (x >= WIDTH - 80 && x <= WIDTH - 30 && y >= TITLE_BAR_HEIGHT + 94 && y <= TITLE_BAR_HEIGHT + 114)
                    newFocusHideToggleHovered = true;
                // 行 3：暂停播放后自动隐藏
                if (x >= WIDTH - 80 && x <= WIDTH - 30 && y >= TITLE_BAR_HEIGHT + 156 && y <= TITLE_BAR_HEIGHT + 176)
                    newPauseHideToggleHovered = true;
                // 行 4：全屏自动隐藏
                if (x >= WIDTH - 80 && x <= WIDTH - 30 && y >= TITLE_BAR_HEIGHT + 218 && y <= TITLE_BAR_HEIGHT + 238)
                    newFsHideToggleHovered = true;
                if (x >= WIDTH - 80 && x <= WIDTH - 30 && y >= TITLE_BAR_HEIGHT + 290 && y <= TITLE_BAR_HEIGHT + 310)
                    newMediaExpToggleHovered = true;
                if (x >= WIDTH - 80 && x <= WIDTH - 30 && y >= TITLE_BAR_HEIGHT + 362 && y <= TITLE_BAR_HEIGHT + 382)
                    newAppLaunchToggleHovered = true;
                // 使用局部变量，防止状态死锁
                newPassToggleHovered = x >= WIDTH - 80 && x <= WIDTH - 30 && y >= TITLE_BAR_HEIGHT + 434 && y <= TITLE_BAR_HEIGHT + 454;
            }
            else if (_selectedTab == 7) // 插件市场
            {
                if (_marketSearchDragging)
                {
                    if (dragging)
                    {
                        int caret = MarketSearchIndexAtX(x);
                        if (caret != _marketSearchCaret) { _marketSearchCaret = caret; Render(); }
                    }
                    else _marketSearchDragging = false;
                }

                //    只看下标会让它开着的时候底下的控件照样亮起来。
                if (_marketDialog == MarketDialog.None)
                {
                    newMarketSearchHovered = x >= MarketSearchX && x <= MarketSearchX + MarketSearchW
                        && y >= MarketControlsY && y <= MarketControlsY + MarketControlH;

                    newMarketRefreshHovered = x >= MarketRefreshX && x <= MarketRefreshX + MarketRefreshW
                        && y >= MarketStatusRowY && y <= MarketStatusRowY + MarketControlH;

                    newHoveredMarketChk = x >= MarketChkX - 4f
                        && x <= MarketChkLabelX + 64f
                        && y >= MarketStatusRowY && y <= MarketStatusRowY + MarketControlH;

                    bool overCatBtn = x >= CONTENT_TEXT_X && x <= CONTENT_TEXT_X + MarketCatBtnW
                        && y >= MarketControlsY && y <= MarketControlsY + MarketControlH;

                    if (_marketCategoryOpen)
                    {
                        const float rowH = 26f;
                        float mY = MarketControlsY + MarketControlH + 4;
                        if (x >= CONTENT_TEXT_X && x <= CONTENT_TEXT_X + MarketCatBtnW && y >= mY && y < mY + MarketCategories.Length * rowH)
                            newHoveredMarketCategoryIndex = (int)((y - mY) / rowH);
                    }
                    if (overCatBtn && newHoveredMarketCategoryIndex == -1 && !_marketCategoryOpen)
                        newHoveredMarketCategoryIndex = -2;
                }

                if (_marketDialog == MarketDialog.LoadFailed)
                {
                    var rect = GetCurrentDialogRect();
                    if (rect.Width > 0)
                    {
                        var close = GetMarketDialogCloseRect(rect);
                        close.Inflate(4f, 4f);
                        newHoveredDialogClose = close.Contains(x, y);
                        if (GetDialogSingleButtonRect(rect).Contains(x, y)) newHoveredDialogButton = 0;
                    }
                }
                else if (_marketDialogIndex != -1)
                {
                    var mp = GetMarketAt(_marketDialogIndex);
                    var rect = GetCurrentDialogRect();
                    if (mp != null && rect.Width > 0)
                    {
                        var close = GetMarketDialogCloseRect(rect);
                        close.Inflate(4f, 4f);
                        newHoveredDialogClose = close.Contains(x, y);

                        if (_marketDialog == MarketDialog.Rate && _rateMine <= 0 && !_rateLoading)
                        {
                            // 星星悬停 → 实时预览分值（半星粒度）。
                            var sr = GetRateStarsRect(rect);
                            if (sr.Contains(x, y))
                            {
                                float slot = sr.Width / 5f;
                                float rel = (x - sr.Left) / slot;
                                float intPart = MathF.Floor(rel);
                                double stars = intPart + ((rel - intPart) >= 0.5f ? 1.0 : 0.5);
                                newRateStars = Math.Clamp(stars, 0.5, 5.0);
                            }
                        }
                    }
                }
                else if (!_marketCategoryOpen)
                {
                    GetMarketListLayout(out int mRows, out int mMaxFirst);
                    _marketScroll = Math.Clamp(_marketScroll, 0, mMaxFirst);
                    float marketRowsTop = MarketRowsTop;   // 与 RenderTabMarket / GetMarketListLayout 严格同源
                    if (x >= CONTENT_TEXT_X && x <= WIDTH - CONTENT_TEXT_RM && y >= marketRowsTop)
                    {
                        int slot = (int)((y - marketRowsTop) / PluginListRowH);
                        int idx = slot + _marketScroll;
                        if (slot >= 0 && slot < mRows)
                        {
                            float rowY = marketRowsTop + slot * PluginListRowH;
                            if (y >= rowY + 18 && y <= rowY + 44)
                            {
                                var mp = _marketView[idx];
                                bool busy = string.Equals(_marketBusyId, mp.Id, StringComparison.Ordinal);
                                var local = MatchLocalPlugin(mp);
                                if (x >= MarketBtn1X && x <= MarketBtn1X + MarketBtnW)
                                { if (!busy) newHoveredMarketInstall = idx; }
                                else if (x >= MarketBtn2X && x <= MarketBtn2X + MarketBtnW)
                                { if (local != null && !busy) newHoveredMarketUninstall = idx; }
                                else if (x >= MarketBtn3X && x <= MarketBtn3X + MarketBtnW)
                                    newHoveredMarketDetail = idx;
                            }
                        }
                    }
                }
            }
            else if (_selectedTab == 6) // 我的插件
            {
                if (_marketDialog == MarketDialog.LoadFailed)
                {
                    var rect = GetCurrentDialogRect();
                    if (rect.Width > 0)
                    {
                        var close = GetMarketDialogCloseRect(rect);
                        close.Inflate(4f, 4f);
                        newHoveredDialogClose = close.Contains(x, y);
                        if (GetDialogSingleButtonRect(rect).Contains(x, y)) newHoveredDialogButton = 0;
                    }
                }
                else
                {
                    float topY = TITLE_BAR_HEIGHT + 12;
                    // 顶部操作按钮：导入 DLL | 打开目录 | 插件市场
                    if (y >= topY + 60 && y <= topY + 84)
                    {
                        if (x >= CONTENT_TEXT_X && x <= CONTENT_TEXT_X + 96) newHoveredPluginAction = 0;       // 导入 DLL
                        else if (x >= CONTENT_TEXT_X + 104 && x <= CONTENT_TEXT_X + 200) newHoveredPluginAction = 1;  // 打开目录
                        else if (x >= CONTENT_TEXT_X + 208 && x <= CONTENT_TEXT_X + 304) newHoveredPluginAction = 2;  // 插件市场
                    }

                    GetPluginListCardTop(out float listY);
                    GetPluginListLayout(out int rows, out int pluginMaxFirst);
                    _pluginScroll = Math.Clamp(_pluginScroll, 0, pluginMaxFirst);
                    if (x >= CONTENT_TEXT_X && x <= WIDTH - CONTENT_TEXT_RM && y >= listY + 44)
                    {
                        int slot = (int)((y - (listY + 44)) / PluginListRowH);
                        int idx = slot + _pluginScroll;
                        if (slot >= 0 && slot < rows)
                        {
                            float rowY = listY + 44 + slot * PluginListRowH;
                            if (y >= rowY + 18 && y <= rowY + 44)
                            {
                                // 从左到右：重载 | 卸载 | 开关
                                if (x >= PLUGIN_BTN_RELOAD_X && x <= PLUGIN_BTN_RELOAD_X + 50) newHoveredPluginReload = idx;
                                else if (x >= PLUGIN_BTN_REMOVE_X && x <= PLUGIN_BTN_REMOVE_X + 50) newHoveredPluginRemove = idx;
                                else if (x >= PLUGIN_BTN_TOGGLE_X && x <= PLUGIN_BTN_TOGGLE_X + 42) newHoveredPluginToggle = idx;
                            }
                        }
                    }
                }
            }

            int newHoveredLinkIndex = -1;
            if (_selectedTab == 4) // 关于页
            {
                int yStart = TITLE_BAR_HEIGHT + 160;
                int yEnd = TITLE_BAR_HEIGHT + 190;

                if (y >= yStart && y <= yEnd)
                {
                    if (x >= 305 && x <= 370) newHoveredLinkIndex = 0;      // 检测更新
                    else if (x >= 375 && x <= 440) newHoveredLinkIndex = 1; // 仓库地址
                    else if (x >= 445 && x <= 500) newHoveredLinkIndex = 2; // 开发者 (Ryen)
                }
            }

            bool newIsHoveringDisabledArea = false;
            // 否则就会出现「明明能点、却显示禁止指针」。
            if (x >= 200 && x <= WIDTH - CONTENT_RM)
            {
                if (_selectedTab == 0 && !ToastSoundConfig.IsRowEnabled
                    && y >= TITLE_BAR_HEIGHT + SOUND_ROW_Y && y <= TITLE_BAR_HEIGHT + SOUND_BOX_Y + SOUND_ROW_H)
                {
                    newIsHoveringDisabledArea = true;
                }
                //     总开关条件拦下（点不动），这里不给禁止指针。
                //     所以这里同样没有它对应的禁止指针区间。
            }

            if (newIsHoveringDisabledArea != _isHoveringDisabledArea) _isHoveringDisabledArea = newIsHoveringDisabledArea;

            if (newMinHovered != _minHovered || newCloseHovered != _closeHovered ||
                newHoveredTab != _hoveredTab || newToggleHovered != _toggleHovered ||
                newToastToggleHovered != _toastToggleHovered || newTopmostToggleHovered != _topmostToggleHovered ||
                newMediaToggleHovered != _mediaToggleHovered || newAutoHideToggleHovered != _autoHideToggleHovered ||
                newFocusHideToggleHovered != _focusHideToggleHovered ||
                newPauseHideToggleHovered != _pauseHideToggleHovered ||
                newFsHideToggleHovered != _fsHideToggleHovered ||
                newDropdownHovered != _dropdownHovered ||
                newMatchModeDropdownHovered != _matchModeDropdownHovered ||
                newHoveredMatchModeIndex != _hoveredMatchModeIndex ||
                newAppDropdownHovered != _appDropdownHovered ||
                newHoveredAppIndex != _hoveredAppIndex ||
                newHoveredDropdownIndex != _hoveredDropdownIndex || newHoveredLinkIndex != _hoveredLinkIndex ||
                newHoveredStyleIndex != _hoveredStyleIndex ||
                newHoveredDisplayModeIndex != _hoveredDisplayModeIndex ||
                newHoveredStandbySceneIndex != _hoveredStandbySceneIndex ||
                newStandbyToggleHovered != _standbyToggleHovered ||
                newPageScrollbarHovered != _pageScrollbarHovered ||
                newListScrollbarHovered != _listScrollbarHovered ||
                newHoveredDisplayRow != _hoveredDisplayRow ||
                newHoveredDisplayMoveUp != _hoveredDisplayMoveUp ||
                newHoveredDisplayMoveDown != _hoveredDisplayMoveDown ||
                newHoverMinus != _hoveredMinusIndex || newHoverPlus != _hoveredPlusIndex ||
                newHoverReset != _hoveredResetIndex || newMediaExpToggleHovered != _mediaExpToggleHovered ||
                newAppLaunchToggleHovered != _appLaunchToggleHovered ||
                newHoveredTheme != _hoveredThemeIndex || newHoveredOpacityIndex != _hoveredOpacityIndex ||
                newMonitorDropdownHovered != _monitorDropdownHovered ||
                newHoveredMonitorDropdownIndex != _hoveredMonitorDropdownIndex ||
                newToastModeDropdownHovered != _toastModeDropdownHovered ||
                newHoveredToastModeIndex != _hoveredToastModeIndex ||
                newToastSoundDropdownHovered != _toastSoundDropdownHovered ||
                newHoveredToastSoundIndex != _hoveredToastSoundIndex ||
                newSoundVolumeDropdownHovered != _soundVolumeDropdownHovered ||
                newHoveredSoundVolumeIndex != _hoveredSoundVolumeIndex ||
                newSoundToggleHovered != _soundToggleHovered ||
                newSoundPreviewHovered != _soundPreviewHovered ||
                newSoundResetHovered != _soundResetHovered ||
                newPassToggleHovered != _passToggleHovered ||
                newClipboardToggleHovered != _clipboardToggleHovered ||
                newHoveredPluginAction != _hoveredPluginAction ||
                newHoveredPluginToggle != _hoveredPluginToggle ||
                newHoveredPluginReload != _hoveredPluginReload ||
                newHoveredPluginRemove != _hoveredPluginRemove ||
                newHoveredMarketInstall != _hoveredMarketInstall ||
                newHoveredMarketUninstall != _hoveredMarketUninstall ||
                newHoveredMarketDetail != _hoveredMarketDetail ||
                newHoveredDialogClose != _hoveredDialogClose ||
                newHoveredDialogButton != _hoveredDialogButton ||
                newRateStars != _rateStars ||
                newHoveredMarketChk != _hoveredMarketChk ||
                newHoveredMarketCategoryIndex != _hoveredMarketCategoryIndex ||
                newMarketSearchHovered != _marketSearchHovered ||
                newMarketRefreshHovered != _hoveredMarketRefresh ||
                newFontPickHovered != _fontPickHovered || newFontResetHovered != _fontResetHovered
                )
            {
                _minHovered = newMinHovered; _closeHovered = newCloseHovered;
                _hoveredTab = newHoveredTab; _toggleHovered = newToggleHovered;
                _toastToggleHovered = newToastToggleHovered;
                _mediaToggleHovered = newMediaToggleHovered; _autoHideToggleHovered = newAutoHideToggleHovered;
                _focusHideToggleHovered = newFocusHideToggleHovered;
                _pauseHideToggleHovered = newPauseHideToggleHovered;
                _fsHideToggleHovered = newFsHideToggleHovered;
                _dropdownHovered = newDropdownHovered;
                _hoveredDropdownIndex = newHoveredDropdownIndex;
                _matchModeDropdownHovered = newMatchModeDropdownHovered;
                _hoveredMatchModeIndex = newHoveredMatchModeIndex;
                _appDropdownHovered = newAppDropdownHovered;
                _hoveredAppIndex = newHoveredAppIndex;
                _hoveredLinkIndex = newHoveredLinkIndex;
                _hoveredDisplayRow = newHoveredDisplayRow;
                _hoveredDisplayMoveUp = newHoveredDisplayMoveUp;
                _hoveredDisplayMoveDown = newHoveredDisplayMoveDown;
                _hoveredStyleIndex = newHoveredStyleIndex;
                _hoveredDisplayModeIndex = newHoveredDisplayModeIndex;
                _hoveredStandbySceneIndex = newHoveredStandbySceneIndex;
                _standbyToggleHovered = newStandbyToggleHovered;
                _pageScrollbarHovered = newPageScrollbarHovered;
                _listScrollbarHovered = newListScrollbarHovered;
                _hoveredMinusIndex = newHoverMinus;
                _hoveredPlusIndex = newHoverPlus;
                _hoveredResetIndex = newHoverReset;
                _hoveredThemeIndex = newHoveredTheme;
                _mediaExpToggleHovered = newMediaExpToggleHovered;
                _appLaunchToggleHovered = newAppLaunchToggleHovered;
                _hoveredOpacityIndex = newHoveredOpacityIndex;
                _monitorDropdownHovered = newMonitorDropdownHovered;
                _hoveredMonitorDropdownIndex = newHoveredMonitorDropdownIndex;
                _toastModeDropdownHovered = newToastModeDropdownHovered;
                _hoveredToastModeIndex = newHoveredToastModeIndex;
                _toastSoundDropdownHovered = newToastSoundDropdownHovered;
                _hoveredToastSoundIndex = newHoveredToastSoundIndex;
                _soundVolumeDropdownHovered = newSoundVolumeDropdownHovered;
                _hoveredSoundVolumeIndex = newHoveredSoundVolumeIndex;
                _soundToggleHovered = newSoundToggleHovered;
                _soundPreviewHovered = newSoundPreviewHovered;
                _soundResetHovered = newSoundResetHovered;
                _topmostToggleHovered = newTopmostToggleHovered;
                _passToggleHovered = newPassToggleHovered;
                _clipboardToggleHovered = newClipboardToggleHovered;
                _hoveredPluginAction = newHoveredPluginAction;
                _hoveredPluginToggle = newHoveredPluginToggle;
                _hoveredPluginReload = newHoveredPluginReload;
                _hoveredPluginRemove = newHoveredPluginRemove;
                _hoveredMarketInstall = newHoveredMarketInstall;
                _hoveredMarketUninstall = newHoveredMarketUninstall;
                _hoveredMarketDetail = newHoveredMarketDetail;
                _hoveredDialogClose = newHoveredDialogClose;
                _hoveredDialogButton = newHoveredDialogButton;
                if (newRateStars != _rateStars) _rateStars = newRateStars;
                _hoveredMarketChk = newHoveredMarketChk;
                _hoveredMarketCategoryIndex = newHoveredMarketCategoryIndex;
                _marketSearchHovered = newMarketSearchHovered;
                _hoveredMarketRefresh = newMarketRefreshHovered;
                _fontPickHovered = newFontPickHovered;
                _fontResetHovered = newFontResetHovered;

                int newDisplayHoverRow = newHoveredDisplayRow != -1 ? newHoveredDisplayRow
                    : (newHoveredDisplayMoveUp != -1 ? newHoveredDisplayMoveUp : newHoveredDisplayMoveDown);
                int newDisplayHoverSlot = newDisplayHoverRow == -1 ? -1 : newDisplayHoverRow - _displayScroll;
                if (newDisplayHoverSlot != _displayHoverRow)
                {
                    _displayHoverRow = newDisplayHoverSlot;
                    StartDisplayHoverAnim();
                }

                Render();
            }
        }
    }
}
