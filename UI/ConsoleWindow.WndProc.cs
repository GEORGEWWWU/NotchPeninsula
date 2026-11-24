using SkiaSharp;
using NotchPeninsula.Plugins;

namespace NotchPeninsula
{
    public partial class ConsoleWindow
    {
        /// <summary>
        /// 模态弹窗自己的悬停。6、7 页签在各自的列表分支里已经算过一遍，这里负责其余页签：
        /// 弹窗既然能跨页签弹出（加载失败），它的按钮反馈也得跟着跨页签存在。
        /// </summary>
        private void UpdateModalDialogHover(int x, int y)
        {
            var rect = GetCurrentDialogRect();
            bool closeHovered = false;
            int button = -1;
            bool linkHovered = false;

            if (rect.Width > 0)
            {
                var close = GetMarketDialogCloseRect(rect);
                close.Inflate(4f, 4f);
                closeHovered = close.Contains(x, y);
                if (!closeHovered) button = HitDialogButton(rect, x, y);
                if (!closeHovered && IsNoticeDialog)
                    linkHovered = HitLoadFailedLink(rect, x, y);
            }

            if (closeHovered == _hoveredDialogClose && button == _hoveredDialogButton
                && linkHovered == _loadFailedLinkHovered) return;

            _hoveredDialogClose = closeHovered;
            _hoveredDialogButton = button;
            _loadFailedLinkHovered = linkHovered;
            Render();
        }

