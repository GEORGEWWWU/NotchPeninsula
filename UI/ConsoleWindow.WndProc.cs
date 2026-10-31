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
                if (!closeHovered && _marketDialog == MarketDialog.LoadFailed)
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

            int newHoveredTheme = -1;
            int newHoveredMaterial = -1;
            int newHoveredOpacityIndex = -1;
            int newHoveredGlassQuality = -1;      // 液态玻璃卡：质量分段控件
            int newHoveredGlassParam = -1;        // 9 根滑轨里悬停的是哪一根
            int newHoveredGlassParamLevel = -1;   // 悬停到的档位
            bool newHoveredGlassReset = false;    // 卡底「恢复默认参数」
            int newHoverMinus = -1, newHoverPlus = -1, newHoverReset = -1;
            if (_selectedTab == 5)
            {
                float themeSegY = TITLE_BAR_HEIGHT + THEME_SEG_Y;
                // 液态玻璃下不认这块：玻璃的明暗由背后的桌面决定，主题色只剩"文字反白"，摆着会误导
                if (!Renderer.IslandLiquidGlass
                    && y >= themeSegY && y <= themeSegY + SEG_H
                    && x >= THEME_SEG_X && x <= THEME_SEG_X + THEME_SEG_W)
                    newHoveredTheme = Math.Min(2, (int)((x - THEME_SEG_X) / (THEME_SEG_W / 3f)));

                float matSegY = TITLE_BAR_HEIGHT + MAT_SEG_Y;
                if (y >= matSegY && y <= matSegY + SEG_H
                    && x >= MAT_SEG_X && x <= MAT_SEG_X + MAT_SEG_W)
                    newHoveredMaterial = Math.Min(2, (int)((x - MAT_SEG_X) / (MAT_SEG_W / 3f)));

                float sliderY = TITLE_BAR_HEIGHT + OPACITY_CARD_Y + OPACITY_SLIDER_DY;
                float sliderX = CONTENT_TEXT_X;
                float sliderW = (WIDTH - CONTENT_TEXT_RM) - CONTENT_TEXT_X;

                if (ShowOpacityCard && !Renderer.PassthroughModeEnabled)
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

                // 液态玻璃卡：材质不是玻璃时整张卡都不画，命中也跟着关掉
                if (Renderer.IslandLiquidGlass)
                {
                    float gCardY = TITLE_BAR_HEIGHT + GLASS_CARD_Y;
                    float segY = gCardY + GLASS_SEG_Y;
                    if (y >= segY && y <= segY + SEG_H)
                    {
                        if (x >= GLASS_QUALITY_SEG_X && x <= GLASS_QUALITY_SEG_X + GLASS_QUALITY_SEG_W)
                            newHoveredGlassQuality = Math.Min(3, (int)((x - GLASS_QUALITY_SEG_X) / (GLASS_QUALITY_SEG_W / 4f)));
                    }

                    // 11 根滑轨：先定位行，再看落在哪一列
                    for (int row = 0; row < GLASS_ROWS; row++)
                    {
                        float ry = gCardY + GLASS_ROW0_Y + (row * GLASS_ROW_H) + 17f;
                        if (y < ry - 12 || y > ry + 12) continue;

                        for (int col = 0; col < 3; col++)
                        {
                            int idx = (int)(row * 3) + col;
                            if (idx >= LiquidGlass.ParamCount) continue;

                            float gx0 = GLASS_COL_X + col * (GLASS_COL_W + GLASS_COL_GAP) + GLASS_SLIDER_DX;
                            float gx1 = gx0 + GLASS_SLIDER_W;
                            if (x < gx0 - 16 || x > gx1 + 16) continue;

                            int lvl = (int)Math.Round((x - gx0) / (GLASS_SLIDER_W / LiquidGlass.MaxLevel));
                            if (lvl < 0) lvl = 0;
                            if (lvl > LiquidGlass.MaxLevel) lvl = LiquidGlass.MaxLevel;

                            newHoveredGlassParam = idx;
                            newHoveredGlassParamLevel = lvl;
                            if (dragging && LiquidGlass.GetLevel(idx) != lvl)
                            {
                                ApplyGlassLevel(idx, lvl);
                                Render();   // 与透明度滑轨同款：跨档立刻跟手
                            }
                            break;
                        }
                        break;
                    }

                    // 「恢复默认参数」（与质量分段同一行）：滑轨之后判，命中就把滑轨悬停清掉
                    float grY = segY + (SEG_H - GLASS_RESET_H) / 2f;
                    if (x >= GLASS_RESET_X && x <= GLASS_RESET_X + GLASS_RESET_W
                        && y >= grY && y <= grY + GLASS_RESET_H)
                    {
                        newHoveredGlassParam = -1;
                        newHoveredGlassParamLevel = -1;
                        newHoveredGlassReset = true;
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
            int newHoveredStandbySceneIndex = -1; // 待机显示内容下拉：列表项的悬停下标
            bool newSceneDropdownHovered = false; // 待机显示内容下拉：框本体的悬停
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
            bool newIslandHotkeyHovered = false;       // 交互页「唤出灵动岛」的键位框
            bool newIslandHotkeyToggleHovered = false; // 同一张卡的总开关
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
            int newHoveredMarketSortIndex = -1;   // 市场排序：菜单内悬停项；-2 = 框本体
            bool newHoveredMarketSortBox = false;
            int newHoveredPluginToggle = -1;
            int newHoveredPluginReload = -1;
            int newHoveredPluginRemove = -1;
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

                // MSP 接入：卡片行首 MSP_CARD_Y + 开关位移
                if (x >= WIDTH - 80 && x <= WIDTH - 30
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
                float mdX = DISPLAY_CTRL_RIGHT - MONITOR_DD_W;
                if (!_monitorDropdownOpen && x >= mdX && x <= DISPLAY_CTRL_RIGHT && y >= mdY && y <= mdY + MONITOR_DD_H)
                    newMonitorDropdownHovered = true;

                if (_monitorDropdownOpen)
                {
                    float listY = mdY + MONITOR_DD_H + 2f;
                    if (x >= mdX && x <= DISPLAY_CTRL_RIGHT && y >= listY && y < listY + _monitorOptions.Length * 26)
                        newHoveredMonitorDropdownIndex = (int)((y - listY) / 26);
                }

                // 目标显示器列表展开时它压住下面几行控件（列表 y 209 起，每项 26），
                // 被压住的控件必须一起停掉悬停，否则会有「悬停列表却亮了显示模式」的观感。
                bool monitorListOpen = _monitorDropdownOpen;

                float modeSegY = TITLE_BAR_HEIGHT + MODE_ROW_Y + (ROW_H - SEG_H) / 2f + page;
                if (!monitorListOpen && !_sceneDropdownOpen
                    && y >= modeSegY && y <= modeSegY + SEG_H && x >= MODE_SEG_X && x <= MODE_SEG_X + MODE_SEG_W)
                    newHoveredDisplayModeIndex = Math.Min(1, (int)((x - MODE_SEG_X) / (MODE_SEG_W / 2f)));

                // 待机显示内容下拉框：展开期间框本体不再吃悬停，交给下面的列表项
                float sceneBoxY = TITLE_BAR_HEIGHT + SCENE_ROW_Y + (ROW_H - SCENE_DD_H) / 2f + page;
                if (!monitorListOpen && !_sceneDropdownOpen
                    && x >= SCENE_SEG_X && x <= SCENE_SEG_X + SCENE_SEG_W
                    && y >= sceneBoxY && y <= sceneBoxY + SCENE_DD_H)
                    newSceneDropdownHovered = true;

                if (_sceneDropdownOpen)
                {
                    GetSceneMenuLayout(out float sceneMenuTop, out int sceneVisible, out int sceneMaxFirst);
                    int sceneFirst = Math.Clamp(_sceneScroll, 0, sceneMaxFirst);
                    if (x >= SCENE_SEG_X && x <= SCENE_SEG_X + SCENE_SEG_W
                        && y >= sceneMenuTop && y < sceneMenuTop + sceneVisible * DROPDOWN_ROW_H)
                        newHoveredStandbySceneIndex = sceneFirst + (int)((y - sceneMenuTop) / DROPDOWN_ROW_H);
                }

                bool sceneListOpen = _sceneDropdownOpen;

                float modeToggleCy = TITLE_BAR_HEIGHT + TOGGLE_ROW_Y + page + ROW_ANCHOR_Y;
                if (!monitorListOpen && !sceneListOpen && x >= WIDTH - 80 && x <= WIDTH - 30
                    && y >= modeToggleCy - TOGGLE_TRACK_H / 2f && y <= modeToggleCy + TOGGLE_TRACK_H / 2f)
                    newStandbyToggleHovered = true;

                GetDisplayListLayout(out int displayVisibleRows, out int displayMaxFirstRow);
                _displayScroll = Math.Clamp(_displayScroll, 0, displayMaxFirstRow);
                float displayRowTop = TITLE_BAR_HEIGHT + DISPLAY_CARD_Y + DISPLAY_FIRST_ROW_Y + page;
                float displayRowBottom = TITLE_BAR_HEIGHT + DISPLAY_CARD_Y + DISPLAY_CARD_H + page;
                if (!Renderer.StandbyActive && !monitorListOpen && !sceneListOpen
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

                if (!sceneListOpen && GetDisplayPageMaxScroll() > 0f)
                {
                    GetPageScrollbarLayout(out float pageBarTop, out float pageBarH);
                    if (x >= WIDTH - 14 && x <= WIDTH && y >= pageBarTop && y <= pageBarTop + pageBarH)
                        newPageScrollbarHovered = true;
                }

                GetListScrollbarLayout(out float listBarTop, out float listBarH);
                if (!sceneListOpen && listBarH > 0f && ScrollBarAlpha() > 0.01f
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

                // 「唤出灵动岛」：总开关在卡片头部右侧，键位框在下面那行
                //（y 值与 DrawIslandHotkeyCard 严格同源）
                newIslandHotkeyToggleHovered = HitIslandHotkeyToggle(x, y);
                newIslandHotkeyHovered = HitIslandHotkeyBox(x, y);
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
                        const float rowH = 26f;
                        float mY = MarketControlsY + MarketControlH + 4;
                        if (x >= CONTENT_TEXT_X && x <= CONTENT_TEXT_X + MarketCatBtnW && y >= mY && y < mY + MarketCategories.Length * rowH)
                            newHoveredMarketCategoryIndex = (int)((y - mY) / rowH);
                    }
                    if (overCatBtn && newHoveredMarketCategoryIndex == -1 && !_marketCategoryOpen)
                        newHoveredMarketCategoryIndex = -2;

                    if (_marketSortOpen)
                    {
                        const float sortRowH = 26f;
                        float sY = MarketStatusRowY + MarketControlH + 4;
                        if (x >= MarketSortX && x <= MarketSortX + MarketSortW && y >= sY && y < sY + MarketSortOptions.Length * sortRowH)
                            newHoveredMarketSortIndex = (int)((y - sY) / sortRowH);
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
                        if (!newHoveredDialogClose && _marketDialog == MarketDialog.LoadFailed)
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

                        // 详情弹窗右下角有「安装 / 已安装」按钮：悬停反馈和其它弹窗按钮同一条路。
                        if (!newHoveredDialogClose && _marketDialog == MarketDialog.Detail)
                            newHoveredDialogButton = HitDialogButton(rect, x, y);

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
                        if (!newHoveredDialogClose && _marketDialog == MarketDialog.LoadFailed)
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
                    if (x >= CONTENT_TEXT_X && x <= WIDTH - CONTENT_TEXT_RM && y >= listY + PluginRowsTopPad)
                    {
                        int slot = (int)((y - (listY + PluginRowsTopPad)) / PluginListRowH);
                        int idx = slot + _pluginScroll;
                        if (slot >= 0 && slot < rows)
                        {
                            float rowY = listY + PluginRowsTopPad + slot * PluginListRowH;
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

            // 侧边栏左下角的「导出诊断包」常驻，不跟页签走 —— 用户在任何一页都能按。
            bool newAboutExportHovered = x >= EXPORT_BTN_X && x <= EXPORT_BTN_X + EXPORT_BTN_W
                                      && y >= EXPORT_BTN_Y && y <= EXPORT_BTN_Y + EXPORT_BTN_H;

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
                newHoveredStandbySceneIndex != _hoveredSceneIndex ||
                newSceneDropdownHovered != _sceneDropdownHovered ||
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
                newHoveredMaterial != _hoveredMaterialIndex ||
                newHoveredGlassQuality != _hoveredGlassQualityIndex ||
                newHoveredGlassParam != _hoveredGlassParamIndex ||
                newHoveredGlassParamLevel != _hoveredGlassParamLevel ||
                newHoveredGlassReset != _hoveredGlassReset ||
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
                newMspToggleHovered != _mspToggleHovered ||
                newMspWikiHovered != _mspWikiHovered ||
                newHoveredPluginAction != _hoveredPluginAction ||
                newHoveredPluginToggle != _hoveredPluginToggle ||
                newHoveredPluginReload != _hoveredPluginReload ||
                newHoveredPluginRemove != _hoveredPluginRemove ||
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
                _aboutExportHovered = newAboutExportHovered;
                _hoveredDisplayRow = newHoveredDisplayRow;
                _hoveredDisplayMoveUp = newHoveredDisplayMoveUp;
                _hoveredDisplayMoveDown = newHoveredDisplayMoveDown;
                _hoveredStyleIndex = newHoveredStyleIndex;
                _hoveredDisplayModeIndex = newHoveredDisplayModeIndex;
                _hoveredSceneIndex = newHoveredStandbySceneIndex;
                _sceneDropdownHovered = newSceneDropdownHovered;
                _standbyToggleHovered = newStandbyToggleHovered;
                _pageScrollbarHovered = newPageScrollbarHovered;
                _listScrollbarHovered = newListScrollbarHovered;
                _hoveredMinusIndex = newHoverMinus;
                _hoveredPlusIndex = newHoverPlus;
                _hoveredResetIndex = newHoverReset;
                _hoveredThemeIndex = newHoveredTheme;
                _hoveredMaterialIndex = newHoveredMaterial;
                _hoveredGlassQualityIndex = newHoveredGlassQuality;
                _hoveredGlassParamIndex = newHoveredGlassParam;
                _hoveredGlassParamLevel = newHoveredGlassParamLevel;
                _hoveredGlassReset = newHoveredGlassReset;
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
                _islandHotkeyHovered = newIslandHotkeyHovered;
                _islandHotkeyToggleHovered = newIslandHotkeyToggleHovered;
                _mspToggleHovered = newMspToggleHovered;
                _mspWikiHovered = newMspWikiHovered;
                _hoveredPluginAction = newHoveredPluginAction;
                _hoveredPluginToggle = newHoveredPluginToggle;
                _hoveredPluginReload = newHoveredPluginReload;
                _hoveredPluginRemove = newHoveredPluginRemove;
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
