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
                    // index 0（水平宽度）/ 4（弹出的宽度）已改成「系统自动调整，无需设置」：
                    // 那两行既不画控件也不吃指针 —— 与绘制侧同源，改一边就得改另一边。
                    if (i == 0 || i == 4) continue;

                    float btnY = GetBtnY(i);
                    float rightX = WIDTH - 36; // 保持原有变量不动
                    if (x >= rightX - 175 && x <= rightX - 145 && y >= btnY && y <= btnY + 24) newHoverMinus = i;
                    if (x >= rightX - 80 && x <= rightX - 50 && y >= btnY && y <= btnY + 24) newHoverPlus = i;
                    if (x >= rightX - 40 && x <= rightX && y >= btnY && y <= btnY + 24) newHoverReset = i;
                }
            }

            int newHoveredStyleIndex = -1;
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
            bool newFontPickHovered = false;
            bool newFontResetHovered = false;

            if (_selectedTab == 0) // 通用设置
            {
                // ⚠️ 本段所有 y 值必须与 RenderTabGeneral 严格同源，坐标常量一律用上面的 const，
                //    不要再手写数字 —— 上一版就是手写 y 值 + 常量没右对齐，导致整片热区错位。
                // 开机自启
                if (x >= WIDTH - 80 && x <= WIDTH - 30 && y >= TITLE_BAR_HEIGHT + 32 && y <= TITLE_BAR_HEIGHT + 52)
                    newToggleHovered = true;
                // 窗口置顶开关
                if (x >= WIDTH - 80 && x <= WIDTH - 30 && y >= TITLE_BAR_HEIGHT + 104 && y <= TITLE_BAR_HEIGHT + 124)
                    newTopmostToggleHovered = true;

                // 🔔 系统消息通知卡行 1：总开关（轨道高 TOGGLE_TRACK_H，中心 = 行内锚点）
                if (x >= WIDTH - 80 && x <= WIDTH - 30
                    && y >= TITLE_BAR_HEIGHT + TOAST_TOGGLE_ROW_Y && y <= TITLE_BAR_HEIGHT + TOAST_TOGGLE_ROW_Y + TOGGLE_TRACK_H)
                    newToastToggleHovered = true;

                // 🔔 系统消息通知卡第 2 行：消息通知内容下拉
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

                // 🎵 行 3：消息提示音开关（轨道高 TOGGLE_TRACK_H，中心 = 行内锚点）
                if (x >= WIDTH - 80 && x <= WIDTH - 30
                    && y >= TITLE_BAR_HEIGHT + SOUND_TOGGLE_ROW_Y && y <= TITLE_BAR_HEIGHT + SOUND_TOGGLE_ROW_Y + TOGGLE_TRACK_H)
                    newSoundToggleHovered = true;

                // 🎵 行 4 的「提示音设置」是**父开关「消息提示音」的附属**：
                //    父开关关掉时整行不吃指针（与绘制侧的置灰判据同源，见 ToastSoundConfig.IsRowEnabled）。
                bool soundRowEnabled = ToastSoundConfig.IsRowEnabled;
                // 音量 / 试听 / 重置 还要再多一个条件：已选中具体音源（同样与绘制侧同源）
                bool soundReady = ToastSoundConfig.IsSourceReady;

                // 🎵 行 4：提示音下拉（浮层展开时底层不吃指针，避免误触浮窗底下的框）
                // ⚠️ 命中区必须与绘制侧同一个 SOUND_BOX_Y（框顶），不能用 SOUND_ROW_Y。
                if (soundRowEnabled && !_toastSoundDropdownOpen
                    && x >= SOUND_CTRL_X && x <= SOUND_CTRL_X + SOUND_CTRL_W
                    && y >= TITLE_BAR_HEIGHT + SOUND_BOX_Y && y <= TITLE_BAR_HEIGHT + SOUND_BOX_Y + SOUND_ROW_H)
                    newToastSoundDropdownHovered = true;
                if (_toastSoundDropdownOpen)
                {
                    // 🔻 布局（浮层顶 / 可视行数 / 最大首行）与绘制、滚轮共用同一个 GetToastSoundMenuLayout。
                    //    以前这里自己算一份、滚轮再算一份（还漏了 ROW_DROPDOWN_TOP），三处对不上，
                    //    才会出现「滚两下就断」。
                    GetToastSoundMenuLayout(out float menuTop, out int visible, out int maxFirst);
                    // 首行**只认 _dropdownScroll**，不再每帧「抢回选中项」—— 那是滚轮失效的元凶。
                    int first = Math.Clamp(_dropdownScroll, 0, maxFirst);
                    if (x >= SOUND_CTRL_X && x <= SOUND_CTRL_X + SOUND_CTRL_W
                        && y >= menuTop && y < menuTop + visible * DROPDOWN_ROW_H)
                        newHoveredToastSoundIndex = first + (int)((y - menuTop) / DROPDOWN_ROW_H);
                }

                // 🎵 音量下拉：只在选了具体音源时接受指针（与绘制侧的置灰判据同源）
                if (soundReady && !_soundVolumeDropdownOpen
                    && x >= SOUND_VOL_X && x <= SOUND_VOL_X + SOUND_VOL_W
                    && y >= TITLE_BAR_HEIGHT + SOUND_BOX_Y && y <= TITLE_BAR_HEIGHT + SOUND_BOX_Y + SOUND_ROW_H)
                    newSoundVolumeDropdownHovered = true;
                if (soundReady && _soundVolumeDropdownOpen)
                {
                    // 🔻 浮层顶 / 浮层底 / 可视行数**只认 GetVolumeMenuLayout**（与绘制侧同一个真源）。
                    //    以前这里自己算了一份、绘制侧在 RenderDropdownList 里又算一份，而且少了
                    //    `anchorY - 2` 那一步 —— 两边靠巧合算出同一个行数，SOUND_BOX_Y 一挪就错位。
                    GetVolumeMenuLayout(out float menuTopV, out float menuBottom, out _);
                    if (x >= SOUND_VOL_X && x <= SOUND_VOL_X + SOUND_VOL_W
                        && y >= menuTopV && y < menuBottom)
                        newHoveredSoundVolumeIndex = (int)((y - menuTopV) / DROPDOWN_ROW_H);
                }

                // 🎵 行 4 右侧按钮组：[试听] [重置]（同样只在有具体音源时接受指针）
                if (soundReady && y >= TITLE_BAR_HEIGHT + SOUND_BTN_Y && y <= TITLE_BAR_HEIGHT + SOUND_BTN_Y + SOUND_BTN_H)
                {
                    if (x >= SOUND_PREVIEW_X && x <= SOUND_PREVIEW_X + SOUND_BTN_W) newSoundPreviewHovered = true;
                    if (x >= SOUND_RESET_X && x <= SOUND_RESET_X + SOUND_BTN_W) newSoundResetHovered = true;
                }

                // 📋 剪贴板链接检测（= 卡片行首 CLIPBOARD_CARD_Y + 开关位移 20..40）
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

                // ── 显示内容列表（可滚动：首行 = _displayScroll，可视行数走 GetDisplayListLayout）──
                // ⚠️ 行起点 / 行高 / 箭头槽位必须与 RenderTabDisplay 严格同源：
                //    卡片顶 = TITLE_BAR_HEIGHT + DISPLAY_CARD_Y，首行 = 卡片顶 + DISPLAY_FIRST_ROW_Y，行高 DISPLAY_ROW_H。
                //    整行（名称 + 复选框）都可点 = 勾选 / 取消勾选；只有两个箭头槽吃 ↑ / ↓ 的悬停。
                //    命中出的行号是**绝对条目下标**（= 滚动首行 + 槽位），点击侧直接拿它索引 displayItems。
                GetDisplayListLayout(out int displayVisibleRows, out int displayMaxFirstRow);
                _displayScroll = Math.Clamp(_displayScroll, 0, displayMaxFirstRow);
                float displayRowTop = TITLE_BAR_HEIGHT + DISPLAY_CARD_Y + DISPLAY_FIRST_ROW_Y;
                float displayRowBottom = HEIGHT - 20;
                if (x >= 216 && x <= WIDTH - 36 && y >= displayRowTop && y <= displayRowBottom)
                {
                    int rowIdx = _displayScroll + (int)((y - displayRowTop) / DISPLAY_ROW_H);
                    if (rowIdx < _displayScroll + displayVisibleRows)
                    {
                        // 箭头槽比整行窄，所以先判箭头、再判整行 —— 箭头所在处不参与「整行勾选」的手感干扰
                        if (x >= DISPLAY_MOVE_UP_X && x <= DISPLAY_MOVE_UP_X + SORT_TRI_W) newHoveredDisplayMoveUp = rowIdx;
                        else if (x >= DISPLAY_MOVE_DOWN_X && x <= DISPLAY_MOVE_DOWN_X + SORT_TRI_W) newHoveredDisplayMoveDown = rowIdx;
                        else newHoveredDisplayRow = rowIdx;
                    }
                }
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
                //    （自动隐藏卡片是四行高：行1 +32、行2 +94、行3 +156、行4 +218；其余两张卡 +290 / +362）
                // 行 1：自动隐藏总开关（**已停用**，仍可拨动；穿透模式下禁止）
                if (!Renderer.PassthroughModeEnabled && x >= WIDTH - 80 && x <= WIDTH - 30 && y >= TITLE_BAR_HEIGHT + 32 && y <= TITLE_BAR_HEIGHT + 52)
                    newAutoHideToggleHovered = true;
                // 🎯 行 2：当焦点离开时自动隐藏岛。可用性**只**看穿透模式 —— 总开关不再是父开关。
                if (!Renderer.PassthroughModeEnabled
                    && x >= WIDTH - 80 && x <= WIDTH - 30 && y >= TITLE_BAR_HEIGHT + 94 && y <= TITLE_BAR_HEIGHT + 114)
                    newFocusHideToggleHovered = true;
                // 🎵 行 3：暂停播放后自动隐藏（可用性同上）
                if (!Renderer.PassthroughModeEnabled
                    && x >= WIDTH - 80 && x <= WIDTH - 30 && y >= TITLE_BAR_HEIGHT + 156 && y <= TITLE_BAR_HEIGHT + 176)
                    newPauseHideToggleHovered = true;
                // 🖥 行 4：全屏自动隐藏（可用性同上）
                if (!Renderer.PassthroughModeEnabled
                    && x >= WIDTH - 80 && x <= WIDTH - 30 && y >= TITLE_BAR_HEIGHT + 218 && y <= TITLE_BAR_HEIGHT + 238)
                    newFsHideToggleHovered = true;
                // 媒体交互模式（组合模式同样可用：组合模式现在也能展开媒体面板）
                if (x >= WIDTH - 80 && x <= WIDTH - 30 && y >= TITLE_BAR_HEIGHT + 290 && y <= TITLE_BAR_HEIGHT + 310)
                    newMediaExpToggleHovered = true;
                // 使用局部变量，防止状态死锁
                newPassToggleHovered = x >= WIDTH - 80 && x <= WIDTH - 30 && y >= TITLE_BAR_HEIGHT + 362 && y <= TITLE_BAR_HEIGHT + 382;
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

                // 列表行内按钮（全部在下行：重载 | 移除 | 开关）
                // 上行（名称）无交互目标，仅下行按钮可点击
                float listY = topY + 110;
                int rows = Math.Min(_pluginView.Count, 7);
                // ⚠️ 这里的行起点必须与 Render() 里的 `listY + 44` 严格一致。
                //    （2026-09-25 删掉卡片顶部那行「顺序：…」后，整块列表上移了 20px）
                if (x >= 216 && x <= WIDTH - 36 && y >= listY + 44)
                {
                    int idx = (int)((y - (listY + 44)) / 56);
                    if (idx >= 0 && idx < rows)
                    {
                        float rowY = listY + 44 + idx * 56;
                        // 下行按钮区（rowY+22 .. rowY+48）
                        if (y >= rowY + 22 && y <= rowY + 48)
                        {
                            // 从左到右：重载 | 移除 | 开关
                            // （排序已于 2026-09-25 移到「显示设置 → 显示内容」）
                            if (x >= PLUGIN_BTN_RELOAD_X && x <= PLUGIN_BTN_RELOAD_X + 50) newHoveredPluginReload = idx;
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
            //    自动隐藏卡片加高后，「媒体交互方式」卡片跟着往下挪，这里没跟着改，
            //    禁止区域就压在了别的开关那一行上 —— 导致不管该开关是否被禁用，
            //    hover 上去都是禁止指针。卡片高度自 2026-09-26 起是四行 248px（12..260））。
            if (x >= 200 && x <= WIDTH - 20)
            {
                // 注：tab 1 已没有置灰区域 —— 「待机显示内容」卡片与「启用组合模式」开关都在
                //     2026-09-25 被「显示内容」列表取代，那张列表整行可点、没有禁用项。
                if (_selectedTab == 0 && !ToastSoundConfig.IsRowEnabled
                    && y >= TITLE_BAR_HEIGHT + SOUND_ROW_Y && y <= TITLE_BAR_HEIGHT + SOUND_BOX_Y + SOUND_ROW_H)
                {
                    // 🎵 提示音设置行（通知卡行 4）：父开关「消息提示音」关掉时整行禁止指针。
                    //    判据与绘制侧的置灰（ToastSoundConfig.IsRowEnabled）、命中侧的不吃指针
                    //    **完全同源** —— 与「父开关关掉 → 附属行整行禁用」的通用约定同一套（如「消息提示音 → 提示音设置」）。
                    newIsHoveringDisabledArea = true;
                }
                else if (_selectedTab == 3)
                {
                    // 与 Render() tab 3 的 disabled 判据同源（改那边记得改这边）：
                    // 整张自动隐藏卡片**只**因穿透模式而禁用 —— 自 2026-09-26 起总开关不再栅栏三个模式，
                    // 所以旧的 subToggleDisabled（需先开启「自动隐藏」）分支已删除。
                    // 注：「媒体交互方式」卡片不再因为组合模式而禁用（2026-09-25 起组合模式也能展开媒体面板），
                    //     所以这里也没有它对应的禁止指针区间。
                    if (Renderer.PassthroughModeEnabled
                        && y >= TITLE_BAR_HEIGHT + 12 && y <= TITLE_BAR_HEIGHT + 260)   // 自动隐藏卡片整卡（穿透模式下四行全禁用）
                        newIsHoveringDisabledArea = true;
                }
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
                newHoveredDisplayRow != _hoveredDisplayRow ||
                newHoveredDisplayMoveUp != _hoveredDisplayMoveUp ||
                newHoveredDisplayMoveDown != _hoveredDisplayMoveDown ||
                newHoverMinus != _hoveredMinusIndex || newHoverPlus != _hoveredPlusIndex ||
                newHoverReset != _hoveredResetIndex || newMediaExpToggleHovered != _mediaExpToggleHovered ||
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
                _fontPickHovered = newFontPickHovered;
                _fontResetHovered = newFontResetHovered;

                // 🖱 「显示内容」列表：指针压在这一行的任何部位（行本体 / ∧ / ∨）都算悬停，
                //    行底动画统一按这个行号淡入淡出；行号一变就开表，由它逐拍推进到目标值。
                //    ⚠️ 动画数组按**可视槽位**索引（行号 - 滚动首行），所以滚动后槽位变了也会重开表。
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