        //    因此它是一个整体，不要再按页签拆开。
        private void OnMouseMove(int x, int y, bool dragging)
        {

            bool toggleHovered = !IsConfirmDialogOpen && SidebarToggleRect.Contains(x, y);
            if (_sidebarToggleHovered != toggleHovered)
            {
                _sidebarToggleHovered = toggleHovered;
                Render();
            }

            bool newMinHovered = x >= WIDTH - 92 && x < WIDTH - 46 && y <= TITLE_BAR_HEIGHT;
            bool newCloseHovered = x >= WIDTH - 46 && x <= WIDTH && y <= TITLE_BAR_HEIGHT;

            // Tab Hover 判定：与绘制 / 光标共用一套几何（HitSidebarTab）
            int newHoveredTab = HitSidebarTab(x, y);

            // 弹窗是模态的：它盖住的顶栏与侧栏不该再亮，否则鼠标划过弹窗还能看见下面在闪
            if (IsConfirmDialogOpen)
            {
                newMinHovered = false;
                newCloseHovered = false;
                newHoveredTab = -1;

                // 加载失败弹窗可能出现在任意页签（市场后台装完 / 插件页重载），
                // 所以它的悬停不能只挂在 6、7 的分支里 —— 否则在别的页签上「好的」不会亮。
                if (_selectedTab != 6 && _selectedTab != 7)
                {
                    // 顶栏 / 侧栏的悬停先落定：直接 return 会把「最小化键还亮着」留在画面上。
                    bool chromeChanged = newMinHovered != _minHovered
                        || newCloseHovered != _closeHovered || newHoveredTab != _hoveredTab;
                    _minHovered = newMinHovered;
                    _closeHovered = newCloseHovered;
                    _hoveredTab = newHoveredTab;
                    if (chromeChanged) Render();
                    UpdateModalDialogHover(x, y);
                    return;
                }
            }

            // 列表拖动期间不再算任何 hover：行随拖动实时重排，hover 下标会漂，
            // 而且每帧 Render 应该只服务拖动本身。
            if (_displayDragActive)
            {
                UpdateDisplayDrag(y);
                return;
            }
            // 已按下把手、还在等长按判定：保持当前画面，别让底下的 hover 抢先亮。
            if (_displayDragPendingRow >= 0) return;

            if (_draggedGlassParam >= 0 && dragging)
            {
                int level = GlassLevelAt(_draggedGlassParam, x);
                _hoveredGlassParamLevel = level;
                if (GlassMaterial.GetLevel(_draggedGlassParam) != level)
                {
                    ApplyGlassLevel(_draggedGlassParam, level);
                    Render();
                }
                return;
            }

            var newPersonalizeDropdown = DropdownKind.None;
            int newPersonalizeOption = -1;
            int newHoveredGlassParam = -1;
            int newHoveredGlassParamLevel = -1;
            bool newHoveredGlassReset = false;
            bool immersiveHovered = false;
            int newHoverMinus = -1, newHoverPlus = -1, newHoverReset = -1;
            if (_selectedTab == 5 && _personalizeDropdownOpen != DropdownKind.None)
            {
                newPersonalizeOption = HitPersonalizeOption(x, y);
            }
            else if (_selectedTab == 5)
            {
                newPersonalizeDropdown = HitDropdownTrigger(x, y);
                immersiveHovered = HitImmersiveToggle(x, y);
                newHoveredGlassParam = HitGlassTrack(x, y);
                if (newHoveredGlassParam >= 0)
                    newHoveredGlassParamLevel = GlassLevelAt(newHoveredGlassParam, x);

                float resetY = TITLE_BAR_HEIGHT + GLASS_CARD_Y + GlassResetTopY;
                newHoveredGlassReset = Renderer.IslandLiquidGlass
                    && x >= GLASS_RESET_X && x <= GLASS_RESET_X + GLASS_RESET_W
                    && y >= resetY && y <= resetY + GLASS_RESET_H;

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
            bool newDisplayModeDropdownHovered = false;
            int newHoveredDisplayModeIndex = -1;
            int newHoveredStandbySceneIndex = -1; // 待机显示内容下拉：列表项的悬停下标
            bool newSceneDropdownHovered = false; // 待机显示内容下拉：框本体的悬停
            bool newStandbyToggleHovered = false; // 待机模式：「双击空白切换」开关
            bool newStandbyRowHovered = false;    // 待机模式：整行（只用来显示右侧蓝字提示，不参与点击）
            bool newPageScrollbarHovered = false; // 显示设置：整页滚动条
            bool newListScrollbarHovered = false; // 显示设置：「显示内容」列表滚动条
            int newHoveredDisplayRow = -1;        // 「显示内容」列表：悬停在行本体上
            int newHoveredDisplayMoveUp = -1;     // 该行 ∧ 的悬停
            int newHoveredDisplayMoveDown = -1;   // 该行 ∨ 的悬停
            int newHoveredDisplayDrag = -1;       // 该行 ≡ 拖动把手的悬停
            int newHoveredSilentRow = -1;         // 静默模式设置页（tab 9）：行本体
            int newHoveredSilentMoveUp = -1;      // 同上：∧
            int newHoveredSilentMoveDown = -1;    // 同上：∨
            bool newSilentSwitchHovered = false;  // 静默页右上角「去切换」按钮
            bool newSilentToastHovered = false;   // 静默页「消息通知」卡
            bool newSilentMediaHovered = false;   // 静默页「媒体」卡
            bool newToggleHovered = false;
            bool newToastToggleHovered = false;
            bool newTopmostToggleHovered = false;
            bool newMediaToggleHovered = false;
            bool newMediaLiveToggleHovered = false;   // 「使用实况窗展示媒体」
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
            bool newIslandHotkeyHovered = false;       // 交互页「唤出灵动岛」的键位框
            bool newIslandHotkeyToggleHovered = false; // 同一张卡的总开关
            bool newSilentHotkeyHovered = false;       // 交互页「静默模式」的键位框
            bool newSilentHotkeyToggleHovered = false; // 同一张卡的总开关
            bool newMspToggleHovered = false; // 「MSP 接入（实验性）」
            bool newMspWikiHovered = false;   // 「实验性」标签右边的外链图标
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
            bool newLiveWindowMediaHovered = false;   // 实况窗管理页：媒体行
            int newLiveWindowHoverRow = -1;           // 实况窗管理页：插件行
            int newHoveredMarketSortIndex = -1;   // 市场排序：菜单内悬停项；-2 = 框本体
            bool newHoveredMarketSortBox = false;
            int newHoveredPluginToggle = -1;
            int newHoveredPluginReload = -1;
            int newHoveredPluginRemove = -1;
            int newHoveredPluginSettings = -1;
            bool newSettingsBackHovered = false;
            int newHoveredSettingControl = -1;
            int newHoveredSettingSegment = -1;
            int newHoveredMarketInstall = -1;
            int newHoveredMarketUninstall = -1;
            int newHoveredMarketDetail = -1;
            int newHoveredMarketCategoryIndex = -1;
            bool newHoveredDialogClose = false;
            int newHoveredDialogButton = -1;
            bool newLoadFailedLinkHovered = false;
            double newRateStars = 0;
            bool newHoveredMarketChk = false;
            bool newMarketSearchHovered = false;
            bool newMarketRefreshHovered = false;
            bool newHoveredMarketUpdateAll = false;
            bool newFontPickHovered = false;
            bool newFontResetHovered = false;

            if (_selectedTab == 0) // 通用设置
            {
                // 开机自启
                if (x >= LeftColumnRight - 62 && x <= LeftColumnRight - 14 && y >= TITLE_BAR_HEIGHT + 32 && y <= TITLE_BAR_HEIGHT + 52)
                    newToggleHovered = true;
                // 灵动岛置顶开关
                if (x >= LeftColumnRight - 62 && x <= LeftColumnRight - 14 && y >= TITLE_BAR_HEIGHT + 104 && y <= TITLE_BAR_HEIGHT + 124)
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
                    if (x >= TOAST_MODE_CTRL_X && x <= TOAST_MODE_CTRL_X + TOAST_MODE_CTRL_W)
                    {
                        int row = DropdownRowAt(menuTop, _toastModeOptions.Length, y);
                        if (row >= 0) newHoveredToastModeIndex = row;
                    }
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
                    if (x >= SOUND_CTRL_X && x <= SOUND_CTRL_X + SOUND_CTRL_W)
                    {
                        int row = DropdownRowAt(menuTop, visible, y);
                        if (row >= 0) newHoveredToastSoundIndex = first + row;
                    }
                }

                if (soundReady && !_soundVolumeDropdownOpen
                    && x >= SOUND_VOL_X && x <= SOUND_VOL_X + SOUND_VOL_W
                    && y >= TITLE_BAR_HEIGHT + SOUND_BOX_Y && y <= TITLE_BAR_HEIGHT + SOUND_BOX_Y + SOUND_ROW_H)
                    newSoundVolumeDropdownHovered = true;
                if (soundReady && _soundVolumeDropdownOpen)
                {
                    GetVolumeMenuLayout(out float menuTopV, out int volVisible, out _);
                    if (x >= SOUND_VOL_X && x <= SOUND_VOL_X + SOUND_VOL_W)
                    {
                        int row = DropdownRowAt(menuTopV, volVisible, y);
                        if (row >= 0) newHoveredSoundVolumeIndex = row;
                    }
                }

                if (soundReady && y >= TITLE_BAR_HEIGHT + SOUND_BTN_Y && y <= TITLE_BAR_HEIGHT + SOUND_BTN_Y + SOUND_BTN_H)
                {
                    if (x >= SOUND_PREVIEW_X && x <= SOUND_PREVIEW_X + SOUND_BTN_W) newSoundPreviewHovered = true;
                    if (x >= SOUND_RESET_X && x <= SOUND_RESET_X + SOUND_BTN_W) newSoundResetHovered = true;
                }

                // MSP 接入：卡片行首 MSP_CARD_Y + 开关位移
                if (x >= LeftColumnRight - 62 && x <= LeftColumnRight - 14
                    && y >= TITLE_BAR_HEIGHT + MSP_CARD_Y + ROW_ANCHOR_Y - TOGGLE_TRACK_H / 2f
                    && y <= TITLE_BAR_HEIGHT + MSP_CARD_Y + ROW_ANCHOR_Y + TOGGLE_TRACK_H / 2f)
                    newMspToggleHovered = true;

                // 「实验性」标签右边的外链图标（命中区比图标大一圈）
                if (MspWikiIconRect().Contains(x, y))
                    newMspWikiHovered = true;

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
                bool dropdownOpen = _monitorDropdownOpen || _displayModeDropdownOpen || _sceneDropdownOpen;

                float styleY = TITLE_BAR_HEIGHT + STYLE_OPT_Y + page;
                if (!dropdownOpen && y >= styleY && y <= styleY + STYLE_OPT_H)
                {
                    if (x >= STYLE_OPT_X && x <= STYLE_OPT_X + STYLE_OPT_W) newHoveredStyleIndex = 0;
                    else
                    {
                        float styleX1 = STYLE_OPT_X + STYLE_OPT_W + CARD_GAP;
                        if (x >= styleX1 && x <= styleX1 + STYLE_OPT_W) newHoveredStyleIndex = 1;
                    }
                }

                float mdY = TITLE_BAR_HEIGHT + MONITOR_ROW_Y + (ROW_H - MONITOR_DD_H) / 2f + page;
                float mdX = DISPLAY_CTRL_RIGHT - MONITOR_DD_W;
                if (!dropdownOpen && HitMonitorDropdownBox(x, y))
                    newMonitorDropdownHovered = true;

                if (_monitorDropdownOpen)
                {
                    float listY = mdY + MONITOR_DD_H + 2f;
                    if (x >= mdX && x <= DISPLAY_CTRL_RIGHT)
                    {
                        int row = DropdownRowAt(listY, _monitorOptions.Length, y);
                        if (row >= 0) newHoveredMonitorDropdownIndex = row;
                    }
                }

                if (_displayModeDropdownOpen)
                    newHoveredDisplayModeIndex = HitDisplayModeOption(x, y);
                else if (!dropdownOpen)
                    newDisplayModeDropdownHovered = DisplayModeDropdownBox.Contains(x, y);

                if (!dropdownOpen && Renderer.StandbyModeOnly && HitSceneDropdownBox(x, y))
                    newSceneDropdownHovered = true;

                if (_sceneDropdownOpen)
                {
                    GetSceneMenuLayout(out float sceneMenuTop, out int sceneVisible, out int sceneMaxFirst);
                    int sceneFirst = Math.Clamp(_sceneScroll, 0, sceneMaxFirst);
                    if (x >= SCENE_SEG_X && x <= SCENE_SEG_X + SCENE_SEG_W)
                    {
                        int row = DropdownRowAt(sceneMenuTop, sceneVisible, y);
                        if (row >= 0) newHoveredStandbySceneIndex = sceneFirst + row;
                    }
                }

                float modeToggleCy = TITLE_BAR_HEIGHT + TOGGLE_ROW_Y + page + ROW_ANCHOR_Y;
                // 静默模式下这一整行置灰（岛本来就退场，双击切换没有意义）。
                if (!dropdownOpen && !Renderer.SilentActive
                    && x >= LeftColumnRight - 62 && x <= LeftColumnRight - 14
                    && y >= modeToggleCy - TOGGLE_TRACK_H / 2f && y <= modeToggleCy + TOGGLE_TRACK_H / 2f)
                    newStandbyToggleHovered = true;

                // 整行悬停只服务右侧那句蓝字提示（悬浮才显示，点击仍只认开关本体）。
                float standbyRowTop = TITLE_BAR_HEIGHT + TOGGLE_ROW_Y + page;
                if (!dropdownOpen && !Renderer.SilentActive
                    && x >= CONTENT_TEXT_X && x <= LeftColumnRight
                    && y >= standbyRowTop && y <= standbyRowTop + ROW_H)
                    newStandbyRowHovered = true;

                GetDisplayListLayout(out int displayVisibleRows, out int displayMaxFirstRow);
                _displayScroll = Math.Clamp(_displayScroll, 0, displayMaxFirstRow);
                float displayRowTop = TITLE_BAR_HEIGHT + DISPLAY_CARD_Y + DISPLAY_FIRST_ROW_Y + page;
                float displayRowBottom = TITLE_BAR_HEIGHT + DISPLAY_CARD_Y + DISPLAY_CARD_H + page;
                // 待机 / 静默模式下这张卡不控制任何东西（静默清单在 tab 9），整卡不吃悬停。
                if (!Renderer.StandbyModeOnly && !Renderer.SilentActive && !dropdownOpen
                    && x >= DISPLAY_ITEM_L && x <= DISPLAY_ITEM_R && y >= displayRowTop && y <= displayRowBottom)
                {
                    int rowIdx = _displayScroll + (int)((y - displayRowTop) / DISPLAY_ROW_H);
                    if (rowIdx < _displayScroll + displayVisibleRows)
                    {
                        if (x >= DISPLAY_DRAG_X && x <= DISPLAY_DRAG_X + SORT_TRI_W) newHoveredDisplayDrag = rowIdx;
                        else if (x >= DISPLAY_MOVE_UP_X && x <= DISPLAY_MOVE_UP_X + SORT_TRI_W) newHoveredDisplayMoveUp = rowIdx;
                        else if (x >= DISPLAY_MOVE_DOWN_X && x <= DISPLAY_MOVE_DOWN_X + SORT_TRI_W) newHoveredDisplayMoveDown = rowIdx;
                        else newHoveredDisplayRow = rowIdx;
                    }
                }

                if (!dropdownOpen && GetDisplayPageMaxScroll() > 0f)
                {
                    GetPageScrollbarLayout(out float pageBarTop, out float pageBarH);
                    if (x >= DisplayPageScrollbarX - 6f && x <= WIDTH - CONTENT_RM && y >= pageBarTop && y <= pageBarTop + pageBarH)
                        newPageScrollbarHovered = true;
                }

                GetListScrollbarLayout(out float listBarTop, out float listBarH);
                if (!dropdownOpen && listBarH > 0f && ScrollBarAlpha() > 0.01f
                    && x >= DisplayListScrollbarX - 6f && x <= WIDTH - CONTENT_RM
                    && y >= listBarTop && y <= listBarTop + listBarH)
                    newListScrollbarHovered = true;

                var displayCardHit = new SKRect(RightColumnLeft, TITLE_BAR_HEIGHT + DISPLAY_CARD_Y + page,
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
                if (!anyPopupOpen && x >= LeftColumnRight - 62 && x <= LeftColumnRight - 14 && y >= TITLE_BAR_HEIGHT + MediaRowStart + ROW_ANCHOR_Y - 10 && y <= TITLE_BAR_HEIGHT + MediaRowStart + ROW_ANCHOR_Y + 10)
                    newMediaToggleHovered = true;

                // 「使用实况窗展示媒体」（首卡第二行，槽位与 DrawToggleRow 同源）
                if (!anyPopupOpen
                    && x >= LeftColumnRight - 62 && x <= LeftColumnRight - 14
                    && y >= TITLE_BAR_HEIGHT + MediaTopLeftRow2Y + ROW_ANCHOR_Y - TOGGLE_TRACK_H / 2f
                    && y <= TITLE_BAR_HEIGHT + MediaTopLeftRow2Y + ROW_ANCHOR_Y + TOGGLE_TRACK_H / 2f)
                    newMediaLiveToggleHovered = true;

                if (!anyPopupOpen && x >= PlatformBoxX && x <= PlatformBoxX + 110 && y >= PlatformBoxY && y <= PlatformBoxY + 32)
                    newDropdownHovered = true;

                if (_dropdownOpen)
                {
                    if (x >= PlatformBoxX && x <= PlatformBoxX + 110)
                    {
                        int row = DropdownRowAt(PlatformMenuY, _platforms.Length, y);
                        if (row >= 0) newHoveredDropdownIndex = row;
                    }
                }

                bool matchRowEnabled = MediaController.TargetPlatform == "other";
                newMatchModeDropdownHovered = !anyPopupOpen && matchRowEnabled
                    && x >= MATCH_MODE_X && x <= MATCH_MODE_X + MATCH_BOX_W && y >= MATCH_ROW_Y && y <= MATCH_ROW_Y + MATCH_BOX_H;
                newAppDropdownHovered = !anyPopupOpen && matchRowEnabled && MediaController.IsManualSessionMatch
                    && x >= MATCH_APP_X && x <= MATCH_APP_X + MATCH_BOX_W && y >= MATCH_ROW_Y && y <= MATCH_ROW_Y + MATCH_BOX_H;

                if (_matchModeDropdownOpen && x >= MATCH_MODE_X && x <= MATCH_MODE_X + MATCH_BOX_W)
                {
                    int row = DropdownRowAt(MATCH_MENU_Y, _matchModeOptions.Length, y);
                    if (row >= 0) newHoveredMatchModeIndex = row;
                }

                if (_appDropdownOpen)
                {
                    int appRows = Math.Clamp(_appOptions.Length, 1, 8);
                    float appMenuX = MATCH_MENU_RIGHT - APP_MENU_W;
                    if (x >= appMenuX && x <= MATCH_MENU_RIGHT)
                    {
                        int row = DropdownRowAt(MATCH_MENU_Y, appRows, y);
                        if (row >= 0) newHoveredAppIndex = row;
                    }
                }

                // 歌词设置卡片（坐标常量与 Render() 同源；四行统一 34px 行距）
                float lyricY = LyricCardContentTop;
                bool newLyricToggleHovered = !anyPopupOpen && (x >= LeftColumnRight - 62 && x <= LeftColumnRight - 14 && y >= lyricY + LYRIC_TOGGLE1_DY && y <= lyricY + LYRIC_TOGGLE1_DY + 20);
                bool newTransToggleHovered = !anyPopupOpen && (x >= LeftColumnRight - 62 && x <= LeftColumnRight - 14 && y >= lyricY + LYRIC_TOGGLE2_DY && y <= lyricY + LYRIC_TOGGLE2_DY + 20);
                bool newScanToggleHovered = !anyPopupOpen && (x >= LeftColumnRight - 62 && x <= LeftColumnRight - 14 && y >= lyricY + LYRIC_TOGGLE3_DY && y <= lyricY + LYRIC_TOGGLE3_DY + 20);

                // 延迟补偿按钮整行排在最后（歌词卡片里第四行）
                float btnY = lyricY + LYRIC_DELAY_DY;
                float cardRightX = LeftColumnRight - 16;
                bool newLyricMinusHovered = !anyPopupOpen && (x >= cardRightX - 175 && x <= cardRightX - 145 && y >= btnY && y <= btnY + LYRIC_BTN_H);
                bool newLyricPlusHovered = !anyPopupOpen && (x >= cardRightX - 80 && x <= cardRightX - 50 && y >= btnY && y <= btnY + LYRIC_BTN_H);
                bool newLyricResetHovered = !anyPopupOpen && (x >= cardRightX - 40 && x <= cardRightX && y >= btnY && y <= btnY + LYRIC_BTN_H);

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

                bool newHotkeyToggleHovered = HitMediaHotkeyToggle(x, y, anyPopupOpen);

                int newHoveredHotkeyRow = HitMediaHotkeyRow(x, y, anyPopupOpen);

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
                // 静默模式：自动隐藏整块 + 唤出灵动岛整卡一起置灰，所以这里一个 hover 都不置。
                bool silent = Renderer.SilentActive;
                // 行 1：自动隐藏总开关
                if (!silent && x >= LeftColumnRight - 62 && x <= LeftColumnRight - 14 && y >= TITLE_BAR_HEIGHT + 32 && y <= TITLE_BAR_HEIGHT + 52)
                    newAutoHideToggleHovered = true;
                // 行 2：当焦点离开时自动隐藏岛
                if (!silent && x >= LeftColumnRight - 62 && x <= LeftColumnRight - 14 && y >= TITLE_BAR_HEIGHT + 94 && y <= TITLE_BAR_HEIGHT + 114)
                    newFocusHideToggleHovered = true;
                // 行 3：暂停播放后自动隐藏
                if (!silent && x >= LeftColumnRight - 62 && x <= LeftColumnRight - 14 && y >= TITLE_BAR_HEIGHT + 156 && y <= TITLE_BAR_HEIGHT + 176)
                    newPauseHideToggleHovered = true;
                // 行 4：全屏自动隐藏
                if (!silent && x >= LeftColumnRight - 62 && x <= LeftColumnRight - 14 && y >= TITLE_BAR_HEIGHT + 218 && y <= TITLE_BAR_HEIGHT + 238)
                    newFsHideToggleHovered = true;
                if (x >= WIDTH - 80 && x <= WIDTH - 30 && y >= TITLE_BAR_HEIGHT + MEDIA_EXP_CARD_Y + 20 && y <= TITLE_BAR_HEIGHT + MEDIA_EXP_CARD_Y + 40)
                    newMediaExpToggleHovered = true;
                if (x >= WIDTH - 80 && x <= WIDTH - 30 && y >= TITLE_BAR_HEIGHT + APP_LAUNCH_CARD_Y + 20 && y <= TITLE_BAR_HEIGHT + APP_LAUNCH_CARD_Y + 40)
                    newAppLaunchToggleHovered = true;
                // 使用局部变量，防止状态死锁
                newPassToggleHovered = x >= LeftColumnRight - 62 && x <= LeftColumnRight - 14 && y >= TITLE_BAR_HEIGHT + PASS_CARD_Y + 20 && y <= TITLE_BAR_HEIGHT + PASS_CARD_Y + 40;

                // 「唤出灵动岛」：总开关在卡片头部右侧，键位框在下面那行
                //（y 值与 DrawIslandHotkeyCard 严格同源）
                newIslandHotkeyToggleHovered = !silent && HitIslandHotkeyToggle(x, y);
                newIslandHotkeyHovered = !silent && HitIslandHotkeyBox(x, y);

                // 「静默模式」快捷键卡：静默模式下也能用（正是靠它进出），所以这里不加 silent 条件，
                // 只受「唤出灵动岛」卡被禁用时那层视觉影响 —— 两张卡物理上不重叠。
                newSilentHotkeyToggleHovered = HitSilentHotkeyToggle(x, y);
                newSilentHotkeyHovered = HitSilentHotkeyBox(x, y);
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

                    // 「一键更新全部」条：几何与 RenderTabMarket / MarketUpdateBarRect 同源
                    newHoveredMarketUpdateAll = MarketUpdateCount > 0
                        && x >= CONTENT_TEXT_X && x <= WIDTH - CONTENT_TEXT_RM
                        && y >= MarketUpdateBarTop && y <= MarketUpdateBarTop + MarketUpdateBarH;

                    newHoveredMarketSortBox = x >= MarketSortX && x <= MarketSortX + MarketSortW
                        && y >= MarketStatusRowY && y <= MarketStatusRowY + MarketControlH;

                    newHoveredMarketChk = x >= MarketChkX - 4f
                        && x <= MarketChkLabelX + 64f
                        && y >= MarketStatusRowY && y <= MarketStatusRowY + MarketControlH;

                    bool overCatBtn = x >= CONTENT_TEXT_X && x <= CONTENT_TEXT_X + MarketCatBtnW
                        && y >= MarketControlsY && y <= MarketControlsY + MarketControlH;

                    if (_marketCategoryOpen)
                    {
                        float mY = MarketControlsY + MarketControlH + 4;
                        if (x >= CONTENT_TEXT_X && x <= CONTENT_TEXT_X + MarketCatBtnW)
                        {
                            int row = DropdownRowAt(mY, MarketCategories.Length, y);
                            if (row >= 0) newHoveredMarketCategoryIndex = row;
                        }
                    }
                    if (overCatBtn && newHoveredMarketCategoryIndex == -1 && !_marketCategoryOpen)
                        newHoveredMarketCategoryIndex = -2;

                    if (_marketSortOpen)
                    {
                        float sY = MarketStatusRowY + MarketControlH + 4;
                        if (x >= MarketSortX && x <= MarketSortX + MarketSortW)
                        {
                            int row = DropdownRowAt(sY, MarketSortOptions.Length, y);
                            if (row >= 0) newHoveredMarketSortIndex = row;
                        }
                    }
                    if (newHoveredMarketSortBox && newHoveredMarketSortIndex == -1 && !_marketSortOpen)
                        newHoveredMarketSortIndex = -2;
                }

                if (IsConfirmDialogOpen)
                {
                    var rect = GetCurrentDialogRect();
                    if (rect.Width > 0)
                    {
                        var close = GetMarketDialogCloseRect(rect);
                        close.Inflate(4f, 4f);
                        newHoveredDialogClose = close.Contains(x, y);
                        if (!newHoveredDialogClose) newHoveredDialogButton = HitDialogButton(rect, x, y);

                        // 失败弹窗正文里的群号：位置取自打开时排好的版面，与绘制同一份数据
                        if (!newHoveredDialogClose && IsNoticeDialog)
                            newLoadFailedLinkHovered = HitLoadFailedLink(rect, x, y);
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

                        if (!newHoveredDialogClose && _marketDialog == MarketDialog.Detail
                            && !string.Equals(_marketBusyId, mp.Id, StringComparison.Ordinal))
                        {
                            int hit = HitDialogButton(rect, x, y);
                            if (hit >= 0)
                            {
                                var local = MatchLocalPlugin(mp);
                                if (local == null || PluginManager.Instance.IsDisabled(local))
                                    newHoveredDialogButton = hit;
                            }
                        }

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
                else if (!_marketCategoryOpen && !_marketSortOpen)
                {
                    GetMarketListLayout(out int mRows, out int mMaxFirst);
                    _marketScroll = Math.Clamp(_marketScroll, 0, mMaxFirst);
                    int slot = HitPluginTile(MarketTileTop, x, y);
                    if (slot >= 0 && slot < mRows)
                    {
                        var rect = PluginTile(MarketTileTop, slot);
                        float actionY = rect.Top + MarketButtonsTop;
                        if (y >= actionY && y <= actionY + 20)
                        {
                            int idx = slot + _marketScroll;
                            var mp = _marketView[idx];
                            bool busy = string.Equals(_marketBusyId, mp.Id, StringComparison.Ordinal);
                            float detailX = rect.Right - 10f - MarketBtnW;
                            float uninstallX = detailX - 6f - MarketBtnW;
                            float installX = uninstallX - 6f - MarketBtnW;
                            if (x >= installX && x <= installX + MarketBtnW && !busy)
                                newHoveredMarketInstall = idx;
                            else if (x >= uninstallX && x <= uninstallX + MarketBtnW && !busy && MatchLocalPlugin(mp) != null)
                                newHoveredMarketUninstall = idx;
                            else if (x >= detailX && x <= detailX + MarketBtnW)
                                newHoveredMarketDetail = idx;
                        }
                    }
                }
            }
            else if (_selectedTab == 6) // 我的插件
            {
                if (_settingsPluginId != null)
                {
                    UpdatePluginSettingsHover(x, y, out newSettingsBackHovered,
                        out newHoveredSettingControl, out newHoveredSettingSegment);
                }
                else if (IsConfirmDialogOpen)
                {
                    var rect = GetCurrentDialogRect();
                    if (rect.Width > 0)
                    {
                        var close = GetMarketDialogCloseRect(rect);
                        close.Inflate(4f, 4f);
                        newHoveredDialogClose = close.Contains(x, y);
                        if (!newHoveredDialogClose) newHoveredDialogButton = HitDialogButton(rect, x, y);

                        // 失败弹窗正文里的群号：位置取自打开时排好的版面，与绘制同一份数据
                        if (!newHoveredDialogClose && IsNoticeDialog)
                            newLoadFailedLinkHovered = HitLoadFailedLink(rect, x, y);
                    }
                }
                else
                {
                    float topY = TITLE_BAR_HEIGHT + CARD_TOP_Y;
                    // 顶部操作按钮：导入插件 | 打开目录 | 插件市场
                    if (y >= topY + 60 && y <= topY + 84)
                    {
                        if (x >= CONTENT_TEXT_X && x <= CONTENT_TEXT_X + 96) newHoveredPluginAction = 0;       // 导入插件
                        else if (x >= CONTENT_TEXT_X + 104 && x <= CONTENT_TEXT_X + 200) newHoveredPluginAction = 1;  // 打开目录
                        else if (x >= CONTENT_TEXT_X + 208 && x <= CONTENT_TEXT_X + 304) newHoveredPluginAction = 2;  // 插件市场
                    }

                    GetPluginListCardTop(out float listY);
                    GetPluginListLayout(out int rows, out int pluginMaxFirst);
                    _pluginScroll = Math.Clamp(_pluginScroll, 0, pluginMaxFirst);
                    float rowsTop = listY + PluginRowsTopPad;
                    int slot = HitPluginTile(rowsTop, x, y);
                    if (slot >= 0 && slot < rows)
                    {
                        var rect = PluginTile(rowsTop, slot);
                        float actionY = rect.Top + PluginButtonsTop;
                        if (y >= actionY && y <= actionY + 20)
                        {
                            int idx = slot + _pluginScroll;
                            float settingsX = rect.Right - 224f;
                            if (x >= settingsX && x <= settingsX + 50) newHoveredPluginSettings = idx;
                            else if (x >= settingsX + 56 && x <= settingsX + 106) newHoveredPluginReload = idx;
                            else if (x >= settingsX + 112 && x <= settingsX + 162) newHoveredPluginRemove = idx;
                            else if (x >= settingsX + 168 && x <= settingsX + 210) newHoveredPluginToggle = idx;
                        }
                    }
                }
            }
            else if (_selectedTab == 8) // 实况窗管理
            {
                float mediaTop = LiveWindowMediaCardTop;
                if (x >= CONTENT_L && x <= LeftColumnRight && y >= mediaTop && y <= mediaTop + LW_MEDIA_CARD_H)
                    newLiveWindowMediaHovered = true;

                var owners = LiveWindowHost.KnownPluginOwners;
                if (owners.Count > 0 && x >= RightColumnLeft && x <= WIDTH - CONTENT_RM)
                {
                    float rowTop = LiveWindowPluginRowTop;
                    if (y >= rowTop)
                    {
                        int idx = (int)((y - rowTop) / LW_ROW_H);
                        if (idx >= 0 && idx < LiveWindowVisibleRows && idx + _liveWindowScroll < owners.Count)
                            newLiveWindowHoverRow = idx + _liveWindowScroll;
                    }
                }
            }
            else if (_selectedTab == 9) // 静默模式设置
            {
                // 右上角「去切换」：始终存在（静默模式下去显示设置切回来）。
                if (x >= SilentSwitchLeft && x <= DISPLAY_CTRL_RIGHT
                    && y >= SilentSwitchTop && y <= SilentSwitchTop + SILENT_SWITCH_H)
                    newSilentSwitchHovered = true;

                if (x >= CONTENT_L && x <= LeftColumnRight
                    && y >= SilentToastTop && y <= SilentToastTop + SILENT_TOAST_CARD_H)
                    newSilentToastHovered = true;

                if (x >= CONTENT_L && x <= LeftColumnRight
                    && y >= SilentMediaTop && y <= SilentMediaTop + SILENT_MEDIA_CARD_H)
                    newSilentMediaHovered = true;

                var items = NotchPeninsula.Plugins.PluginManager.Instance.SilentDisplayItems;
                int visibleRows = SilentListVisibleRows();
                _silentListScroll = Math.Clamp(_silentListScroll, 0, Math.Max(0, items.Count - visibleRows));

                float rowTop = TITLE_BAR_HEIGHT + SilentListCardY + SILENT_LIST_FIRST_ROW_Y;
                float rowBottom = TITLE_BAR_HEIGHT + SilentListCardY + SilentListCardH;
                if (items.Count > 0 && x >= DISPLAY_ITEM_L && x <= DISPLAY_ITEM_R && y >= rowTop && y <= rowBottom)
                {
                    int idx = _silentListScroll + (int)((y - rowTop) / SILENT_LIST_ROW_H);
                    if (idx < items.Count && idx < _silentListScroll + visibleRows)
                    {
                        // 列表里只有已声明重要信息的插件，全部可交互。
                        if (x >= DISPLAY_MOVE_UP_X && x <= DISPLAY_MOVE_UP_X + SORT_TRI_W) newHoveredSilentMoveUp = idx;
                        else if (x >= DISPLAY_MOVE_DOWN_X && x <= DISPLAY_MOVE_DOWN_X + SORT_TRI_W) newHoveredSilentMoveDown = idx;
                        else newHoveredSilentRow = idx;
                    }
                }
            }

            int newHoveredLinkIndex = HitAboutLink(x, y);

            // 侧边栏左下角的「导出诊断包」「清理解包缓存」常驻，不跟页签走 —— 用户在任何一页都能按。
            bool newAboutExportHovered = x >= EXPORT_BTN_X && x <= EXPORT_BTN_X + EXPORT_BTN_W
                                      && y >= EXPORT_BTN_Y && y <= EXPORT_BTN_Y + EXPORT_BTN_H;
            bool newCacheCleanHovered = x >= CACHE_BTN_X && x <= CACHE_BTN_X + CACHE_BTN_W
                                     && y >= CACHE_BTN_Y && y <= CACHE_BTN_Y + CACHE_BTN_H;

            bool newIsHoveringDisabledArea = false;
            // 否则就会出现「明明能点、却显示禁止指针」。
            // 左边界必须跟右列卡片对齐：从 CONTENT_L 起算的话，左列同高度的卡片
            //（通用页的 MSP 卡）也会被这层禁止指针盖住。
            if (x >= RightColumnLeft && x <= WIDTH - CONTENT_RM)
            {
                if (_selectedTab == 0 && !ToastSoundConfig.IsRowEnabled
                    && y >= TITLE_BAR_HEIGHT + SOUND_BOX_Y && y <= TITLE_BAR_HEIGHT + SOUND_BOX_Y + SOUND_ROW_H)
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
                newMediaToggleHovered != _mediaToggleHovered || newMediaLiveToggleHovered != _mediaLiveToggleHovered || newAutoHideToggleHovered != _autoHideToggleHovered ||
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
                newDisplayModeDropdownHovered != _displayModeDropdownHovered ||
                newHoveredDisplayModeIndex != _hoveredDisplayModeIndex ||
                newHoveredStandbySceneIndex != _hoveredSceneIndex ||
                newSceneDropdownHovered != _sceneDropdownHovered ||
                newStandbyToggleHovered != _standbyToggleHovered ||
                newStandbyRowHovered != _standbyRowHovered ||
                newPageScrollbarHovered != _pageScrollbarHovered ||
                newListScrollbarHovered != _listScrollbarHovered ||
                newHoveredDisplayRow != _hoveredDisplayRow ||
                newHoveredDisplayMoveUp != _hoveredDisplayMoveUp ||
                newHoveredDisplayMoveDown != _hoveredDisplayMoveDown ||
                newHoveredDisplayDrag != _hoveredDisplayDrag ||
                newHoveredSilentRow != _hoveredSilentRow ||
                newHoveredSilentMoveUp != _hoveredSilentMoveUp ||
                newHoveredSilentMoveDown != _hoveredSilentMoveDown ||
                newSilentSwitchHovered != _silentSwitchHovered ||
                newSilentToastHovered != _silentToastHovered ||
                newSilentMediaHovered != _silentMediaHovered ||
                newHoverMinus != _hoveredMinusIndex || newHoverPlus != _hoveredPlusIndex ||
                newHoverReset != _hoveredResetIndex || newMediaExpToggleHovered != _mediaExpToggleHovered ||
                newAppLaunchToggleHovered != _appLaunchToggleHovered ||
                newPersonalizeDropdown != _personalizeDropdownHovered ||
                newPersonalizeOption != _hoveredPersonalizeOption ||
                newHoveredGlassParam != _hoveredGlassParamIndex ||
                newHoveredGlassParamLevel != _hoveredGlassParamLevel ||
                newHoveredGlassReset != _hoveredGlassReset ||
                immersiveHovered != _immersiveToggleHovered ||
                // ⚠️ 这一项漏掉过：它只在被比较的几个状态里"顺带"赋值，
                //    于是鼠标只停在这一行时整块 if 不成立 → 悬停永远不更新，
                //    表现为「控件画得出来、高亮不亮、点了没反应」。
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
                newIslandHotkeyHovered != _islandHotkeyHovered ||
                newIslandHotkeyToggleHovered != _islandHotkeyToggleHovered ||
                newSilentHotkeyHovered != _silentHotkeyHovered ||
                newSilentHotkeyToggleHovered != _silentHotkeyToggleHovered ||
                newMspToggleHovered != _mspToggleHovered ||
                newMspWikiHovered != _mspWikiHovered ||
                newHoveredPluginAction != _hoveredPluginAction ||
                newHoveredPluginToggle != _hoveredPluginToggle ||
                newHoveredPluginReload != _hoveredPluginReload ||
                newHoveredPluginRemove != _hoveredPluginRemove ||
                newHoveredPluginSettings != _hoveredPluginSettings ||
                newSettingsBackHovered != _settingsBackHovered ||
                newHoveredSettingControl != _hoveredSettingControl ||
                newHoveredSettingSegment != _hoveredSettingSegment ||
                newLiveWindowMediaHovered != _liveWindowMediaHovered ||
                newLiveWindowHoverRow != _liveWindowHoverRow ||
                newHoveredMarketInstall != _hoveredMarketInstall ||
                newHoveredMarketUninstall != _hoveredMarketUninstall ||
                newHoveredMarketDetail != _hoveredMarketDetail ||
                newHoveredDialogClose != _hoveredDialogClose ||
                newHoveredDialogButton != _hoveredDialogButton ||
                newLoadFailedLinkHovered != _loadFailedLinkHovered ||
                newRateStars != _rateStars ||
                newHoveredMarketChk != _hoveredMarketChk ||
                newHoveredMarketCategoryIndex != _hoveredMarketCategoryIndex ||
                newMarketSearchHovered != _marketSearchHovered ||
                newMarketRefreshHovered != _hoveredMarketRefresh ||
                newHoveredMarketUpdateAll != _hoveredMarketUpdateAll ||
                newHoveredMarketSortIndex != _hoveredMarketSortIndex ||
                newHoveredMarketSortBox != _hoveredMarketSortBox ||
                newFontPickHovered != _fontPickHovered || newFontResetHovered != _fontResetHovered ||
                newAboutExportHovered != _aboutExportHovered
                || newCacheCleanHovered != _cacheCleanHovered
                )
            {
                _minHovered = newMinHovered; _closeHovered = newCloseHovered;
                _hoveredTab = newHoveredTab; _toggleHovered = newToggleHovered;
                _toastToggleHovered = newToastToggleHovered;
                _mediaToggleHovered = newMediaToggleHovered; _mediaLiveToggleHovered = newMediaLiveToggleHovered; _autoHideToggleHovered = newAutoHideToggleHovered;
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
                _aboutExportHovered = newAboutExportHovered;
                _cacheCleanHovered = newCacheCleanHovered;
                _hoveredDisplayRow = newHoveredDisplayRow;
                _hoveredDisplayMoveUp = newHoveredDisplayMoveUp;
                _hoveredDisplayMoveDown = newHoveredDisplayMoveDown;
                _hoveredDisplayDrag = newHoveredDisplayDrag;
                _hoveredSilentRow = newHoveredSilentRow;
                _hoveredSilentMoveUp = newHoveredSilentMoveUp;
                _hoveredSilentMoveDown = newHoveredSilentMoveDown;
                _silentSwitchHovered = newSilentSwitchHovered;
                _silentToastHovered = newSilentToastHovered;
                _silentMediaHovered = newSilentMediaHovered;
                _hoveredStyleIndex = newHoveredStyleIndex;
                _displayModeDropdownHovered = newDisplayModeDropdownHovered;
                _hoveredDisplayModeIndex = newHoveredDisplayModeIndex;
                _hoveredSceneIndex = newHoveredStandbySceneIndex;
                _sceneDropdownHovered = newSceneDropdownHovered;
                _standbyToggleHovered = newStandbyToggleHovered;
                _standbyRowHovered = newStandbyRowHovered;
                _pageScrollbarHovered = newPageScrollbarHovered;
                _listScrollbarHovered = newListScrollbarHovered;
                _hoveredMinusIndex = newHoverMinus;
                _hoveredPlusIndex = newHoverPlus;
                _hoveredResetIndex = newHoverReset;
                _personalizeDropdownHovered = newPersonalizeDropdown;
                _hoveredPersonalizeOption = newPersonalizeOption;
                _hoveredGlassParamIndex = newHoveredGlassParam;
                _hoveredGlassParamLevel = newHoveredGlassParamLevel;
                _hoveredGlassReset = newHoveredGlassReset;
                _immersiveToggleHovered = immersiveHovered;
                _mediaExpToggleHovered = newMediaExpToggleHovered;
                _appLaunchToggleHovered = newAppLaunchToggleHovered;
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
                _islandHotkeyHovered = newIslandHotkeyHovered;
                _islandHotkeyToggleHovered = newIslandHotkeyToggleHovered;
                _silentHotkeyHovered = newSilentHotkeyHovered;
                _silentHotkeyToggleHovered = newSilentHotkeyToggleHovered;
                _mspToggleHovered = newMspToggleHovered;
                _mspWikiHovered = newMspWikiHovered;
                _hoveredPluginAction = newHoveredPluginAction;
                _hoveredPluginToggle = newHoveredPluginToggle;
                _hoveredPluginReload = newHoveredPluginReload;
                _hoveredPluginRemove = newHoveredPluginRemove;
                _hoveredPluginSettings = newHoveredPluginSettings;
                _settingsBackHovered = newSettingsBackHovered;
                _hoveredSettingControl = newHoveredSettingControl;
                _hoveredSettingSegment = newHoveredSettingSegment;
                _liveWindowMediaHovered = newLiveWindowMediaHovered;
                _liveWindowHoverRow = newLiveWindowHoverRow;
                _hoveredMarketInstall = newHoveredMarketInstall;
                _hoveredMarketUninstall = newHoveredMarketUninstall;
                _hoveredMarketDetail = newHoveredMarketDetail;
                _hoveredDialogClose = newHoveredDialogClose;
                _hoveredDialogButton = newHoveredDialogButton;
                _loadFailedLinkHovered = newLoadFailedLinkHovered;
                if (newRateStars != _rateStars) _rateStars = newRateStars;
                _hoveredMarketChk = newHoveredMarketChk;
                _hoveredMarketCategoryIndex = newHoveredMarketCategoryIndex;
                _marketSearchHovered = newMarketSearchHovered;
                _hoveredMarketRefresh = newMarketRefreshHovered;
                _hoveredMarketUpdateAll = newHoveredMarketUpdateAll;
                _hoveredMarketSortIndex = newHoveredMarketSortIndex;
                _hoveredMarketSortBox = newHoveredMarketSortBox;
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
