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
        // 鼠标移动：整窗悬停命中（各页签热区）+ 悬停状态统一提交。
        //
        // ⚠️ 这段刻意保持「先收集 newXxx 局部变量、最后一次性提交到字段、只 Render() 一次」的写法：
        //    中途直接写字段会让每帧多触发好几次重绘，拖拽时掉帧。
        //    因此它是一个整体，不要再按页签拆开。
        //
        // dragging = 左键按住中（原 wParam 的 MK_LBUTTON，0x0001），只有透明度滑轨会用到。
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
            else if (x >= 10 && x <= 170 && y >= TITLE_BAR_HEIGHT + 230 && y <= TITLE_BAR_HEIGHT + 266) newHoveredTab = 6; // 6. 插件中心
            else if (x >= 10 && x <= 170 && y >= TITLE_BAR_HEIGHT + 280 && y <= TITLE_BAR_HEIGHT + 316) newHoveredTab = 4; // 7. 关于软件

            int newHoveredTheme = -1;
            int newHoveredOpacityIndex = -1;
            int newHoverMinus = -1, newHoverPlus = -1, newHoverReset = -1;
            if (_selectedTab == 5)
            {
                // 避免和下面的 rightX 冲突，改名为 themeRightX
                float themeRightX = WIDTH - 36;
                float themeY = GetBtnY(-1);

                // 主题按钮的三个胶囊热区
                if (x >= themeRightX - 140 && x <= themeRightX - 100 && y >= themeY && y <= themeY + 24) newHoveredTheme = 0;
                if (x >= themeRightX - 90 && x <= themeRightX - 50 && y >= themeY && y <= themeY + 24) newHoveredTheme = 1;
                if (x >= themeRightX - 40 && x <= themeRightX && y >= themeY && y <= themeY + 24) newHoveredTheme = 2;

                // 透明度滑块热区判定与拖拽滑动逻辑
                float sliderY = TITLE_BAR_HEIGHT + 95;
                float sliderX = 216;
                float sliderW = WIDTH - 40 - 216;

                if (!Renderer.PassthroughModeEnabled)
                {
                    // 放宽 Y 轴的判定区域，提升拖拽时的手感，防止手抖断触
                    if (x >= sliderX - 20 && x <= sliderX + sliderW + 20 && y >= sliderY - 20 && y <= sliderY + 20)
                    {
                        newHoveredOpacityIndex = (int)Math.Round((x - sliderX) / (sliderW / 4));
                        if (newHoveredOpacityIndex < 0) newHoveredOpacityIndex = 0;
                        if (newHoveredOpacityIndex > 4) newHoveredOpacityIndex = 4;

                        // 核心滑动逻辑：判断此时鼠标左键是否处于“按住”状态 (MK_LBUTTON = 0x0001)
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

                for (int i = 0; i < 8; i++)
                {
                    float btnY = GetBtnY(i);
                    float rightX = WIDTH - 36; // 保持原有变量不动
                    if (x >= rightX - 175 && x <= rightX - 145 && y >= btnY && y <= btnY + 24) newHoverMinus = i;
                    if (x >= rightX - 80 && x <= rightX - 50 && y >= btnY && y <= btnY + 24) newHoverPlus = i;
                    if (x >= rightX - 40 && x <= rightX && y >= btnY && y <= btnY + 24) newHoverReset = i;
                }
            }

            int newHoveredDisplayOptionIndex = -1;
            int newHoveredStyleIndex = -1;
            bool newToggleHovered = false;
            bool newToastToggleHovered = false;
            bool newTopmostToggleHovered = false;
            bool newMediaToggleHovered = false;
            bool newAutoHideToggleHovered = false;
            bool newPauseHideToggleHovered = false; // 「暂停播放后自动隐藏」
            bool newFsHideToggleHovered = false;    // 「全屏自动隐藏」
            bool newDropdownHovered = false;
            int newHoveredDropdownIndex = -1;
            bool newMatchModeDropdownHovered = false;
            int newHoveredMatchModeIndex = -1;
            bool newAppDropdownHovered = false;
            int newHoveredAppIndex = -1;
            bool newMediaExpToggleHovered = false;
            bool newPassToggleHovered = false;
            bool newClipboardToggleHovered = false;
            bool newMonitorDropdownHovered = false;
            int newHoveredMonitorDropdownIndex = -1;
            bool newToastModeDropdownHovered = false;
            int newHoveredToastModeIndex = -1;
            bool newCompositeToggleHover = false;
            bool newCompDateTimeHover = false;
            bool newCompHardwareHover = false;
            bool newCompMediaHover = false;
            int newHoveredPluginAction = -1;
            int newHoveredPluginToggle = -1;
            int newHoveredPluginReload = -1;
            int newHoveredPluginRemove = -1;
            int newHoveredPluginMoveLeft = -1;
            int newHoveredPluginMoveRight = -1;
            bool newFontPickHovered = false;
            bool newFontResetHovered = false;

            if (_selectedTab == 0) // 通用设置
            {
                // ⚠️ 本段的 y 值必须与下面 tab 0 的渲染保持同步
                //    （通知卡片是两行高：行1 开关 +176..+196、行2 下拉 +233..+265；
                //      剪贴板卡片 +310；字体按钮见 FONT_BTN_Y）
                // 开机自启
                if (x >= WIDTH - 80 && x <= WIDTH - 30 && y >= TITLE_BAR_HEIGHT + 32 && y <= TITLE_BAR_HEIGHT + 52)
                    newToggleHovered = true;
                // 窗口置顶开关
                if (x >= WIDTH - 80 && x <= WIDTH - 30 && y >= TITLE_BAR_HEIGHT + 104 && y <= TITLE_BAR_HEIGHT + 124)
                    newTopmostToggleHovered = true;

                // 🔔 通知卡片第 1 行：系统消息通知开关
                if (x >= WIDTH - 80 && x <= WIDTH - 30 && y >= TITLE_BAR_HEIGHT + 176 && y <= TITLE_BAR_HEIGHT + 196)
                    newToastToggleHovered = true;
                // 🔔 通知卡片第 2 行：消息通知内容下拉（复用现有下拉控件样式）
                if (!_toastModeDropdownOpen && x >= WIDTH - 140 && x <= WIDTH - 30 && y >= TITLE_BAR_HEIGHT + 233 && y <= TITLE_BAR_HEIGHT + 265)
                    newToastModeDropdownHovered = true;
                if (_toastModeDropdownOpen)
                {
                    if (x >= WIDTH - 140 && x <= WIDTH - 30 && y >= TITLE_BAR_HEIGHT + 267 && y < TITLE_BAR_HEIGHT + 267 + _toastModeOptions.Length * 26)
                        newHoveredToastModeIndex = (y - (TITLE_BAR_HEIGHT + 267)) / 26;
                }

                // 📋 剪贴板链接检测（2026-09-20 从「交互设置」搬到这里）
                if (x >= WIDTH - 80 && x <= WIDTH - 30 && y >= TITLE_BAR_HEIGHT + 310 && y <= TITLE_BAR_HEIGHT + 330)
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
                // 刘海形态选择器的点击热区
                float styleY = TITLE_BAR_HEIGHT + 50;
                if (x >= 220 && x <= 370 && y >= styleY && y <= styleY + 90) newHoveredStyleIndex = 0;
                if (x >= 390 && x <= 540 && y >= styleY && y <= styleY + 90) newHoveredStyleIndex = 1;

                // 目标显示器卡片
                float mdY = TITLE_BAR_HEIGHT + 186;
                if (!_monitorDropdownOpen && x >= WIDTH - 140 && x <= WIDTH - 30 && y >= mdY && y <= mdY + 32)
                    newMonitorDropdownHovered = true;

                if (_monitorDropdownOpen)
                {
                    float listY = TITLE_BAR_HEIGHT + 220;
                    if (x >= WIDTH - 140 && x <= WIDTH - 30 && y >= listY && y < listY + _monitorOptions.Length * 26)
                        newHoveredMonitorDropdownIndex = (y - (int)listY) / 26;
                }

                // 待机显示内容卡片 (1/3 布局)
                float displayOptY = TITLE_BAR_HEIGHT + 306;
                if (x >= 220 && x <= 330 && y >= displayOptY && y <= displayOptY + 40) newHoveredDisplayOptionIndex = 2; // 硬件占用
                if (x >= 340 && x <= 450 && y >= displayOptY && y <= displayOptY + 40) newHoveredDisplayOptionIndex = 0; // 时间日期
                if (x >= 460 && x <= 570 && y >= displayOptY && y <= displayOptY + 40) newHoveredDisplayOptionIndex = 1; // 空白

                // 组合模式hover判定
                float compCardY = TITLE_BAR_HEIGHT + 372;
                newCompositeToggleHover = x >= WIDTH - 80 && x <= WIDTH - 30 && y >= compCardY + 72 && y <= compCardY + 92;
                if (Renderer.CompositeModeEnabled)
                { // 状态拦截，防止关闭时产生幽灵悬停
                    newCompDateTimeHover = x >= 216 && x <= 350 && y >= compCardY + 105 && y <= compCardY + 121;
                    newCompHardwareHover = x >= 216 && x <= 350 && y >= compCardY + 140 && y <= compCardY + 156;
                    newCompMediaHover = x >= 216 && x <= 380 && y >= compCardY + 175 && y <= compCardY + 191;
                }

            }
            if (_selectedTab == 1 && !Renderer.CompositeModeEnabled)
            {
                float displayOptY = TITLE_BAR_HEIGHT + 306;
                if (x >= 220 && x <= 330 && y >= displayOptY && y <= displayOptY + 40) newHoveredDisplayOptionIndex = 2;
                if (x >= 340 && x <= 450 && y >= displayOptY && y <= displayOptY + 40) newHoveredDisplayOptionIndex = 0;
                if (x >= 460 && x <= 570 && y >= displayOptY && y <= displayOptY + 40) newHoveredDisplayOptionIndex = 1;
            }
            else if (_selectedTab == 2) // 媒体设置
            {
                // 任意下拉展开时，底层控件一律不吃悬停，避免浮窗底下的按钮被误触
                bool anyPopupOpen = _dropdownOpen || _matchModeDropdownOpen || _appDropdownOpen;

                // 媒体控制
                if (!anyPopupOpen && x >= WIDTH - 80 && x <= WIDTH - 30 && y >= TITLE_BAR_HEIGHT + 32 && y <= TITLE_BAR_HEIGHT + 52)
                    newMediaToggleHovered = true;

                // 下拉菜单（合并卡片第 1 行「目标媒体平台」；框体 +96..+128，命中内缩 2px）
                // ⚠️ 必须与 Render() 里 tab 2 的 `dY = TITLE_BAR_HEIGHT + 96` / `dH = 32` 保持同步
                if (!anyPopupOpen && x >= WIDTH - 140 && x <= WIDTH - 30 && y >= TITLE_BAR_HEIGHT + 98 && y <= TITLE_BAR_HEIGHT + 128)
                    newDropdownHovered = true;

                if (_dropdownOpen)
                {
                    if (x >= WIDTH - 140 && x <= WIDTH - 30 && y >= TITLE_BAR_HEIGHT + 130 && y < TITLE_BAR_HEIGHT + 130 + _platforms.Length * 26)
                        newHoveredDropdownIndex = (y - (TITLE_BAR_HEIGHT + 130)) / 26;
                }

                // 匹配方式（合并卡片第 2 行）：仅通用媒体可选；右框只在手动模式下可选
                // 坐标全部来自 MATCH_ROW_Y / MATCH_MENU_Y，与 Render() 同源
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
                bool newKaraokeToggleHovered = !anyPopupOpen && (x >= WIDTH - 80 && x <= WIDTH - 30 && y >= lyricY + 117 && y <= lyricY + 137);

                // 延迟补偿按钮整行排在最后（歌词卡片里第四行）
                float btnY = lyricY + 147;
                float cardRightX = WIDTH - 36;
                bool newLyricMinusHovered = !anyPopupOpen && (x >= cardRightX - 175 && x <= cardRightX - 145 && y >= btnY && y <= btnY + 24);
                bool newLyricPlusHovered = !anyPopupOpen && (x >= cardRightX - 80 && x <= cardRightX - 50 && y >= btnY && y <= btnY + 24);
                bool newLyricResetHovered = !anyPopupOpen && (x >= cardRightX - 40 && x <= cardRightX && y >= btnY && y <= btnY + 24);

                if (newLyricToggleHovered != _lyricToggleHovered || newTransToggleHovered != _transToggleHovered || newKaraokeToggleHovered != _karaokeToggleHovered || newLyricMinusHovered != _lyricMinusHovered || newLyricPlusHovered != _lyricPlusHovered || newLyricResetHovered != _lyricResetHovered)
                {
                    _lyricToggleHovered = newLyricToggleHovered;
                    _transToggleHovered = newTransToggleHovered;
                    _karaokeToggleHovered = newKaraokeToggleHovered;
                    _lyricMinusHovered = newLyricMinusHovered;
                    _lyricPlusHovered = newLyricPlusHovered;
                    _lyricResetHovered = newLyricResetHovered;
                    Render();
                }
            }
            else if (_selectedTab == 3) // 交互设置
            {
                // ⚠️ 本段的 y 值必须与下面 tab 3 的渲染保持同步
                //    （自动隐藏卡片是三行高：行1 +32、行2 +94、行3 +156；其余两张卡 +228 / +300）
                // 自动隐藏（穿透模式下禁止）
                if (!Renderer.PassthroughModeEnabled && x >= WIDTH - 80 && x <= WIDTH - 30 && y >= TITLE_BAR_HEIGHT + 32 && y <= TITLE_BAR_HEIGHT + 52)
                    newAutoHideToggleHovered = true;
                // 🎵 暂停播放后自动隐藏（自动隐藏卡片的第二行）
                // 可用性 = 自动隐藏已开启 且 非穿透模式；不可用时不给 hover（避免「灰着还能点」）
                if (!Renderer.PassthroughModeEnabled && NotchWindow.IsAutoHideEnabled
                    && x >= WIDTH - 80 && x <= WIDTH - 30 && y >= TITLE_BAR_HEIGHT + 94 && y <= TITLE_BAR_HEIGHT + 114)
                    newPauseHideToggleHovered = true;
                // 🖥 全屏自动隐藏（自动隐藏卡片的第三行）——可用性同上
                if (!Renderer.PassthroughModeEnabled && NotchWindow.IsAutoHideEnabled
                    && x >= WIDTH - 80 && x <= WIDTH - 30 && y >= TITLE_BAR_HEIGHT + 156 && y <= TITLE_BAR_HEIGHT + 176)
                    newFsHideToggleHovered = true;
                // 媒体交互模式
                if (!Renderer.CompositeModeEnabled && x >= WIDTH - 80 && x <= WIDTH - 30 && y >= TITLE_BAR_HEIGHT + 228 && y <= TITLE_BAR_HEIGHT + 248)
                    newMediaExpToggleHovered = true;
                // 使用局部变量，防止状态死锁
                newPassToggleHovered = x >= WIDTH - 80 && x <= WIDTH - 30 && y >= TITLE_BAR_HEIGHT + 300 && y <= TITLE_BAR_HEIGHT + 320;
            }
            else if (_selectedTab == 6) // 插件中心
            {
                float topY = TITLE_BAR_HEIGHT + 12;
                // 三个操作按钮
                if (y >= topY + 60 && y <= topY + 84)
                {
                    if (x >= 216 && x <= 312) newHoveredPluginAction = 0;       // 导入 DLL
                    else if (x >= 320 && x <= 416) newHoveredPluginAction = 1;  // 打开目录
                    else if (x >= 424 && x <= 520) newHoveredPluginAction = 2;  // 插件市场
                }

                // 列表行内按钮（全部在下行：← → 排序 | 重载 | 移除 | 开关）
                // 上行（名称）无交互目标，仅下行按钮可点击
                float listY = topY + 110;
                int rows = Math.Min(_pluginView.Count, 7);
                // ⚠️ 这里的行起点必须与 Render() 里的 `listY + 64` 严格一致
                //    （2026-09-20 加「顺序：…」那一行时把渲染下移了 20px，热区漏改 →
                //      整行交互热区整体上移 20px，按钮全部点不到）
                if (x >= 216 && x <= WIDTH - 36 && y >= listY + 64)
                {
                    int idx = (int)((y - (listY + 64)) / 56);
                    if (idx >= 0 && idx < rows)
                    {
                        float rowY = listY + 64 + idx * 56;
                        // 下行按钮区（rowY+22 .. rowY+48）
                        if (y >= rowY + 22 && y <= rowY + 48)
                        {
                            // 从左到右：排序 ← → | 重载 | 移除 | 开关
                            if (x >= PLUGIN_SORT_LEFT_X && x <= PLUGIN_SORT_LEFT_X + SORT_TRI_W) newHoveredPluginMoveLeft = idx;
                            else if (x >= PLUGIN_SORT_RIGHT_X && x <= PLUGIN_SORT_RIGHT_X + SORT_TRI_W) newHoveredPluginMoveRight = idx;
                            else if (x >= PLUGIN_BTN_RELOAD_X && x <= PLUGIN_BTN_RELOAD_X + 50) newHoveredPluginReload = idx;
                            else if (x >= PLUGIN_BTN_REMOVE_X && x <= PLUGIN_BTN_REMOVE_X + 50) newHoveredPluginRemove = idx;
                            else if (x >= PLUGIN_BTN_TOGGLE_X && x <= PLUGIN_BTN_TOGGLE_X + 42) newHoveredPluginToggle = idx;
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
            // 「禁止」指针区域 —— 判据必须与 Render() 里对应卡片的 disabled **完全同源**，
            // 否则就会出现「明明能点、却显示禁止指针」。
            // ⚠️ 这些 y 区间是手写的，卡片一挪动就必须同步改（踩过一次：
            //    自动隐藏卡片从两行加高到三行后，「媒体交互方式」卡片从 yOffset 84 挪到了 208，
            //    这里没跟着改，禁止区域就压在了「暂停播放后自动隐藏」那一行上 ——
            //    导致不管该开关是否被禁用，hover 上去都是禁止指针）。
            if (x >= 200 && x <= WIDTH - 20)
            {
                if (_selectedTab == 1 && Renderer.CompositeModeEnabled
                    && y >= TITLE_BAR_HEIGHT + 248 && y <= TITLE_BAR_HEIGHT + 360)  // 待机显示内容卡片
                {
                    newIsHoveringDisabledArea = true;
                }
                else if (_selectedTab == 3)
                {
                    // 与 Render() tab 3 的 disabled 判据同源（改那边记得改这边）
                    bool autoHideDisabled = Renderer.PassthroughModeEnabled;
                    bool subToggleDisabled = autoHideDisabled || !NotchWindow.IsAutoHideEnabled;

                    if (Renderer.CompositeModeEnabled
                        && y >= TITLE_BAR_HEIGHT + 208 && y <= TITLE_BAR_HEIGHT + 270)  // 媒体交互方式卡片
                        newIsHoveringDisabledArea = true;
                    else if (autoHideDisabled
                        && y >= TITLE_BAR_HEIGHT + 12 && y <= TITLE_BAR_HEIGHT + 198)   // 自动隐藏卡片整卡（穿透模式下三行全禁用）
                        newIsHoveringDisabledArea = true;
                    else if (subToggleDisabled
                        && y >= TITLE_BAR_HEIGHT + 74 && y <= TITLE_BAR_HEIGHT + 198)   // 两个附属开关行（需先开启「自动隐藏」）
                        newIsHoveringDisabledArea = true;
                }
            }

            if (newIsHoveringDisabledArea != _isHoveringDisabledArea) _isHoveringDisabledArea = newIsHoveringDisabledArea;

            if (newMinHovered != _minHovered || newCloseHovered != _closeHovered ||
                newHoveredTab != _hoveredTab || newToggleHovered != _toggleHovered ||
                newToastToggleHovered != _toastToggleHovered || newTopmostToggleHovered != _topmostToggleHovered ||
                newMediaToggleHovered != _mediaToggleHovered || newAutoHideToggleHovered != _autoHideToggleHovered ||
                newPauseHideToggleHovered != _pauseHideToggleHovered ||
                newFsHideToggleHovered != _fsHideToggleHovered ||
                newDropdownHovered != _dropdownHovered ||
                newMatchModeDropdownHovered != _matchModeDropdownHovered ||
                newHoveredMatchModeIndex != _hoveredMatchModeIndex ||
                newAppDropdownHovered != _appDropdownHovered ||
                newHoveredAppIndex != _hoveredAppIndex ||
                newHoveredDropdownIndex != _hoveredDropdownIndex || newHoveredLinkIndex != _hoveredLinkIndex ||
                newHoveredDisplayOptionIndex != _hoveredDisplayOptionIndex ||
                newHoveredStyleIndex != _hoveredStyleIndex ||
                newHoverMinus != _hoveredMinusIndex || newHoverPlus != _hoveredPlusIndex ||
                newHoverReset != _hoveredResetIndex || newMediaExpToggleHovered != _mediaExpToggleHovered ||
                newHoveredTheme != _hoveredThemeIndex || newHoveredOpacityIndex != _hoveredOpacityIndex ||
                newMonitorDropdownHovered != _monitorDropdownHovered ||
                newHoveredMonitorDropdownIndex != _hoveredMonitorDropdownIndex ||
                newToastModeDropdownHovered != _toastModeDropdownHovered ||
                newHoveredToastModeIndex != _hoveredToastModeIndex ||
                newCompositeToggleHover != _compositeToggleHovered ||
                newCompDateTimeHover != _compDateTimeHovered ||
                newCompHardwareHover != _compHardwareHovered ||
                newCompMediaHover != _compMediaHovered || newPassToggleHovered != _passToggleHovered ||
                newClipboardToggleHovered != _clipboardToggleHovered ||
                newHoveredPluginAction != _hoveredPluginAction ||
                newHoveredPluginToggle != _hoveredPluginToggle ||
                newHoveredPluginReload != _hoveredPluginReload ||
                newHoveredPluginRemove != _hoveredPluginRemove ||
                newHoveredPluginMoveLeft != _hoveredPluginMoveLeft ||
                newHoveredPluginMoveRight != _hoveredPluginMoveRight ||
                newFontPickHovered != _fontPickHovered || newFontResetHovered != _fontResetHovered
                )
            {
                _minHovered = newMinHovered; _closeHovered = newCloseHovered;
                _hoveredTab = newHoveredTab; _toggleHovered = newToggleHovered;
                _toastToggleHovered = newToastToggleHovered;
                _mediaToggleHovered = newMediaToggleHovered; _autoHideToggleHovered = newAutoHideToggleHovered;
                _pauseHideToggleHovered = newPauseHideToggleHovered;
                _fsHideToggleHovered = newFsHideToggleHovered;
                _dropdownHovered = newDropdownHovered;
                _hoveredDropdownIndex = newHoveredDropdownIndex;
                _matchModeDropdownHovered = newMatchModeDropdownHovered;
                _hoveredMatchModeIndex = newHoveredMatchModeIndex;
                _appDropdownHovered = newAppDropdownHovered;
                _hoveredAppIndex = newHoveredAppIndex;
                _hoveredLinkIndex = newHoveredLinkIndex;
                _hoveredDisplayOptionIndex = newHoveredDisplayOptionIndex;
                _hoveredStyleIndex = newHoveredStyleIndex;
                _hoveredMinusIndex = newHoverMinus;
                _hoveredPlusIndex = newHoverPlus;
                _hoveredResetIndex = newHoverReset;
                _hoveredThemeIndex = newHoveredTheme;
                _mediaExpToggleHovered = newMediaExpToggleHovered;
                _hoveredOpacityIndex = newHoveredOpacityIndex;
                _monitorDropdownHovered = newMonitorDropdownHovered;
                _hoveredMonitorDropdownIndex = newHoveredMonitorDropdownIndex;
                _toastModeDropdownHovered = newToastModeDropdownHovered;
                _hoveredToastModeIndex = newHoveredToastModeIndex;
                _compositeToggleHovered = newCompositeToggleHover;
                _compDateTimeHovered = newCompDateTimeHover;
                _compHardwareHovered = newCompHardwareHover;
                _compMediaHovered = newCompMediaHover;
                _topmostToggleHovered = newTopmostToggleHovered;
                _passToggleHovered = newPassToggleHovered;
                _clipboardToggleHovered = newClipboardToggleHovered;
                _hoveredPluginAction = newHoveredPluginAction;
                _hoveredPluginToggle = newHoveredPluginToggle;
                _hoveredPluginReload = newHoveredPluginReload;
                _hoveredPluginRemove = newHoveredPluginRemove;
                _hoveredPluginMoveLeft = newHoveredPluginMoveLeft;
                _hoveredPluginMoveRight = newHoveredPluginMoveRight;
                _fontPickHovered = newFontPickHovered;
                _fontResetHovered = newFontResetHovered;
                Render();
            }
        }
    }
}
