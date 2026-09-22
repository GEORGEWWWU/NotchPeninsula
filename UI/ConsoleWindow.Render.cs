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
        // ============================================================
        //  设置窗口 —— 绘制部分（由 Render() 拆出）
        //  局部函数已提升为实例方法，统一把 canvas 作为第一个参数。
        //  每个页签一个 RenderTabXxx，只依赖 canvas 与实例字段。
        // ============================================================

        // 侧边栏单个页签（选中态 / 悬停态 / 文本）
        private void DrawTab(SKCanvas canvas, int index, string label, float yOffset)
        {
            var tabRect = new SKRect(10, TITLE_BAR_HEIGHT + yOffset, 170, TITLE_BAR_HEIGHT + yOffset + 36);
            if (_selectedTab == index)
            {
                canvas.DrawRoundRect(tabRect, 4, 4, _tabBgSelected);
                canvas.DrawRoundRect(new SKRect(10, TITLE_BAR_HEIGHT + yOffset + 8, 13, TITLE_BAR_HEIGHT + yOffset + 28), 1.5f, 1.5f, _tabIndicator);
            }
            else if (_hoveredTab == index)
            {
                canvas.DrawRoundRect(tabRect, 4, 4, _tabBgHovered);
            }
            canvas.DrawText(label, 30, TITLE_BAR_HEIGHT + yOffset + 24, _uiTextPaint);
        }

        // 画整张卡片底 + 一行开关内容
        private void DrawToggleCard(SKCanvas canvas, float yOffset, string title, string sub, bool state, bool hovered, bool disabled = false)
        {
            var cardRect = new SKRect(200, TITLE_BAR_HEIGHT + yOffset, WIDTH - 20, TITLE_BAR_HEIGHT + yOffset + 62);
            canvas.DrawRoundRect(cardRect, 6, 6, _cardBg);
            canvas.DrawRoundRect(cardRect, 6, 6, _cardBorder);
            DrawToggleRow(canvas, yOffset, title, sub, state, hovered, disabled);
        }

        // 只画「一行开关」的内容（标题 / 副标题 / 右侧开关），**不画卡片底**。
        // 拆出来是为了让「自动隐藏」那张卡片能在同一个卡片底里放两行（主开关 + 附属开关）。
        // 单行内容的位置（标题 +26 / 副标题 +46 / 开关 +20..+40）与原来完全一致，所以既有调用方行为不变。
        private void DrawToggleRow(SKCanvas canvas, float yOffset, string title, string sub, bool state, bool hovered, bool disabled = false)
        {
            _uiTextPaint.Color = disabled ? new SKColor(100, 100, 100) : SKColors.White;
            canvas.DrawText(title, 216, TITLE_BAR_HEIGHT + yOffset + 26, _uiTextPaint);
            _uiTextPaint.Color = SKColors.White;

            _subTextPaint.Color = disabled ? new SKColor(80, 80, 80) : new SKColor(170, 170, 170);
            canvas.DrawText(sub, 216, TITLE_BAR_HEIGHT + yOffset + 46, _subTextPaint);
            _subTextPaint.Color = new SKColor(170, 170, 170);

            float tW = 42; float tH = 20; float tX = WIDTH - 20 - 16 - tW; float tY = TITLE_BAR_HEIGHT + yOffset + 20;
            var tRect = new SKRect(tX, tY, tX + tW, tY + tH);

            if (disabled)
            {
                _dynamicStrokePaint.Color = new SKColor(80, 80, 80);
                canvas.DrawRoundRect(tRect, tH / 2, tH / 2, _dynamicStrokePaint);
                _toggleCirclePaint.Color = new SKColor(100, 100, 100);
                canvas.DrawCircle(tX + tH / 2, tY + tH / 2, tH / 2 - 4, _toggleCirclePaint);
                _toggleCirclePaint.Color = SKColors.White;
            }
            else
            {
                if (state)
                {
                    _dynamicFillPaint.Color = hovered ? new SKColor(0, 140, 240) : new SKColor(0, 120, 212);
                    canvas.DrawRoundRect(tRect, tH / 2, tH / 2, _dynamicFillPaint);
                }
                else
                {
                    _dynamicStrokePaint.Color = hovered ? new SKColor(150, 150, 150) : new SKColor(100, 100, 100);
                    canvas.DrawRoundRect(tRect, tH / 2, tH / 2, _dynamicStrokePaint);
                }

                if (state) canvas.DrawCircle(tX + tW - tH / 2, tY + tH / 2, tH / 2 - 4, _toggleCirclePaint);
                else
                {
                    _toggleCirclePaint.Color = hovered ? new SKColor(200, 200, 200) : new SKColor(150, 150, 150);
                    canvas.DrawCircle(tX + tH / 2, tY + tH / 2, tH / 2 - 4, _toggleCirclePaint);
                    _toggleCirclePaint.Color = SKColors.White;
                }
            }
        }

        // 行内小开关（组合模式总开关用）
        private void DrawToggleCard_Inline(SKCanvas canvas, float yOffset, string title, string sub, bool state, bool hovered)
        {
            canvas.DrawText(title, 216, yOffset + 16, _uiTextPaint);
            canvas.DrawText(sub, 216, yOffset + 36, _subTextPaint);
            float tW = 42; float tH = 20; float tX = WIDTH - 20 - 16 - tW; float tY = yOffset + 10;
            var tRect = new SKRect(tX, tY, tX + tW, tY + tH);
            if (state)
            {
                _dynamicFillPaint.Color = hovered ? new SKColor(0, 140, 240) : new SKColor(0, 120, 212);
                canvas.DrawRoundRect(tRect, tH / 2, tH / 2, _dynamicFillPaint);
                canvas.DrawCircle(tX + tW - tH / 2, tY + tH / 2, tH / 2 - 4, _toggleCirclePaint);
            }
            else
            {
                _dynamicStrokePaint.Color = hovered ? new SKColor(150, 150, 150) : new SKColor(100, 100, 100);
                canvas.DrawRoundRect(tRect, tH / 2, tH / 2, _dynamicStrokePaint);
                _toggleCirclePaint.Color = hovered ? new SKColor(200, 200, 200) : new SKColor(150, 150, 150);
                canvas.DrawCircle(tX + tH / 2, tY + tH / 2, tH / 2 - 4, _toggleCirclePaint);
                _toggleCirclePaint.Color = SKColors.White;
            }
        }

        // 勾选框选项
        private void DrawCheckItem(SKCanvas canvas, float yOffset, string label, bool isChecked, bool hovered, bool disabled)
        {
            float boxX = 216;
            float boxY = yOffset;
            float boxSize = 16f;
            var boxRect = new SKRect(boxX, boxY, boxX + boxSize, boxY + boxSize);

            if (disabled)
            {
                _dynamicStrokePaint.Color = new SKColor(80, 80, 80);
                _dynamicFillPaint.Color = new SKColor(60, 60, 60);
            }
            else
            {
                _dynamicStrokePaint.Color = isChecked ? new SKColor(0, 120, 212) : (hovered ? new SKColor(150, 150, 150) : new SKColor(100, 100, 100));
                _dynamicFillPaint.Color = isChecked ? new SKColor(0, 120, 212) : SKColors.Transparent;
            }

            canvas.DrawRoundRect(boxRect, 3, 3, _dynamicFillPaint);
            canvas.DrawRoundRect(boxRect, 3, 3, _dynamicStrokePaint);

            if (isChecked)
            {
                _iconPaint.Color = SKColors.White;
                canvas.DrawLine(boxX + 3, boxY + 8, boxX + 6, boxY + 11, _iconPaint);
                canvas.DrawLine(boxX + 6, boxY + 11, boxX + 13, boxY + 4, _iconPaint);
            }

            if (disabled)
                _uiTextPaint.Color = new SKColor(100, 100, 100);
            else
                _uiTextPaint.Color = SKColors.White;
            canvas.DrawText(label, boxX + 24, boxY + 13, _uiTextPaint);
            _uiTextPaint.Color = SKColors.White;
        }

        // 侧边栏重排与分割线绘制
        private void RenderSidebar(SKCanvas canvas)
        {
            // 个性化中心最上，两条分割线
            DrawTab(canvas, 5, "个性化中心", 10);
            canvas.DrawLine(20, TITLE_BAR_HEIGHT + 52, 160, TITLE_BAR_HEIGHT + 52, _separatorPaint);
            DrawTab(canvas, 0, "通用设置", 60);
            DrawTab(canvas, 1, "显示设置", 100);
            DrawTab(canvas, 2, "媒体设置", 140);
            DrawTab(canvas, 3, "交互设置", 180);
            canvas.DrawLine(20, TITLE_BAR_HEIGHT + 222, 160, TITLE_BAR_HEIGHT + 222, _separatorPaint);
            DrawTab(canvas, 6, "插件中心", 230);
            canvas.DrawLine(20, TITLE_BAR_HEIGHT + 272, 160, TITLE_BAR_HEIGHT + 272, _separatorPaint);
            DrawTab(canvas, 4, "关于软件", 280);
        }

        // 页签：通用设置
        private void RenderTabGeneral(SKCanvas canvas)
        {
            DrawToggleCard(canvas, 12, "开机自启", "跟随系统启动自动运行该程序", _isAutoStartEnabled, _toggleHovered);
            // 窗口置顶（与下方消息通知整组互换位置）
            DrawToggleCard(canvas, 84, "窗口置顶", "开启后刘海将始终保持在其他窗口最上层", NotchWindow.IsTopmostEnabled, _topmostToggleHovered);
            // 🔔 「系统消息通知」+「消息通知内容」—— 一张两行卡（yOffset 156..284）
            //    卡片高 128：行1 +156（开关 +176..+196）、分隔线 +226、
            //    行2「消息通知内容」标题 +245 / 描述 +265 / 下拉 +272..+304。
            //    ⚠️ 改这里的数值时必须同步改 WM_MOUSEMOVE 的 tab 0 段与 RenderDropdowns 的浮层锚点。
            var notifyCardRect = new SKRect(200, TITLE_BAR_HEIGHT + 156, WIDTH - 20, TITLE_BAR_HEIGHT + TOAST_CARD_BOTTOM);
            canvas.DrawRoundRect(notifyCardRect, 6, 6, _cardBg);
            canvas.DrawRoundRect(notifyCardRect, 6, 6, _cardBorder);

            DrawToggleRow(canvas, 156, "系统消息通知", "允许在刘海中显示Windows系统的Toast消息", NotchWindow.IsToastEnabled, _toastToggleHovered);

            canvas.DrawLine(216, TITLE_BAR_HEIGHT + 226, WIDTH - 36, TITLE_BAR_HEIGHT + 226, _separatorPaint);

            // 行2：消息通知内容下拉（完整时展示应用名并拉大通知尺寸）
            canvas.DrawText("消息通知内容", 216, TITLE_BAR_HEIGHT + 256, _uiTextPaint);
            string toastModeDesc = _selectedToastModeIndex switch
            {
                2 => "完整显示应用名、发送者与消息主体",
                1 => "右侧展示“现在”与应用名",
                _ => "仅显示发送者与消息主体"
            };
            canvas.DrawText(toastModeDesc, 216, TITLE_BAR_HEIGHT + 276, _subTextPaint);

            DrawDropdownBox(canvas, TOAST_MODE_CTRL_X, TOAST_MODE_ROW_Y, TOAST_MODE_CTRL_W, TOAST_MODE_ROW_H,
                _toastModeOptions[_selectedToastModeIndex], _toastModeDropdownHovered, enabled: true);

            // 🎵 「消息提示音」—— 独立子卡片（yOffset 294..410）
            //    第 1 行：提示音开关 + 音频来源说明（默认关闭，不想被打扰的人不用管）
            //    第 2 行：左起「提示音」信息文本 → 提示音下拉 → 音量下拉 → [试听] [重置] → 右侧小字说明
            //    独立成卡是因为它有自己的开关与两组下拉，硬塞进通知卡会顶出一张 252px 的高卡、
            //    控件横向铺满整卡，观感就是「溢出」。
            var soundCardRect = new SKRect(200, TITLE_BAR_HEIGHT + SOUND_CARD_Y, WIDTH - 20, TITLE_BAR_HEIGHT + SOUND_CARD_Y + SOUND_CARD_H);
            canvas.DrawRoundRect(soundCardRect, 6, 6, _cardBg);
            canvas.DrawRoundRect(soundCardRect, 6, 6, _cardBorder);

            // 第 1 行：开关
            string soundSub = _soundHint.Length > 0
                ? _soundHint
                : "新消息到达时播放提示音（默认关闭，不吃任何内存）";
            DrawToggleRow(canvas, SOUND_TOGGLE_ROW_Y - 20, "消息提示音", soundSub,
                ToastSoundConfig.IsEnabled, _soundToggleHovered);

            // 第 2 行：提示音下拉 + 音量下拉 + 两个按钮
            // 「音量」下拉的可用性只看「有没有具体音源」，与绘制侧置灰判据同源
            bool soundReady = ToastSoundConfig.SelectedIndex > 0;

            DrawDropdownBox(canvas, SOUND_CTRL_X, SOUND_ROW_Y, SOUND_CTRL_W, SOUND_ROW_H,
                ToastSoundConfig.CurrentDisplayText(), _toastSoundDropdownHovered, enabled: true);

            DrawDropdownBox(canvas, SOUND_VOL_X, SOUND_ROW_Y, SOUND_VOL_W, SOUND_ROW_H,
                $"{ToastSoundConfig.VolumePercent}%", _soundVolumeDropdownHovered, enabled: soundReady);

            void DrawSoundButton(bool hovered, string label, float bx, float bw, bool enabled)
            {
                var btn = new SKRect(bx, TITLE_BAR_HEIGHT + SOUND_BTN_Y, bx + bw, TITLE_BAR_HEIGHT + SOUND_BTN_Y + SOUND_BTN_H);
                bool hot = enabled && hovered;
                if (!enabled) _dynamicFillPaint.Color = new SKColor(255, 255, 255, 6);
                else _dynamicFillPaint.Color = hot ? new SKColor(0, 140, 240) : new SKColor(0, 120, 212);
                canvas.DrawRoundRect(btn, 4, 4, _dynamicFillPaint);
                float tw = _uiTextPaint.MeasureText(label);
                if (!enabled) _uiTextPaint.Color = new SKColor(110, 110, 110);
                canvas.DrawText(label, bx + (bw - tw) / 2f, TITLE_BAR_HEIGHT + SOUND_BTN_Y + 17, _uiTextPaint);
                if (!enabled) _uiTextPaint.Color = new SKColor(240, 240, 240);
            }

            DrawSoundButton(_soundPreviewHovered, "试听", SOUND_PREVIEW_X, SOUND_BTN_W, soundReady);
            DrawSoundButton(_soundResetHovered, "重置", SOUND_RESET_X, SOUND_BTN_W, soundReady);

            // 第 3 行：来源 / 上限的小字说明，让用户知道「为什么有些音频选不了」
            string soundFootnote = soundReady
                ? $"仅接受 ≤{ToastSoundConfig.MaxDurationSec:F0} 秒、≤{ToastSoundConfig.MaxFileSizeBytes / 1024 / 1024} MB 的音频；列表来自 data\\sound 目录"
                : "把音频文件放进 data\\sound 目录即可出现在上面的列表里";
            canvas.DrawText(TruncateText(soundFootnote, _subTextPaint, WIDTH - 20 - 216 - 12),
                216, TITLE_BAR_HEIGHT + SOUND_HINT_Y, _subTextPaint);

            // 📋 剪贴板链接检测（2026-09-20 从「交互设置」搬来 —— 它是个功能开关，不属于交互行为）
            DrawToggleCard(canvas, 436, "剪贴板链接检测", "复制链接时在刘海中显示，可一键在默认浏览器打开", NotchWindow.IsClipboardEnabled, _clipboardToggleHovered);

            // 切换灵动岛字体：选中字体文件后立即热替换岛内全部文本字体（默认系统字体，不做任何改动）
            var fontCard = new SKRect(200, TITLE_BAR_HEIGHT + FONT_CARD_Y, WIDTH - 20, TITLE_BAR_HEIGHT + FONT_CARD_Y + 62);
            canvas.DrawRoundRect(fontCard, 6, 6, _cardBg);
            canvas.DrawRoundRect(fontCard, 6, 6, _cardBorder);
            canvas.DrawText("切换灵动岛字体", 216, TITLE_BAR_HEIGHT + FONT_CARD_Y + 26, _uiTextPaint);

            bool fontError = _fontHint.Length > 0;
            string fontSub = fontError ? _fontHint : $"当前：{FontConfig.DisplayName}";
            _subTextPaint.Color = fontError ? new SKColor(232, 100, 100) : new SKColor(170, 170, 170);
            canvas.DrawText(TruncateText(fontSub, _subTextPaint, FONT_PICK_X - 216 - 8), 216, TITLE_BAR_HEIGHT + FONT_CARD_Y + 46, _subTextPaint);
            _subTextPaint.Color = new SKColor(170, 170, 170);

            void DrawFontButton(bool hovered, string label, float bx, float bw)
            {
                var btn = new SKRect(bx, TITLE_BAR_HEIGHT + FONT_BTN_Y, bx + bw, TITLE_BAR_HEIGHT + FONT_BTN_Y + FONT_BTN_H);
                _dynamicFillPaint.Color = hovered ? new SKColor(0, 140, 240) : new SKColor(0, 120, 212);
                canvas.DrawRoundRect(btn, 4, 4, _dynamicFillPaint);
                float tw = _uiTextPaint.MeasureText(label);
                canvas.DrawText(label, bx + (bw - tw) / 2f, TITLE_BAR_HEIGHT + FONT_BTN_Y + 18, _uiTextPaint);
            }

            DrawFontButton(_fontPickHovered, "选择字体", FONT_PICK_X, FONT_PICK_W);
            DrawFontButton(_fontResetHovered, "重置", FONT_RESET_X, FONT_RESET_W);
        }

        /// <summary>
        /// 通用设置页的窄版下拉框。**位置与宽度全部由调用方传入**，热区直接复用同一组常数，
        /// 不会再出现「渲染在一处、命中在另一处」的错位。
        /// 禁用态（<paramref name="enabled"/> = false）会整体降低不透明度并画成灰色，
        /// 与命中侧的置灰判据必须同源。
        /// </summary>
        private void DrawDropdownBox(SKCanvas canvas, float x, float yOffset, float w, float h,
                                     string text, bool hovered, bool enabled)
        {
            var rect = new SKRect(x, TITLE_BAR_HEIGHT + yOffset, x + w, TITLE_BAR_HEIGHT + yOffset + h);
            float bgAlpha = enabled ? (hovered ? 15 : 8) : 4;
            _dynamicFillPaint.Color = new SKColor(255, 255, 255, (byte)bgAlpha);
            canvas.DrawRoundRect(rect, 4, 4, _dynamicFillPaint);

            if (!enabled) _uiTextPaint.Color = new SKColor(110, 110, 110);
            canvas.DrawText(TruncateText(text, _uiTextPaint, w - 26), rect.Left + 10, rect.Top + h / 2f + 5, _uiTextPaint);
            if (!enabled) _uiTextPaint.Color = new SKColor(240, 240, 240);

            float cx = rect.Right - 18;
            float cy = rect.MidY;
            if (!enabled) _chevronPaint.Color = new SKColor(90, 90, 90);
            canvas.DrawLine(cx, cy - 2.5f, cx + 5, cy + 2.5f, _chevronPaint);
            canvas.DrawLine(cx + 5, cy + 2.5f, cx + 10, cy - 2.5f, _chevronPaint);
            if (!enabled) _chevronPaint.Color = new SKColor(160, 160, 160);
        }

        // 页签：显示设置
        private void RenderTabDisplay(SKCanvas canvas)
        {
            // 刘海形态两列布局选择器
            var styleCardRect = new SKRect(200, TITLE_BAR_HEIGHT + 12, WIDTH - 20, TITLE_BAR_HEIGHT + 160);
            canvas.DrawRoundRect(styleCardRect, 6, 6, _cardBg);
            canvas.DrawRoundRect(styleCardRect, 6, 6, _cardBorder);
            canvas.DrawText("刘海形态", 216, TITLE_BAR_HEIGHT + 38, _uiTextPaint);

            void DrawStyleOption(int index, string name, float x, float y)
            {
                bool isSelected = Renderer.NotchStyle == index;
                bool isHovered = _hoveredStyleIndex == index;

                // 选项外框与背景反馈
                var optRect = new SKRect(x, y, x + 150, y + 90);
                _dynamicFillPaint.Color = isSelected ? new SKColor(0, 120, 212, 40) : (isHovered ? new SKColor(255, 255, 255, 15) : new SKColor(255, 255, 255, 8));
                canvas.DrawRoundRect(optRect, 6, 6, _dynamicFillPaint);
                _dynamicStrokePaint.Color = isSelected ? new SKColor(0, 120, 212) : new SKColor(80, 80, 80);
                canvas.DrawRoundRect(optRect, 6, 6, _dynamicStrokePaint);

                // 绘制纯血 Skia 伪 PNG 视觉特效图
                float cx = x + 75; float cy = y + 35;

                // 颜色直接同步真实的明暗逻辑，并完美兼容“跟随系统”模式
                bool isLight = Renderer.ThemeMode == 1 || (Renderer.ThemeMode == 2 && Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize")?.GetValue("AppsUseLightTheme") is int val && val == 1);
                _dynamicFillPaint.Color = isLight ? SKColors.White : SKColors.Black;

                if (index == 0) // 调整经典刘海的矢量绘图比例，使其视觉高度和灵动岛保持一致
                {
                    var path = new SKPath();
                    path.MoveTo(cx - 35, cy - 10);
                    path.QuadTo(cx - 25, cy - 10, cx - 25, cy - 5);
                    path.LineTo(cx - 25, cy + 5);
                    path.QuadTo(cx - 25, cy + 10, cx - 15, cy + 10);
                    path.LineTo(cx + 15, cy + 10);
                    path.QuadTo(cx + 25, cy + 10, cx + 25, cy + 5);
                    path.LineTo(cx + 25, cy - 5);
                    path.QuadTo(cx + 25, cy - 10, cx + 35, cy - 10);
                    canvas.DrawPath(path, _dynamicFillPaint);
                }
                else // 模拟灵动岛
                {
                    // 统一高度 20px，圆角 10px 形成胶囊
                    canvas.DrawRoundRect(new SKRect(cx - 25, cy - 10, cx + 25, cy + 10), 10, 10, _dynamicFillPaint);
                }

                // 单选 Radio 按钮与文本
                float radioY = y + 72;
                canvas.DrawCircle(cx - 30, radioY - 4, 6, _dynamicStrokePaint);
                if (isSelected)
                {
                    _dynamicFillPaint.Color = new SKColor(0, 120, 212);
                    canvas.DrawCircle(cx - 30, radioY - 4, 3, _dynamicFillPaint);
                }
                _dynamicTextPaint.Color = isSelected ? new SKColor(0, 140, 240) : SKColors.White;
                canvas.DrawText(name, cx - 15, radioY + 1, _dynamicTextPaint);
            }

            DrawStyleOption(0, "经典刘海", 220, TITLE_BAR_HEIGHT + 50);
            DrawStyleOption(1, "悬浮胶囊", 390, TITLE_BAR_HEIGHT + 50);

            // 目标显示器卡片
            float monitorCardY = TITLE_BAR_HEIGHT + 172;
            var mCardRect = new SKRect(200, monitorCardY, WIDTH - 20, monitorCardY + 62);
            canvas.DrawRoundRect(mCardRect, 6, 6, _cardBg);
            canvas.DrawRoundRect(mCardRect, 6, 6, _cardBorder);
            canvas.DrawText("目标显示器", 216, monitorCardY + 26, _uiTextPaint);
            canvas.DrawText("选择刘海灵动岛显示的目标屏幕", 216, monitorCardY + 46, _subTextPaint);

            float mdW = 110; float mdX = WIDTH - 140; float mdY = monitorCardY + 14; float mdH = 32;
            var mdRect = new SKRect(mdX, mdY, mdX + mdW, mdY + mdH);
            _dynamicFillPaint.Color = _monitorDropdownHovered ? new SKColor(255, 255, 255, 15) : new SKColor(255, 255, 255, 8);
            canvas.DrawRoundRect(mdRect, 4, 4, _dynamicFillPaint);
            string mName = Renderer.TargetMonitorIndex < _monitorOptions.Length ? _monitorOptions[Renderer.TargetMonitorIndex] : "未知";
            canvas.DrawText(mName, mdX + 10, mdY + 21, _uiTextPaint);
            canvas.DrawLine(mdX + mdW - 20, mdY + 14, mdX + mdW - 15, mdY + 19, _chevronPaint);
            canvas.DrawLine(mdX + mdW - 15, mdY + 19, mdX + mdW - 10, mdY + 14, _chevronPaint);

            // 待机显示内容卡片 (极简宫格布局)
            float displayCardY = TITLE_BAR_HEIGHT + 248;
            var displayCardRect = new SKRect(200, displayCardY, WIDTH - 20, displayCardY + 112); // 卡片高度减半收缩
            canvas.DrawRoundRect(displayCardRect, 6, 6, _cardBg);
            canvas.DrawRoundRect(displayCardRect, 6, 6, _cardBorder);
            _uiTextPaint.Color = Renderer.CompositeModeEnabled ? new SKColor(100, 100, 100) : SKColors.White;
            canvas.DrawText("待机显示内容", 216, displayCardY + 26, _uiTextPaint);
            _uiTextPaint.Color = SKColors.White;
            _subTextPaint.Color = Renderer.CompositeModeEnabled ? new SKColor(80, 80, 80) : new SKColor(170, 170, 170);
            canvas.DrawText("刘海处于待机状态时默认展示的信息", 216, displayCardY + 46, _subTextPaint);
            _subTextPaint.Color = new SKColor(170, 170, 170);
            void DrawDisplayOpt(int index, string name, float x, float y)
            {
                bool isDisabled = Renderer.CompositeModeEnabled;
                bool isSelected = _selectedDisplayIndex == index && !isDisabled;
                bool isHovered = _hoveredDisplayOptionIndex == index && !isDisabled;

                var optRect = new SKRect(x, y, x + 110, y + 40);
                _dynamicFillPaint.Color = isDisabled ? new SKColor(255, 255, 255, 3) : (isSelected ? new SKColor(0, 120, 212, 40) : (isHovered ? new SKColor(255, 255, 255, 15) : new SKColor(255, 255, 255, 8)));
                canvas.DrawRoundRect(optRect, 6, 6, _dynamicFillPaint);
                _dynamicStrokePaint.Color = isDisabled ? new SKColor(60, 60, 60) : (isSelected ? new SKColor(0, 120, 212) : new SKColor(80, 80, 80));
                canvas.DrawRoundRect(optRect, 6, 6, _dynamicStrokePaint);

                float cx = x + 20; float cy = y + 20;
                _dynamicStrokePaint.Color = isDisabled ? new SKColor(100, 100, 100) : (isSelected ? new SKColor(0, 140, 240) : SKColors.White);
                _dynamicStrokePaint.StrokeWidth = 1.5f;

                if (index == 0) { canvas.DrawCircle(cx, cy, 8, _dynamicStrokePaint); canvas.DrawLine(cx, cy, cx, cy - 4, _dynamicStrokePaint); canvas.DrawLine(cx, cy, cx + 3, cy + 3, _dynamicStrokePaint); }
                else if (index == 1) { canvas.DrawLine(cx - 6, cy, cx + 6, cy, _dynamicStrokePaint); }
                else if (index == 2) { canvas.DrawRect(cx - 7, cy - 6, 14, 12, _dynamicStrokePaint); canvas.DrawLine(cx - 3, cy - 3, cx + 3, cy - 3, _dynamicStrokePaint); }

                _dynamicTextPaint.Color = isDisabled ? new SKColor(100, 100, 100) : (isSelected ? new SKColor(0, 140, 240) : SKColors.White);
                canvas.DrawText(name, cx + 18, cy + 5, _dynamicTextPaint);
            }

            DrawDisplayOpt(2, "硬件占用", 220, displayCardY + 58);
            DrawDisplayOpt(0, "时间日期", 340, displayCardY + 58);
            DrawDisplayOpt(1, "空白", 460, displayCardY + 58);

            // 自定义组合模式卡片
            float compositeCardY = TITLE_BAR_HEIGHT + 372;
            var compositeCardRect = new SKRect(200, compositeCardY, WIDTH - 20, compositeCardY + 200);
            canvas.DrawRoundRect(compositeCardRect, 6, 6, _cardBg);
            canvas.DrawRoundRect(compositeCardRect, 6, 6, _cardBorder);
            canvas.DrawText("自定义组合模式", 216, compositeCardY + 26, _uiTextPaint);
            canvas.DrawText("自由选择刘海内显示的功能模块", 216, compositeCardY + 46, _subTextPaint);

            // 总开关
            DrawToggleCard_Inline(canvas, compositeCardY + 62, "启用组合模式", "开启后可同时显示多个功能模块", Renderer.CompositeModeEnabled, _compositeToggleHovered);

            // 子选项
            DrawCheckItem(canvas, compositeCardY + 105, "时间日期", Renderer.CompShowDateTime, _compDateTimeHovered, !Renderer.CompositeModeEnabled);
            DrawCheckItem(canvas, compositeCardY + 140, "资源占用检测", Renderer.CompShowHardware, _compHardwareHovered, !Renderer.CompositeModeEnabled);
            DrawCheckItem(canvas, compositeCardY + 175, "媒体控制器(含频谱)", Renderer.CompShowMedia, _compMediaHovered, !Renderer.CompositeModeEnabled);

        }

        // 页签：媒体设置
        private void RenderTabMedia(SKCanvas canvas)
        {
            DrawToggleCard(canvas, 12, "媒体控制", "允许在刘海中显示和控制系统媒体播放", MediaController.IsMediaControlEnabled, _mediaToggleHovered);

            // 🎚 合并卡片：「目标媒体平台」+「匹配方式」共用一张卡（两行 × 62 = 124 高，84..208）
            //    第 1 行行首 84 / 分隔线 142（行首 +58）/ 第 2 行行首 146（行距 62）
            //    ⚠️ 本段所有 y 值必须与 WM_MOUSEMOVE 的 tab 2 命中段保持同步（见字段区的坐标常量注释）
            var platformCardRect = new SKRect(200, PLATFORM_CARD_Y, WIDTH - 20, PLATFORM_CARD_Y + 124);
            canvas.DrawRoundRect(platformCardRect, 6, 6, _cardBg);
            canvas.DrawRoundRect(platformCardRect, 6, 6, _cardBorder);

            // ── 第 1 行：目标媒体平台 ──
            canvas.DrawText("目标媒体平台", 216, TITLE_BAR_HEIGHT + 110, _uiTextPaint);
            canvas.DrawText(MediaController.TargetPlatform == "browser" ? "仅接管浏览器内的播放会话" : "多平台共存时，优先截获并接管的平台",
                216, TITLE_BAR_HEIGHT + 130, _subTextPaint);

            float dW = 110; float dX = WIDTH - 140; float dY = TITLE_BAR_HEIGHT + 96; float dH = 32;
            var dRect = new SKRect(dX, dY, dX + dW, dY + dH);
            _dynamicFillPaint.Color = _dropdownHovered ? new SKColor(255, 255, 255, 15) : new SKColor(255, 255, 255, 8);
            canvas.DrawRoundRect(dRect, 4, 4, _dynamicFillPaint);
            canvas.DrawText(_platforms[_selectedPlatformIndex].Name, dX + 10, dY + 21, _uiTextPaint);

            canvas.DrawLine(dX + dW - 20, dY + 14, dX + dW - 15, dY + 19, _chevronPaint);
            canvas.DrawLine(dX + dW - 15, dY + 19, dX + dW - 10, dY + 14, _chevronPaint);

            // 两行之间的分隔线
            canvas.DrawLine(216, TITLE_BAR_HEIGHT + 142, WIDTH - 36, TITLE_BAR_HEIGHT + 142, _separatorPaint);

            // ── 第 2 行：匹配方式（仅「通用媒体」下可选，其余平台整行置灰）──
            bool matchEnabled = MediaController.TargetPlatform == "other";
            bool appBoxEnabled = matchEnabled && MediaController.IsManualSessionMatch;
            _uiTextPaint.Color = matchEnabled ? SKColors.White : new SKColor(100, 100, 100);
            canvas.DrawText("匹配方式", 216, PLATFORM_ROW2_Y + 26, _uiTextPaint);
            _uiTextPaint.Color = SKColors.White;
            _subTextPaint.Color = matchEnabled ? new SKColor(170, 170, 170) : new SKColor(80, 80, 80);
            canvas.DrawText("自动匹配或手动指定", 216, PLATFORM_ROW2_Y + 46, _subTextPaint);
            _subTextPaint.Color = new SKColor(170, 170, 170);

            float moX = MATCH_MODE_X, appX = MATCH_APP_X, mBoxY = MATCH_ROW_Y, mBoxW = MATCH_BOX_W, mBoxH = MATCH_BOX_H;

            // 左框：自动匹配 / 手动选择软件
            var moRect = new SKRect(moX, mBoxY, moX + mBoxW, mBoxY + mBoxH);
            _dynamicFillPaint.Color = !matchEnabled ? new SKColor(255, 255, 255, 4)
                : (_matchModeDropdownHovered ? new SKColor(255, 255, 255, 15) : new SKColor(255, 255, 255, 8));
            canvas.DrawRoundRect(moRect, 4, 4, _dynamicFillPaint);
            _dynamicTextPaint.Color = matchEnabled ? SKColors.White : new SKColor(100, 100, 100);
            canvas.DrawText(_matchModeOptions[MediaController.IsManualSessionMatch ? 1 : 0], moX + 10, mBoxY + 21, _dynamicTextPaint);
            _dynamicTextPaint.Color = SKColors.White;
            _chevronPaint.Color = matchEnabled ? new SKColor(150, 150, 150) : new SKColor(90, 90, 90);
            canvas.DrawLine(moX + mBoxW - 20, mBoxY + 14, moX + mBoxW - 15, mBoxY + 19, _chevronPaint);
            canvas.DrawLine(moX + mBoxW - 15, mBoxY + 19, moX + mBoxW - 10, mBoxY + 14, _chevronPaint);

            // 右框：手动模式的目标软件（直接显示 AppID）；自动匹配或非通用媒体时置灰
            var appRect = new SKRect(appX, mBoxY, appX + mBoxW, mBoxY + mBoxH);
            _dynamicFillPaint.Color = appBoxEnabled ? (_appDropdownHovered ? new SKColor(255, 255, 255, 15) : new SKColor(255, 255, 255, 8))
                : new SKColor(255, 255, 255, 4);
            canvas.DrawRoundRect(appRect, 4, 4, _dynamicFillPaint);
            string appLabel = !appBoxEnabled ? "自动匹配"
                : (!MediaController.HasActiveSessions ? ""
                    : (MediaController.ManualSessionAppId.Length > 0 ? MediaController.ManualSessionAppId : "未选择"));
            _dynamicTextPaint.Color = appBoxEnabled ? SKColors.White : new SKColor(100, 100, 100);
            canvas.DrawText(TruncateText(appLabel, _dynamicTextPaint, mBoxW - 32), appX + 10, mBoxY + 21, _dynamicTextPaint);
            _dynamicTextPaint.Color = SKColors.White;
            _chevronPaint.Color = appBoxEnabled ? new SKColor(150, 150, 150) : new SKColor(90, 90, 90);
            canvas.DrawLine(appX + mBoxW - 20, mBoxY + 14, appX + mBoxW - 15, mBoxY + 19, _chevronPaint);
            canvas.DrawLine(appX + mBoxW - 15, mBoxY + 19, appX + mBoxW - 10, mBoxY + 14, _chevronPaint);
            _chevronPaint.Color = new SKColor(150, 150, 150);

            // 歌词设置卡片（合并卡片 208 底 + 14 间距；与 WM_MOUSEMOVE 的 lyricY 同源）
            float lyricY = LYRIC_CARD_Y;
            var lyricRect = new SKRect(200, lyricY, WIDTH - 20, lyricY + 176);
            canvas.DrawRoundRect(lyricRect, 6, 6, _cardBg);
            canvas.DrawRoundRect(lyricRect, 6, 6, _cardBorder);
            canvas.DrawText("歌词设置", 216, lyricY + 26, _uiTextPaint);

            // 歌词开关
            canvas.DrawText("在刘海中显示歌词", 216, lyricY + 52, _subTextPaint);
            float tW = 42, tH = 20;
            float tX = WIDTH - 20 - 16 - tW, tY = lyricY + 37;
            var tRect = new SKRect(tX, tY, tX + tW, tY + tH);
            if (MediaController.IsLyricsEnabled)
            {
                _dynamicFillPaint.Color = _lyricToggleHovered ? new SKColor(0, 140, 240) : new SKColor(0, 120, 212);
                canvas.DrawRoundRect(tRect, tH / 2, tH / 2, _dynamicFillPaint);
                canvas.DrawCircle(tX + tW - tH / 2, tY + tH / 2, tH / 2 - 4, _toggleCirclePaint);
            }
            else
            {
                _dynamicStrokePaint.Color = _lyricToggleHovered ? new SKColor(150, 150, 150) : new SKColor(100, 100, 100);
                canvas.DrawRoundRect(tRect, tH / 2, tH / 2, _dynamicStrokePaint);
                _toggleCirclePaint.Color = _lyricToggleHovered ? new SKColor(200, 200, 200) : new SKColor(150, 150, 150);
                canvas.DrawCircle(tX + tH / 2, tY + tH / 2, tH / 2 - 4, _toggleCirclePaint);
                _toggleCirclePaint.Color = SKColors.White;
            }

            // 翻译歌词开关：译文作为第二行画在原文下方（仅当这句有译文时出现）
            canvas.DrawText("显示翻译歌词（上下两行）", 216, lyricY + 92, _subTextPaint);
            float trY = lyricY + 77;
            var trRect = new SKRect(tX, trY, tX + tW, trY + tH);
            if (MediaController.IsTranslationEnabled)
            {
                _dynamicFillPaint.Color = _transToggleHovered ? new SKColor(0, 140, 240) : new SKColor(0, 120, 212);
                canvas.DrawRoundRect(trRect, tH / 2, tH / 2, _dynamicFillPaint);
                canvas.DrawCircle(tX + tW - tH / 2, trY + tH / 2, tH / 2 - 4, _toggleCirclePaint);
            }
            else
            {
                _dynamicStrokePaint.Color = _transToggleHovered ? new SKColor(150, 150, 150) : new SKColor(100, 100, 100);
                canvas.DrawRoundRect(trRect, tH / 2, tH / 2, _dynamicStrokePaint);
                _toggleCirclePaint.Color = _transToggleHovered ? new SKColor(200, 200, 200) : new SKColor(150, 150, 150);
                canvas.DrawCircle(tX + tH / 2, trY + tH / 2, tH / 2 - 4, _toggleCirclePaint);
                _toggleCirclePaint.Color = SKColors.White;
            }

            // 卡拉OK效果开关
            canvas.DrawText("开启卡拉OK动效", 216, lyricY + 132, _subTextPaint);
            float kY = lyricY + 117;
            var kRect = new SKRect(tX, kY, tX + tW, kY + tH);
            if (MediaController.IsKaraokeEnabled)
            {
                _dynamicFillPaint.Color = _karaokeToggleHovered ? new SKColor(0, 140, 240) : new SKColor(0, 120, 212);
                canvas.DrawRoundRect(kRect, tH / 2, tH / 2, _dynamicFillPaint);
                canvas.DrawCircle(tX + tW - tH / 2, kY + tH / 2, tH / 2 - 4, _toggleCirclePaint);
            }
            else
            {
                _dynamicStrokePaint.Color = _karaokeToggleHovered ? new SKColor(150, 150, 150) : new SKColor(100, 100, 100);
                canvas.DrawRoundRect(kRect, tH / 2, tH / 2, _dynamicStrokePaint);
                _toggleCirclePaint.Color = _karaokeToggleHovered ? new SKColor(200, 200, 200) : new SKColor(150, 150, 150);
                canvas.DrawCircle(tX + tH / 2, kY + tH / 2, tH / 2 - 4, _toggleCirclePaint);
                _toggleCirclePaint.Color = SKColors.White;
            }

            // 延迟调整
            canvas.DrawText("歌词延迟补偿", 216, lyricY + 164, _subTextPaint);
            float cardRightX = WIDTH - 36;
            float btnY = lyricY + 147;

            _dynamicFillPaint.Color = _lyricMinusHovered ? new SKColor(255, 255, 255, 30) : new SKColor(255, 255, 255, 15);
            canvas.DrawRoundRect(new SKRect(cardRightX - 175, btnY, cardRightX - 145, btnY + 24), 4, 4, _dynamicFillPaint);
            canvas.DrawText("-", cardRightX - 164, btnY + 17, _uiTextPaint);

            string valStr = $"{MediaController.LyricDelayOffset:F1} s";
            if (MediaController.LyricDelayOffset > 0) valStr = "+" + valStr;
            float textW = _uiTextPaint.MeasureText(valStr);
            canvas.DrawText(valStr, cardRightX - 90 - textW, btnY + 17, _uiTextPaint);

            _dynamicFillPaint.Color = _lyricPlusHovered ? new SKColor(255, 255, 255, 30) : new SKColor(255, 255, 255, 15);
            canvas.DrawRoundRect(new SKRect(cardRightX - 80, btnY, cardRightX - 50, btnY + 24), 4, 4, _dynamicFillPaint);
            canvas.DrawText("+", cardRightX - 69, btnY + 17, _uiTextPaint);

            _dynamicFillPaint.Color = _lyricResetHovered ? new SKColor(255, 255, 255, 30) : new SKColor(255, 255, 255, 15);
            canvas.DrawRoundRect(new SKRect(cardRightX - 40, btnY, cardRightX, btnY + 24), 4, 4, _dynamicFillPaint);
            canvas.DrawText("重置", cardRightX - 33, btnY + 17, _subTextPaint);
        }

        // 页签：交互设置
        private void RenderTabInteraction(SKCanvas canvas)
        {
            // 🎵🖥 「自动隐藏」与它的两个附属开关（暂停播放后 / 全屏时）**共用同一张卡片**，
            //    所以卡片底要单独画成三行高（186px，yOffset 12..198），再用 DrawToggleRow 画三行内容。
            //    三行 yOffset 12 / 74 / 136（行距 62），行间各一条分隔线，让「附属」关系一眼可见。
            //    两个附属开关**互斥**（见点击处理），所以两行看起来是「二选一」的一组。
            //    ⚠️ 改这里的数值时必须同步改上面 tab 3 的悬停热区（当前 +32/+94/+156/+228/+300）。
            //    （「剪贴板链接检测」已于 2026-09-20 搬到「通用设置」，这里只剩三张卡）
            bool isAutoHideDisabled = Renderer.PassthroughModeEnabled;
            var autoHideCardRect = new SKRect(200, TITLE_BAR_HEIGHT + 12, WIDTH - 20, TITLE_BAR_HEIGHT + 198);
            canvas.DrawRoundRect(autoHideCardRect, 6, 6, _cardBg);
            canvas.DrawRoundRect(autoHideCardRect, 6, 6, _cardBorder);

            DrawToggleRow(canvas, 12, "自动隐藏", isAutoHideDisabled ? "穿透模式下禁止自动隐藏" : "当焦点离开时自动隐藏刘海",
                NotchWindow.IsAutoHideEnabled, _autoHideToggleHovered, isAutoHideDisabled);

            canvas.DrawLine(216, TITLE_BAR_HEIGHT + 70, WIDTH - 36, TITLE_BAR_HEIGHT + 70, _separatorPaint);

            // 附属开关的可用性 = 自动隐藏已开启 且 非穿透模式。
            // 两者任一不满足都置灰并**显示为关闭** —— 与「自动隐藏」在穿透模式下置灰显示为关闭的既有约定一致，
            // 免得出现「开关看着是开的、却怎么都不生效」的困惑。
            bool isPauseHideDisabled = isAutoHideDisabled || !NotchWindow.IsAutoHideEnabled;
            DrawToggleRow(canvas, 74, "暂停播放后自动隐藏",
                isAutoHideDisabled ? "穿透模式下禁止自动隐藏"
                    : !NotchWindow.IsAutoHideEnabled ? "需先开启「自动隐藏」"
                    : "媒体暂停播放时，也把刘海藏起来",
                NotchWindow.IsPauseAutoHideEnabled,
                !isPauseHideDisabled && _pauseHideToggleHovered,
                isPauseHideDisabled);

            canvas.DrawLine(216, TITLE_BAR_HEIGHT + 132, WIDTH - 36, TITLE_BAR_HEIGHT + 132, _separatorPaint);

            // 🖥 「全屏自动隐藏」：检测到全屏视频 / 全屏游戏（含独占 D3D）时无条件让位，播放中也不显示。
            //    与上面那行**互斥**，所以副标题里点明「二者只开一个」。
            bool isFsHideDisabled = isAutoHideDisabled || !NotchWindow.IsAutoHideEnabled;
            DrawToggleRow(canvas, 136, "全屏自动隐藏",
                isAutoHideDisabled ? "穿透模式下禁止自动隐藏"
                    : !NotchWindow.IsAutoHideEnabled ? "需先开启「自动隐藏」"
                    : "检测到全屏视频 / 游戏时隐藏",
                NotchWindow.IsFullscreenAutoHideEnabled,
                !isFsHideDisabled && _fsHideToggleHovered,
                isFsHideDisabled);

            bool isMediaExpDisabled = Renderer.CompositeModeEnabled;
            DrawToggleCard(canvas, 208, "媒体交互方式", isMediaExpDisabled ? "组合模式下固定为直接交互" : "开启为展开交互，关闭为直接交互",
                isMediaExpDisabled ? false : (Renderer.MediaInteractionMode == 1),
                !isMediaExpDisabled && _mediaExpToggleHovered,
                isMediaExpDisabled);

            DrawToggleCard(canvas, 280, "穿透模式", "悬停时透明并允许鼠标穿透本体与底层窗口交互", Renderer.PassthroughModeEnabled, _passToggleHovered);
        }

        // 页签：关于软件
        private void RenderTabAbout(SKCanvas canvas)
        {
            float centerX = 200 + (WIDTH - 200) / 2f;
            float startY = TITLE_BAR_HEIGHT + 30f;

            if (_appIconBitmap != null)
            {
                var iconRect = new SKRect(centerX - 32, startY, centerX + 32, startY + 64);
                canvas.DrawBitmap(_appIconBitmap, iconRect, _hqSamplingOpts);
                startY += 90f;
            }

            _dynamicTextPaint.Color = SKColors.White;
            _dynamicTextPaint.TextSize = 20f;
            _dynamicTextPaint.TextAlign = SKTextAlign.Center;
            canvas.DrawText("NotchPeninsula", centerX, startY, _dynamicTextPaint);
            startY += 22f;

            _dynamicTextPaint.Color = new SKColor(170, 170, 170);
            _dynamicTextPaint.TextSize = 13f;
            string displayVersion = _appTitleWithVersion.Replace("NotchPeninsula ", "NPS v");
            canvas.DrawText(displayVersion, centerX, startY, _dynamicTextPaint);
            startY += 35f;

            string[] links = ["检测更新", "项目仓库", "开发者"];
            _dynamicTextPaint.TextAlign = SKTextAlign.Left;

            float spacing = 15f;
            float totalWidth = _dynamicTextPaint.MeasureText(links[0]) + _dynamicTextPaint.MeasureText(links[1]) + _dynamicTextPaint.MeasureText(links[2]) + (spacing * 2);
            float currentX = centerX - (totalWidth / 2f);

            for (int i = 0; i < links.Length; i++)
            {
                float textWidth = _dynamicTextPaint.MeasureText(links[i]);
                _dynamicTextPaint.Color = _hoveredLinkIndex == i ? new SKColor(0, 140, 240) : new SKColor(0, 120, 212);
                canvas.DrawText(links[i], currentX, startY, _dynamicTextPaint);
                currentX += textWidth + spacing;
            }
        }

        // 页签：个性化中心
        private void RenderTabPersonalize(SKCanvas canvas)
        {
            void DrawMultiCard(float yOffset, string title, string[] subLabels, int[] indices, string unit)
            {
                // 检测该卡片对应的尺寸设置是否已被改动
                bool isModified = false;
                foreach (int index in indices)
                {
                    if (Math.Abs(_customValues[index] - _defaultCustomValues[index]) > 0.001f)
                    {
                        isModified = true;
                        break;
                    }
                }

                float cardHeight = 36 + subLabels.Length * 34;
                var cardRect = new SKRect(200, TITLE_BAR_HEIGHT + yOffset, WIDTH - 20, TITLE_BAR_HEIGHT + yOffset + cardHeight);
                canvas.DrawRoundRect(cardRect, 6, 6, _cardBg);
                canvas.DrawRoundRect(cardRect, 6, 6, _cardBorder);

                canvas.DrawText(title, 216, TITLE_BAR_HEIGHT + yOffset + 26, _uiTextPaint);

                // 如果改动了某个尺寸设置，在标题旁边显示已生效标签
                if (isModified)
                {
                    float titleWidth = _uiTextPaint.MeasureText(title);
                    float tagX = 216 + titleWidth + 10;
                    float tagY = TITLE_BAR_HEIGHT + yOffset + 13;
                    var tagRect = new SKRect(tagX, tagY, tagX + 38, tagY + 18);

                    _dynamicFillPaint.Color = new SKColor(0, 120, 212, 35); // 浅背景颜色
                    canvas.DrawRoundRect(tagRect, 3f, 3f, _dynamicFillPaint); // 小圆角

                    _dynamicTextPaint.TextSize = 10f; // 小文本样式
                    _dynamicTextPaint.Color = new SKColor(0, 140, 240);
                    canvas.DrawText("已生效", tagX + 4, tagY + 13, _dynamicTextPaint);
                    _dynamicTextPaint.TextSize = 13f; // 还原字号，防止污染后续文字渲染
                }

                for (int i = 0; i < subLabels.Length; i++)
                {
                    int index = indices[i];
                    float cardBtnY = GetBtnY(index);

                    canvas.DrawText(subLabels[i], 216, cardBtnY + 17, _subTextPaint);
                    float cardRightX = WIDTH - 36;

                    _dynamicFillPaint.Color = _hoveredMinusIndex == index ? new SKColor(255, 255, 255, 30) : new SKColor(255, 255, 255, 15);
                    canvas.DrawRoundRect(new SKRect(cardRightX - 175, cardBtnY, cardRightX - 145, cardBtnY + 24), 4, 4, _dynamicFillPaint);
                    canvas.DrawText("-", cardRightX - 164, cardBtnY + 17, _uiTextPaint);

                    // 使用静态缓存字符串，零 GC 开销
                    string valStr = _valStrCache[index];
                    float textW = _uiTextPaint.MeasureText(valStr);
                    canvas.DrawText(valStr, cardRightX - 90 - textW, cardBtnY + 17, _uiTextPaint);

                    _dynamicFillPaint.Color = _hoveredPlusIndex == index ? new SKColor(255, 255, 255, 30) : new SKColor(255, 255, 255, 15);
                    canvas.DrawRoundRect(new SKRect(cardRightX - 80, cardBtnY, cardRightX - 50, cardBtnY + 24), 4, 4, _dynamicFillPaint);
                    canvas.DrawText("+", cardRightX - 69, cardBtnY + 17, _uiTextPaint);

                    _dynamicFillPaint.Color = _hoveredResetIndex == index ? new SKColor(255, 255, 255, 30) : new SKColor(255, 255, 255, 15);
                    canvas.DrawRoundRect(new SKRect(cardRightX - 40, cardBtnY, cardRightX, cardBtnY + 24), 4, 4, _dynamicFillPaint);
                    canvas.DrawText("重置", cardRightX - 33, cardBtnY + 17, _subTextPaint);
                }
            }

            // 绘制新增的主题卡片
            float themeY = TITLE_BAR_HEIGHT + 12;
            var themeRect = new SKRect(200, themeY, WIDTH - 20, themeY + 125);
            canvas.DrawRoundRect(themeRect, 6, 6, _cardBg);
            canvas.DrawRoundRect(themeRect, 6, 6, _cardBorder);

            canvas.DrawText("刘海 / 灵动岛主题", 216, themeY + 26, _uiTextPaint);
            canvas.DrawText("背景与文本颜色自适应反转", 216, themeY + 46, _subTextPaint);

            float themeRightX = WIDTH - 36; // 变量隔离
            float btnY = GetBtnY(-1);

            void DrawThemeBtn(int index, string label, float leftOffset, float rightOffset)
            {
                bool isActive = Renderer.ThemeMode == index;
                bool isHovered = _hoveredThemeIndex == index;
                float btnWidth = leftOffset - rightOffset;

                _dynamicFillPaint.Color = (isActive || isHovered) ? new SKColor(255, 255, 255, 30) : new SKColor(255, 255, 255, 15);
                canvas.DrawRoundRect(new SKRect(themeRightX - leftOffset, btnY, themeRightX - rightOffset, btnY + 24), 4, 4, _dynamicFillPaint);

                _dynamicTextPaint.Color = isActive ? new SKColor(0, 140, 240) : SKColors.White;

                // 根据文本真实长度在胶囊内部完美居中
                float textWidth = _dynamicTextPaint.MeasureText(label);
                float textX = themeRightX - leftOffset + (btnWidth - textWidth) / 2f;
                canvas.DrawText(label, textX, btnY + 17, _dynamicTextPaint);
            }

            DrawThemeBtn(0, "黑", 140, 100);
            DrawThemeBtn(1, "白", 90, 50);
            DrawThemeBtn(2, "系统", 40, 0);
            canvas.DrawText("背景透明度", 216, themeY + 75, _subTextPaint);
            float sliderY = themeY + 95;
            float sliderX = 216;
            float sliderW = WIDTH - 40 - 216;
            // 背景透明度滑轨
            canvas.DrawLine(sliderX, sliderY, sliderX + sliderW, sliderY, _separatorPaint);
            float activePx = sliderX + (sliderW / 4) * Renderer.BgOpacityLevel;
            _dynamicStrokePaint.Color = new SKColor(0, 120, 212);
            _dynamicStrokePaint.StrokeWidth = 2f;
            canvas.DrawLine(sliderX, sliderY, activePx, sliderY, _dynamicStrokePaint);
            _dynamicStrokePaint.StrokeWidth = 1.5f;
            bool isOpacityDisabled = Renderer.PassthroughModeEnabled;
            _dynamicStrokePaint.Color = isOpacityDisabled ? new SKColor(80, 80, 80) : new SKColor(0, 120, 212);
            for (int i = 0; i < 5; i++)
            {
                float px = sliderX + (sliderW / 4) * i;
                bool isSelected = Renderer.BgOpacityLevel == i;
                bool isHovered = _hoveredOpacityIndex == i;
                // 只画当前选中的小蓝球，或者鼠标悬停时的半透明反馈，去掉丑陋的灰色固定点
                if (isSelected || isHovered)
                {
                    _dynamicFillPaint.Color = isOpacityDisabled ? new SKColor(100, 100, 100) : (isSelected ? new SKColor(0, 120, 212) : new SKColor(255, 255, 255, 80));
                    canvas.DrawCircle(px, sliderY, isSelected ? 6 : 4, _dynamicFillPaint);
                }
                _dynamicTextPaint.Color = isOpacityDisabled ? new SKColor(100, 100, 100) : (isSelected ? SKColors.White : new SKColor(150, 150, 150));
                _dynamicTextPaint.TextSize = 11f;
                string pct = (i * 25) + "%";
                float tw = _dynamicTextPaint.MeasureText(pct);
                canvas.DrawText(pct, px - tw / 2, sliderY + 18, _dynamicTextPaint);
                _dynamicTextPaint.TextSize = 13f;
            }
            DrawMultiCard(147, "待机显示", ["水平宽度", "垂直高度", "底部圆角"], [0, 1, 7], "px");
            DrawMultiCard(299, "媒体控制", ["激活时宽度", "激活时高度"], [2, 3], "px");
            DrawMultiCard(417, "消息通知", ["弹出的宽度", "弹出的高度"], [4, 5], "px");
            DrawMultiCard(535, "全局 DPI 缩放", ["视觉比例"], [6], "x");
        }

        // 页签：插件中心
        private void RenderTabPlugins(SKCanvas canvas)
        {
            RefreshPluginView();

            // ── 顶部操作卡片 ──
            float topY = TITLE_BAR_HEIGHT + 12;
            var topRect = new SKRect(200, topY, WIDTH - 20, topY + 96);
            canvas.DrawRoundRect(topRect, 6, 6, _cardBg);
            canvas.DrawRoundRect(topRect, 6, 6, _cardBorder);
            canvas.DrawText("插件中心", 216, topY + 26, _uiTextPaint);
            canvas.DrawText("导入第三方 DLL 扩展灵动岛能力，支持热重载", 216, topY + 46, _subTextPaint);

            void DrawPluginButton(int index, string label, float bx, float by, float bw)
            {
                bool hovered = _hoveredPluginAction == index;
                var btn = new SKRect(bx, by, bx + bw, by + 24);
                _dynamicFillPaint.Color = hovered ? new SKColor(0, 140, 240) : new SKColor(0, 120, 212);
                canvas.DrawRoundRect(btn, 4, 4, _dynamicFillPaint);
                float tw = _uiTextPaint.MeasureText(label);
                canvas.DrawText(label, bx + (bw - tw) / 2f, by + 17, _uiTextPaint);
            }

            DrawPluginButton(0, "导入 DLL", 216, topY + 60, 96);
            DrawPluginButton(1, "打开目录", 320, topY + 60, 96);
            DrawPluginButton(2, "插件市场", 424, topY + 60, 96);

            // ── 插件列表卡片 ──
            float listY = topY + 110;
            var listRect = new SKRect(200, listY, WIDTH - 20, HEIGHT - 20);
            canvas.DrawRoundRect(listRect, 6, 6, _cardBg);
            canvas.DrawRoundRect(listRect, 6, 6, _cardBorder);
            canvas.DrawText($"已安装插件 ({_pluginView.Count})", 216, listY + 26, _uiTextPaint);
            // 「显示顺序」一览：← / → 调整的就是这张表里的位置。原生模块与插件同处一表，
            // 把它直接画出来，用户就不会再疑惑「岛上只看得见两个内容，插件为什么是 #4」。
            canvas.DrawText(TruncateText("顺序：" + DescribeContentOrder(), _subTextPaint, WIDTH - 36 - 216),
                216, listY + 46, _subTextPaint);

            const int maxRows = 7;
            // 上行：名称独占整行，可延展至卡片右边界外侧
            // 下行：信息（左）+ 全部操作按钮（右，从左到右：← → 排序 | 重载 | 移除 | 开关）
            float nameTextMax = (WIDTH - 36) - 216;                // 名称几乎全宽
            float infoTextMax = PLUGIN_SORT_LEFT_X - 216 - 8;     // 信息止于排序三角之前

            if (_pluginView.Count == 0)
                canvas.DrawText("暂无插件，点击「导入 DLL」或前往插件市场下载安装", 216, listY + 86, _subTextPaint);

            for (int i = 0; i < Math.Min(_pluginView.Count, maxRows); i++)
            {
                var entry = _pluginView[i];
                float rowY = listY + 64 + i * 56;      // 行高 56，上行名称独占，下行按钮全部一行排列
                if (i > 0) canvas.DrawLine(216, rowY - 6, WIDTH - 36, rowY - 6, _separatorPaint);

                // ═══ 上行：插件名称（独占整行，无按钮遮挡） ═══
                canvas.DrawText(TruncateText(entry.FriendlyName, _uiTextPaint, nameTextMax), 216, rowY + 18, _uiTextPaint);

                // ═══ 下行：信息 + 全部操作按钮（同一行从左到右排列） ═══
                string sub;
                SKColor subColor = new SKColor(170, 170, 170);
                if (entry.State == PluginState.Failed)
                {
                    sub = "加载失败：" + (entry.Error ?? "未知错误");
                    subColor = new SKColor(232, 100, 100);
                }
                else if (entry.State == PluginState.Loaded)
                {
                    sub = string.IsNullOrEmpty(entry.Version) ? "运行中" : $"运行中 · v{entry.Version}";
                }
                else
                {
                    sub = "已禁用 · " + entry.Key;
                }
                // 位置 = 在「内容显示顺序表」里的次序。这张表里同时住着三个原生模块
                // （时间日期 / 硬件占用 / 媒体控制器），所以即便岛上当前只显示了两个内容，
                // 插件也可能是 #4 —— 列表卡片顶部那行「顺序：…」把整张表摊开，一眼就能对上。
                int pos = PluginManager.Instance.GetOrderIndex(entry);
                int total = PluginManager.Instance.Order.Count;
                if (pos > 0) sub += total > 0 ? $" · #{pos}/{total}" : $" · #{pos}";
                _subTextPaint.Color = subColor;
                float infoBaseline = rowY + 40;
                canvas.DrawText(TruncateText(sub, _subTextPaint, infoTextMax), 216, infoBaseline, _subTextPaint);
                _subTextPaint.Color = new SKColor(170, 170, 170);

                // ── 下行按钮（全部在同一行，y 中心 ≈ rowY+38） ──
                const float btnTop = 25f, btnH = 20f;       // 操作按钮矩形（上移 2px，远离底部分割线）

                // 排序箭头 < >（用 SKPath 描边绘制，相对下行按钮区垂直居中）
                void DrawSortArrow(float bx, bool hovered, bool enabled, bool left)
                {
                    using var stroke = new SKPaint
                    {
                        Color = !enabled ? new SKColor(130, 130, 130)
                            : hovered ? SKColors.White
                            : new SKColor(210, 210, 210),
                        Style = SKPaintStyle.Stroke,
                        StrokeWidth = 1.6f,
                        StrokeCap = SKStrokeCap.Round,
                        StrokeJoin = SKStrokeJoin.Round,
                        IsAntialias = true
                    };
                    float cx = bx + SORT_TRI_W / 2f;   // 水平居中于 16px 槽
                    float cy = rowY + 35f;            // 相对下行按钮区（rowY+22..rowY+48）垂直居中
                    float s = 3f, h = 5f;
                    using var path = new SKPath();
                    if (left)
                    {
                        path.MoveTo(cx + s, cy - h);
                        path.LineTo(cx - s, cy);
                        path.LineTo(cx + s, cy + h);
                    }
                    else
                    {
                        path.MoveTo(cx - s, cy - h);
                        path.LineTo(cx + s, cy);
                        path.LineTo(cx - s, cy + h);
                    }
                    canvas.DrawPath(path, stroke);
                }
                DrawSortArrow(PLUGIN_SORT_LEFT_X, _hoveredPluginMoveLeft == i,
                    PluginManager.Instance.CanMoveOrder(entry, -1), true);
                DrawSortArrow(PLUGIN_SORT_RIGHT_X, _hoveredPluginMoveRight == i,
                    PluginManager.Instance.CanMoveOrder(entry, 1), false);

                // 操作按钮（重载 / 移除）+ 开关
                void DrawRowButton(float bx, bool hovered, string label, bool danger)
                {
                    var r = new SKRect(bx, rowY + btnTop, bx + 50, rowY + btnTop + btnH);
                    _dynamicFillPaint.Color = hovered
                        ? (danger ? new SKColor(180, 50, 50) : new SKColor(255, 255, 255, 30))
                        : new SKColor(255, 255, 255, 15);
                    canvas.DrawRoundRect(r, 4, 4, _dynamicFillPaint);
                    float tw = _subTextPaint.MeasureText(label);
                    canvas.DrawText(label, bx + (50 - tw) / 2f, rowY + btnTop + 14, _uiTextPaint);
                }
                DrawRowButton(PLUGIN_BTN_RELOAD_X, _hoveredPluginReload == i, "重载", false);
                DrawRowButton(PLUGIN_BTN_REMOVE_X, _hoveredPluginRemove == i, "移除", true);

                float tW = 42, tH = 20;
                float tX = PLUGIN_BTN_TOGGLE_X, tY = rowY + 26;
                var tRect = new SKRect(tX, tY, tX + tW, tY + tH);
                if (entry.IsEnabled)
                {
                    _dynamicFillPaint.Color = _hoveredPluginToggle == i ? new SKColor(0, 140, 240) : new SKColor(0, 120, 212);
                    canvas.DrawRoundRect(tRect, tH / 2, tH / 2, _dynamicFillPaint);
                    canvas.DrawCircle(tX + tW - tH / 2, tY + tH / 2, tH / 2 - 4, _toggleCirclePaint);
                }
                else
                {
                    _dynamicStrokePaint.Color = _hoveredPluginToggle == i ? new SKColor(150, 150, 150) : new SKColor(100, 100, 100);
                    canvas.DrawRoundRect(tRect, tH / 2, tH / 2, _dynamicStrokePaint);
                    _toggleCirclePaint.Color = _hoveredPluginToggle == i ? new SKColor(200, 200, 200) : new SKColor(150, 150, 150);
                    canvas.DrawCircle(tX + tH / 2, tY + tH / 2, tH / 2 - 4, _toggleCirclePaint);
                    _toggleCirclePaint.Color = SKColors.White;
                }
            }

            if (_pluginView.Count > maxRows)
                canvas.DrawText($"还有 {_pluginView.Count - maxRows} 个插件未显示，可在“打开目录”中管理", 216, HEIGHT - 32, _subTextPaint);
        }

        // 各页签展开的下拉浮层（媒体平台 / 匹配方式 / 目标软件 / 通知内容 / 目标显示器）
        /// <summary>
        /// 画一个展开的下拉列表浮层。行高固定 26，与命中判定里的 <c>/ 26</c> 必须一致。
        /// <paramref name="dimmedIndex"/> 那一项灰显（但仍可点，用于「自定义项失效」这种
        /// 「能点进去重选、但当前值不可用」的场景）。
        /// </summary>
        private void RenderDropdownList(SKCanvas canvas, float x, float yOffset, float w,
                                        string[] options, int selectedIndex, int hoveredIndex, int dimmedIndex)
        {
            // 🔻 浮层高度必须**钳制**在窗口内：
            //    提示音列表是**动态加载**的（data\sound 里丢多少 wav 就有多少项），
            //    若不做上限，16 项 = 416px 就会从提示音卡一直拖到窗口底部之外（40 项更夸张）。
            //    这里保证「浮层底边 ≤ 窗口高 - 12」，超出部分走滚动窗口（见 _dropdownScroll）。
            float dY = TITLE_BAR_HEIGHT + yOffset;
            int total = options.Length;
            int maxRows = Math.Max(1, (int)((HEIGHT - 12 - dY) / 26));
            int visible = Math.Min(total, maxRows);
            float listH = visible * 26;
            var dRect = new SKRect(x, dY, x + w, dY + listH);
            canvas.DrawRoundRect(dRect, 4, 4, _menuBg);
            canvas.DrawRoundRect(dRect, 4, 4, _menuBorder);

            // 滚动：让选中项尽量可见（浮层刚展开时定位到当前选中行）
            int first = 0;
            if (total > visible)
            {
                first = Math.Clamp(_dropdownScroll, 0, total - visible);
                if (selectedIndex >= 0 && (selectedIndex < first || selectedIndex >= first + visible))
                    first = Math.Clamp(selectedIndex - visible / 2, 0, total - visible);
            }

            for (int row = 0; row < visible; row++)
            {
                int i = first + row;
                float itemY = dY + row * 26;
                if (hoveredIndex == i)
                    canvas.DrawRoundRect(new SKRect(x + 2, itemY + 2, x + w - 2, itemY + 24), 3, 3, _tabBgSelected);

                _dynamicTextPaint.Color = i == selectedIndex
                    ? new SKColor(0, 120, 212)
                    : (i == dimmedIndex ? new SKColor(130, 130, 130) : SKColors.White);
                canvas.DrawText(TruncateText(options[i], _dynamicTextPaint, w - 24), x + 12, itemY + 18, _dynamicTextPaint);
            }
            _dynamicTextPaint.Color = SKColors.White;

            // 超出可视区时在右侧画一条滚动条指示，避免用户以为「列表就这么长」
            if (total > visible)
            {
                float trackH = listH - 8;
                float thumbH = Math.Max(18, trackH * visible / total);
                float thumbY = dY + 4 + trackH * first / Math.Max(1, total - visible);
                _dynamicFillPaint.Color = new SKColor(255, 255, 255, 30);
                canvas.DrawRoundRect(new SKRect(x + w - 6, dY + 4, x + w - 3, dY + 4 + trackH), 1.5f, 1.5f, _dynamicFillPaint);
                _dynamicFillPaint.Color = new SKColor(255, 255, 255, 110);
                canvas.DrawRoundRect(new SKRect(x + w - 6, thumbY, x + w - 3, thumbY + thumbH), 1.5f, 1.5f, _dynamicFillPaint);
            }
        }

        private void RenderDropdowns(SKCanvas canvas)
        {
            if (_selectedTab == 2 && _dropdownOpen)
            {
                float mX = WIDTH - 140; float mY = TITLE_BAR_HEIGHT + 130; float mW = 110; float mH = _platforms.Length * 26;
                var mRect = new SKRect(mX, mY, mX + mW, mY + mH);

                canvas.DrawRoundRect(mRect, 4, 4, _menuBg);
                canvas.DrawRoundRect(mRect, 4, 4, _menuBorder);

                for (int i = 0; i < _platforms.Length; i++)
                {
                    float itemY = mY + i * 26;
                    if (_hoveredDropdownIndex == i)
                    {
                        canvas.DrawRoundRect(new SKRect(mX + 2, itemY + 2, mX + mW - 2, itemY + 24), 3, 3, _tabBgSelected);
                    }
                    _dynamicTextPaint.Color = i == _selectedPlatformIndex ? new SKColor(0, 120, 212) : SKColors.White;
                    canvas.DrawText(_platforms[i].Name, mX + 12, itemY + 18, _dynamicTextPaint);
                }
            }

            // 匹配方式下拉菜单（左框）
            if (_selectedTab == 2 && _matchModeDropdownOpen)
            {
                float mX = MATCH_MODE_X; float mY = MATCH_MENU_Y; float mW = MATCH_BOX_W; float mH = _matchModeOptions.Length * 26;
                var mRect = new SKRect(mX, mY, mX + mW, mY + mH);
                canvas.DrawRoundRect(mRect, 4, 4, _menuBg);
                canvas.DrawRoundRect(mRect, 4, 4, _menuBorder);

                int selectedMode = MediaController.IsManualSessionMatch ? 1 : 0;
                for (int i = 0; i < _matchModeOptions.Length; i++)
                {
                    float itemY = mY + i * 26;
                    if (_hoveredMatchModeIndex == i)
                        canvas.DrawRoundRect(new SKRect(mX + 2, itemY + 2, mX + mW - 2, itemY + 24), 3, 3, _tabBgSelected);
                    _dynamicTextPaint.Color = i == selectedMode ? new SKColor(0, 120, 212) : SKColors.White;
                    canvas.DrawText(_matchModeOptions[i], mX + 12, itemY + 18, _dynamicTextPaint);
                }
            }

            // 手动选择软件下拉菜单（右框）：直接展示所有 SMTC 会话的 AppID
            if (_selectedTab == 2 && _appDropdownOpen)
            {
                int rows = Math.Clamp(_appOptions.Length, 1, 8);
                float mW = APP_MENU_W; float mX = MATCH_MENU_RIGHT - mW; float mY = MATCH_MENU_Y; float mH = rows * 26;
                var mRect = new SKRect(mX, mY, mX + mW, mY + mH);
                canvas.DrawRoundRect(mRect, 4, 4, _menuBg);
                canvas.DrawRoundRect(mRect, 4, 4, _menuBorder);

                if (_appOptions.Length == 0)
                {
                    _dynamicTextPaint.Color = new SKColor(150, 150, 150);
                    canvas.DrawText("暂无活动会话", mX + 12, mY + 18, _dynamicTextPaint);
                    _dynamicTextPaint.Color = SKColors.White;
                }
                else
                {
                    for (int i = 0; i < rows; i++)
                    {
                        float itemY = mY + i * 26;
                        if (_hoveredAppIndex == i)
                            canvas.DrawRoundRect(new SKRect(mX + 2, itemY + 2, mX + mW - 2, itemY + 24), 3, 3, _tabBgSelected);
                        _dynamicTextPaint.Color = string.Equals(_appOptions[i], MediaController.ManualSessionAppId, StringComparison.OrdinalIgnoreCase)
                            ? new SKColor(0, 120, 212) : SKColors.White;
                        canvas.DrawText(TruncateText(_appOptions[i], _dynamicTextPaint, mW - 24), mX + 12, itemY + 18, _dynamicTextPaint);
                    }
                    _dynamicTextPaint.Color = SKColors.White;
                }
            }

            // 消息通知内容下拉菜单（通用设置）
            if (_selectedTab == 0 && _toastModeDropdownOpen)
            {
                RenderDropdownList(canvas, TOAST_MODE_CTRL_X, TOAST_MODE_ROW_Y + TOAST_MODE_ROW_H + 2, TOAST_MODE_CTRL_W,
                    _toastModeOptions, _selectedToastModeIndex, _hoveredToastModeIndex, dimmedIndex: -1);
            }

            // 🎵 消息提示音下拉菜单（通用设置）：无 / data\sound 里的每个 wav / 浏览音频…
            if (_selectedTab == 0 && _toastSoundDropdownOpen)
            {
                RenderDropdownList(canvas, SOUND_CTRL_X, SOUND_ROW_Y + SOUND_ROW_H + 2, SOUND_CTRL_W,
                    ToastSoundConfig.BuildOptionLabels(), ToastSoundConfig.SelectedIndex, _hoveredToastSoundIndex,
                    dimmedIndex: ToastSoundConfig.IsUsableFile(ToastSoundConfig.CustomPath, out _)
                        ? -1 : ToastSoundConfig.CustomIndex);
            }

            // 🎵 音量下拉菜单：档位很少（4 档），直接贴着音量框展开
            if (_selectedTab == 0 && _soundVolumeDropdownOpen)
            {
                var volLabels = new string[ToastSoundConfig.VolumeOptions.Length];
                for (int i = 0; i < volLabels.Length; i++) volLabels[i] = $"{ToastSoundConfig.VolumeOptions[i]}%";
                RenderDropdownList(canvas, SOUND_VOL_X, SOUND_ROW_Y + SOUND_ROW_H + 2, SOUND_VOL_W,
                    volLabels, ToastSoundConfig.VolumeIndex, _hoveredSoundVolumeIndex, dimmedIndex: -1);
            }

            // 目标显示器
            if (_selectedTab == 1 && _monitorDropdownOpen)
            {
                float dX = WIDTH - 140; float dY = TITLE_BAR_HEIGHT + 220; float dW = 110; float dH = _monitorOptions.Length * 26;
                var dRect = new SKRect(dX, dY, dX + dW, dY + dH);
                canvas.DrawRoundRect(dRect, 4, 4, _menuBg);
                canvas.DrawRoundRect(dRect, 4, 4, _menuBorder);
                for (int i = 0; i < _monitorOptions.Length; i++)
                {
                    float itemY = dY + i * 26;
                    if (_hoveredMonitorDropdownIndex == i) canvas.DrawRoundRect(new SKRect(dX + 2, itemY + 2, dX + dW - 2, itemY + 24), 3, 3, _tabBgSelected);
                    _dynamicTextPaint.Color = i == Renderer.TargetMonitorIndex ? new SKColor(0, 120, 212) : SKColors.White;
                    canvas.DrawText(_monitorOptions[i], dX + 12, itemY + 18, _dynamicTextPaint);
                }
            }
        }
    }
}
