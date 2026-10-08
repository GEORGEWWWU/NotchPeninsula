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
        // ----  ----
        // ----  ----

        // 侧边栏单个页签：**只画文字**。选中态的底与蓝色竖条由 DrawTabSelection 单独画 ——
        // 它们是一个整体，要能滑到别的行上去。
        private void DrawTab(SKCanvas canvas, int index, string label, float yOffset)
        {
            canvas.DrawText(label, 30, TITLE_BAR_HEIGHT + yOffset + 24, _uiTextPaint);
        }

        // 选中态的整体：背景方块 + 左侧蓝色竖条，一起滑到选中行。
        // 急出缓入位移（先快后慢）。**只有蓝条做「拉丝」**：背景块保持 36 高，方块跟着拉长反而显得不跟手。
        private void DrawTabSelection(SKCanvas canvas)
        {
            float p = TabEase(_tabSlideT);
            float d = _tabSlideToY - _tabSlideFromY;
            float top = _tabSlideFromY + d * p;

            canvas.DrawRoundRect(new SKRect(10, TITLE_BAR_HEIGHT + top, 170, TITLE_BAR_HEIGHT + top + TAB_ROW_H), 4, 4, _tabBgSelected);

            // 蓝条以自身中线为中心对称外扩：中点最强、两端归零，所以起止两端没有跳变。
            // 幅度固定、与滑动距离无关，最大伸展时 20 + 10 = 30 仍稳稳落在 36 高的方块里。
            float stretch = TAB_SLIDE_STRETCH * (float)Math.Sin(Math.PI * Math.Sqrt(Math.Clamp(_tabSlideT, 0f, 1f)));
            float mid = top + TAB_ROW_H * 0.5f;
            float half = (TAB_ROW_H * 0.5f - TAB_BAR_DY) + stretch * 0.5f;

            canvas.DrawRoundRect(new SKRect(10, TITLE_BAR_HEIGHT + mid - half, 13, TITLE_BAR_HEIGHT + mid + half),
                1.5f, 1.5f, _tabIndicator);
        }

        // 急出缓入：先快后慢，到点前自己收住
        private static float TabEase(float t)
        {
            float u = 1f - Math.Clamp(t, 0f, 1f);
            return 1f - u * u * u;
        }

        // 选中页签变了就起一次滑动；正在滑的时候再切，从当前位置接着走，不跳回去。
        // 只在渲染侧边栏时调（唯一入口），所以任何改 _selectedTab 的地方都自动有动画。
        private void SyncTabSlide()
        {
            if (_tabSlideDst < 0)                        // 首次：直接定位，不做入场动画
            {
                _tabSlideDst = _selectedTab;
                _tabSlideFromY = _tabSlideToY = TabRowY(_selectedTab);
                _tabSlideT = 1f;
                return;
            }

            if (_tabSlideDst == _selectedTab) return;

            _tabSlideFromY += (_tabSlideToY - _tabSlideFromY) * TabEase(_tabSlideT);
            _tabSlideToY = TabRowY(_selectedTab);
            _tabSlideDst = _selectedTab;
            _tabSlideStarted = Environment.TickCount64;
            _tabSlideT = 0f;
            StartDisplayHoverAnim();                     // 借用列表行底那张 16ms 表，跑完自己停
        }

        // 画整张卡片底 + 一行开关内容。
        private void DrawToggleCard(SKCanvas canvas, float yOffset, string title, string sub, bool state, bool hovered, bool disabled = false, string? badge = null)
        {
            var cardRect = new SKRect(CONTENT_L, TITLE_BAR_HEIGHT + yOffset, WIDTH - CONTENT_RM, TITLE_BAR_HEIGHT + yOffset + 62);
            canvas.DrawRoundRect(cardRect, 6, 6, _cardBg);
            canvas.DrawRoundRect(cardRect, 6, 6, _cardBorder);
            DrawToggleRow(canvas, yOffset, title, sub, state, hovered, disabled);

            // 徽标跟在标题文字后面（不是钉在卡片角上）：标题改字数时自己跟着挪，
            // 纵向按全页统一的「墨迹中线」对齐，和开关同一套锚定。
            if (!string.IsNullOrEmpty(badge))
            {
                float titleBaseline = TITLE_BAR_HEIGHT + yOffset
                    + (sub.Length > 0 ? ROW_TEXT_BASELINE : ROW_TEXT_BASELINE_SINGLE);
                float left = CONTENT_TEXT_X + _uiTextPaint.MeasureText(title) + BADGE_GAP_X;
                DrawInlineBadge(canvas, left, titleBaseline - TEXT_INK_MID_OFFSET, badge!);
            }
        }

        // 贴纸感的琥珀小标签：圆角矩形 + 一层向下偏 1px 的阴影。
        // 不用红色 —— 红在这套界面里是「错误 / 失效」的语义，实验性是「留意一下」。
        private void DrawInlineBadge(SKCanvas canvas, float left, float centerY, string text)
        {
            float w = _badgeTextPaint.MeasureText(text) + BADGE_PAD_X * 2f;
            float top = centerY - BADGE_H / 2f;
            var rect = new SKRect(left, top, left + w, top + BADGE_H);

            _badgeShadowPaint.Color = new SKColor(0, 0, 0, 60);
            canvas.DrawRoundRect(new SKRect(rect.Left, rect.Top + 1f, rect.Right, rect.Bottom + 1f),
                BADGE_RADIUS, BADGE_RADIUS, _badgeShadowPaint);

            _badgeBgPaint.Color = new SKColor(245, 180, 50);   // 琥珀
            canvas.DrawRoundRect(rect, BADGE_RADIUS, BADGE_RADIUS, _badgeBgPaint);

            _badgeTextPaint.Color = new SKColor(64, 42, 0);    // 深棕字，压在琥珀上
            var fm = _badgeTextPaint.FontMetrics;
            float baseline = rect.MidY - (fm.Ascent + fm.Descent) / 2f;
            canvas.DrawText(text, rect.Left + BADGE_PAD_X, baseline, _badgeTextPaint);
        }

        private void DrawToggleRow(SKCanvas canvas, float yOffset, string title, string sub, bool state, bool hovered, bool disabled = false)
        {
            float titleBaseline = TITLE_BAR_HEIGHT + yOffset
                + (sub.Length > 0 ? ROW_TEXT_BASELINE : ROW_TEXT_BASELINE_SINGLE);

            _uiTextPaint.Color = disabled ? Neutral(100) : _fgColor;
            canvas.DrawText(title, CONTENT_TEXT_X, titleBaseline, _uiTextPaint);
            _uiTextPaint.Color = _fgColor;

            if (sub.Length > 0)
            {
                _subTextPaint.Color = disabled ? Neutral(80) : Neutral(170);
                canvas.DrawText(sub, CONTENT_TEXT_X, titleBaseline + ROW_SUB_OFFSET, _subTextPaint);
                _subTextPaint.Color = Neutral(170);
            }

            float tW = 42; float tH = TOGGLE_TRACK_H; float tX = WIDTH - CONTENT_RM - 16 - tW;
            float tY = TITLE_BAR_HEIGHT + yOffset + ROW_ANCHOR_Y - tH / 2f;
            var tRect = new SKRect(tX, tY, tX + tW, tY + tH);

            if (disabled)
            {
                _dynamicStrokePaint.Color = Neutral(80);
                canvas.DrawRoundRect(tRect, tH / 2, tH / 2, _dynamicStrokePaint);
                _toggleCirclePaint.Color = Neutral(100);
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
                    _dynamicStrokePaint.Color = hovered ? Neutral(150) : Neutral(100);
                    canvas.DrawRoundRect(tRect, tH / 2, tH / 2, _dynamicStrokePaint);
                }

                if (state) canvas.DrawCircle(tX + tW - tH / 2, tY + tH / 2, tH / 2 - 4, _toggleCirclePaint);
                else
                {
                    _toggleCirclePaint.Color = hovered ? Neutral(200) : Neutral(150);
                    canvas.DrawCircle(tX + tH / 2, tY + tH / 2, tH / 2 - 4, _toggleCirclePaint);
                    _toggleCirclePaint.Color = SKColors.White;
                }
            }
        }

        private void DrawSegmented(SKCanvas canvas, float x, float y, float w, float h,
            string[] labels, int selected, int hovered)
        {
            int n = labels.Length;
            float segW = w / n;

            _dynamicFillPaint.Color = Overlay(8);
            canvas.DrawRoundRect(new SKRect(x, y, x + w, y + h), 6, 6, _dynamicFillPaint);

            for (int i = 0; i < n; i++)
            {
                var r = new SKRect(x + i * segW + (i > 0 ? 2f : 0f), y,
                    x + (i + 1) * segW - (i < n - 1 ? 2f : 0f), y + h);
                if (i == selected)
                {
                    _dynamicFillPaint.Color = new SKColor(0, 120, 212);
                    canvas.DrawRoundRect(r, 5, 5, _dynamicFillPaint);
                }
                else if (i == hovered)
                {
                    _dynamicFillPaint.Color = Overlay(24);
                    canvas.DrawRoundRect(r, 5, 5, _dynamicFillPaint);
                }

                // 文本用完立刻恢复基准色：本帧后面还要用同一支画笔
                _uiTextPaint.Color = i == selected ? SKColors.White : _fgColor;
                canvas.DrawText(labels[i], r.MidX - _uiTextPaint.MeasureText(labels[i]) / 2f, r.MidY + 5.5f, _uiTextPaint);
            }
            _uiTextPaint.Color = _fgColor;
        }

        private void DrawScrollBar(SKCanvas canvas, float x, float trackTop, float trackH,
            float thumbRatio, float posRatio, float minThumbH = 18f)
        {
            float alpha = ScrollBarAlpha();
            if (alpha <= 0.01f || trackH <= 0f) return;

            float thumbH = Math.Max(minThumbH, trackH * Math.Clamp(thumbRatio, 0.05f, 1f));
            float travel = Math.Max(1f, trackH - thumbH);
            float thumbY = trackTop + travel * Math.Clamp(posRatio, 0f, 1f);

            _dynamicFillPaint.Color = Overlay((byte)(30 * alpha));
            canvas.DrawRoundRect(new SKRect(x, trackTop, x + 3, trackTop + trackH), 1.5f, 1.5f, _dynamicFillPaint);
            _dynamicFillPaint.Color = Overlay((byte)(140 * alpha));
            canvas.DrawRoundRect(new SKRect(x, thumbY, x + 3, thumbY + thumbH), 1.5f, 1.5f, _dynamicFillPaint);
        }

        // ---- 「显示内容」列表的行悬停动画 ----

        private void StartDisplayHoverAnim()
        {
            if (_displayHoverTimerOn || _hwnd == IntPtr.Zero) return;
            if (Win32.SetTimer(_hwnd, DISPLAY_HOVER_TIMER_ID, DISPLAY_HOVER_TICK_MS, IntPtr.Zero) == IntPtr.Zero) return;
            _displayHoverTimerOn = true;
            // 系统默认 tick 15.625ms，不抬精度的话 16ms 会被取整成 ~31ms（32FPS）。
            // 只在动画期间抬，停下就还回去。
            Win32.TimeBeginPeriod(1);
        }

        private void StopDisplayHoverAnim(IntPtr hwnd)
        {
            if (!_displayHoverTimerOn) return;
            Win32.KillTimer(hwnd, DISPLAY_HOVER_TIMER_ID);
            _displayHoverTimerOn = false;
            Win32.TimeEndPeriod(1);
        }

        private bool TickDisplayHoverAnim()
        {
            bool animating = false;
            for (int i = 0; i < _displayHoverAnim.Length; i++)
            {
                float target = i == _displayHoverRow ? 1f : 0f;
                float cur = _displayHoverAnim[i];
                if (Math.Abs(target - cur) <= 0.01f) { _displayHoverAnim[i] = target; continue; }

                _displayHoverAnim[i] = cur + (target - cur) * DISPLAY_HOVER_EASE;
                animating = true;
            }

            for (int i = 0; i < _hintAnim.Length; i++)
            {
                float target = i == _hintRow ? 1f : 0f;
                float cur = _hintAnim[i];
                if (Math.Abs(target - cur) <= 0.01f) { _hintAnim[i] = target; continue; }

                _hintAnim[i] = cur + (target - cur) * DISPLAY_HOVER_EASE;
                animating = true;
            }

            // 侧边栏：选中块的滑动
            if (_tabSlideT < 1f)
            {
                _tabSlideT = (Environment.TickCount64 - _tabSlideStarted) / TAB_SLIDE_MS;
                if (_tabSlideT > 1f) _tabSlideT = 1f;
                animating = true;
            }

            // 否则会停在 0.99 那种「差一点点」的状态上。
            Render();
            return animating;
        }

        private float GetDisplayHoverProgress(int row)
            => row >= 0 && row < _displayHoverAnim.Length ? _displayHoverAnim[row] : 0f;

        private void DrawMoveArrow(SKCanvas canvas, float slotX, float cy, bool hovered, bool enabled, bool up)
        {
            var stroke = !enabled ? _sortArrowDisabledStroke
                : hovered ? _sortArrowHoverStroke
                : _sortArrowStroke;

            float cx = slotX + SORT_TRI_W / 2f;
            if (hovered && enabled)
            {
                _dynamicFillPaint.Color = Overlay(26);
                canvas.DrawRoundRect(new SKRect(slotX, cy - 11f, slotX + SORT_TRI_W, cy + 11f), 5, 5, _dynamicFillPaint);
            }

            float dir = up ? -1f : 1f;
            float tipY = cy + dir * 5.5f;
            float baseY = cy - dir * 5.5f;
            canvas.DrawLine(cx, baseY, cx, tipY, stroke);
            canvas.DrawLine(cx, tipY, cx - 4f, tipY - dir * 4.5f, stroke);
            canvas.DrawLine(cx, tipY, cx + 4f, tipY - dir * 4.5f, stroke);
        }

        private void DrawDisplayItem(SKCanvas canvas, float y, string name, bool isBuiltin, bool isShown, float hoverP,
            bool enabled, bool hoverUp, bool hoverDown, bool canUp, bool canDown)
        {
            if (enabled && hoverP > 0.01f)
            {
                _dynamicFillPaint.Color = Overlay((byte)(28 * hoverP));
                canvas.DrawRoundRect(new SKRect(DISPLAY_ITEM_L, y, DISPLAY_ITEM_R, y + DISPLAY_ITEM_H), 6, 6, _dynamicFillPaint);
            }

            const float boxS = 16f;
            float boxX = DISPLAY_ITEM_L + 10f;
            float boxY = y + (DISPLAY_ITEM_H - boxS) / 2f;
            var box = new SKRect(boxX, boxY, boxX + boxS, boxY + boxS);
            if (isShown)
            {
                _dynamicFillPaint.Color = enabled ? new SKColor(0, 120, 212) : Neutral(80);
                canvas.DrawRoundRect(box, 4, 4, _dynamicFillPaint);
                var tick = enabled ? _displayTickPaint : _sortArrowDisabledStroke;
                canvas.DrawLine(boxX + 4f, boxY + 8.4f, boxX + 6.8f, boxY + 11.2f, tick);
                canvas.DrawLine(boxX + 6.8f, boxY + 11.2f, boxX + 12f, boxY + 5f, tick);
            }
            else
            {
                _dynamicStrokePaint.Color = !enabled ? Neutral(60) : hoverP > 0.5f ? Neutral(170) : Neutral(110);
                canvas.DrawRoundRect(box, 4, 4, _dynamicStrokePaint);
            }

            const string tag = "内置";
            float tagW = isBuiltin ? _subTextPaint.MeasureText(tag) + 14f : 0f;
            float textX = box.Right + 10f;
            string shownName = TruncateText(name, _uiTextPaint, DISPLAY_MOVE_UP_X - 12f - textX - tagW);
            _uiTextPaint.Color = !enabled ? Neutral(100) : isShown ? _fgColor : Neutral(150);
            canvas.DrawText(shownName, textX, y + 20.5f, _uiTextPaint);
            _uiTextPaint.Color = _fgColor;

            if (isBuiltin)
            {
                float tagX = textX + _uiTextPaint.MeasureText(shownName) + 8f;
                _dynamicFillPaint.Color = enabled ? new SKColor(0, 120, 212, 40) : Overlay(10);
                canvas.DrawRoundRect(new SKRect(tagX, y + 7f, tagX + tagW, y + 23f), 8, 8, _dynamicFillPaint);
                _subTextPaint.Color = enabled ? new SKColor(0, 140, 240) : Neutral(90);
                canvas.DrawText(tag, tagX + 7f, y + 19f, _subTextPaint);
                _subTextPaint.Color = Neutral(170);
            }

            float cy = y + DISPLAY_ITEM_H / 2f;
            DrawMoveArrow(canvas, DISPLAY_MOVE_UP_X, cy, hoverUp, enabled && canUp, true);
            DrawMoveArrow(canvas, DISPLAY_MOVE_DOWN_X, cy, hoverDown, enabled && canDown, false);
        }

        private static readonly SKPaint _displayTickPaint = new()
        {
            Color = SKColors.White,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 1.7f,
            StrokeCap = SKStrokeCap.Round,
            StrokeJoin = SKStrokeJoin.Round,
            IsAntialias = true
        };

        // 别删那三行，否则浅色外观下箭头会变成浅灰画在浅底上。
        private static readonly SKPaint _sortArrowStroke = new()
        {
            Color = new SKColor(210, 210, 210),
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 1.6f,
            StrokeCap = SKStrokeCap.Round,
            StrokeJoin = SKStrokeJoin.Round,
            IsAntialias = true
        };

        private static readonly SKPaint _sortArrowHoverStroke = new()
        {
            Color = SKColors.White,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 1.6f,
            StrokeCap = SKStrokeCap.Round,
            StrokeJoin = SKStrokeJoin.Round,
            IsAntialias = true
        };

        private static readonly SKPaint _sortArrowDisabledStroke = new()
        {
            Color = new SKColor(130, 130, 130),
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 1.6f,
            StrokeCap = SKStrokeCap.Round,
            StrokeJoin = SKStrokeJoin.Round,
            IsAntialias = true
        };

        // 侧边栏重排与分割线绘制
        private void RenderSidebar(SKCanvas canvas)
        {
            SyncTabSlide();
            DrawTabSelection(canvas);   // 先铺选中块：它在悬停底与文字之下，才不会被盖住

            // 个性化中心最上，两条分割线（分割线 = 上一区块最后一行行首 + 42）
            DrawTab(canvas, 5, "个性化中心", TabRowY(5));
            canvas.DrawLine(20, TITLE_BAR_HEIGHT + TabRowY(5) + 42f, 160, TITLE_BAR_HEIGHT + TabRowY(5) + 42f, _separatorPaint);
            DrawTab(canvas, 0, "通用设置", TabRowY(0));
            DrawTab(canvas, 1, "显示设置", TabRowY(1));
            DrawTab(canvas, 2, "媒体设置", TabRowY(2));
            DrawTab(canvas, 3, "交互设置", TabRowY(3));
            canvas.DrawLine(20, TITLE_BAR_HEIGHT + TabRowY(3) + 42f, 160, TITLE_BAR_HEIGHT + TabRowY(3) + 42f, _separatorPaint);
            DrawTab(canvas, 6, "我的插件", TabRowY(6));
            DrawTab(canvas, 7, "插件市场", TabRowY(7));
            canvas.DrawLine(20, TITLE_BAR_HEIGHT + TabRowY(7) + 42f, 160, TITLE_BAR_HEIGHT + TabRowY(7) + 42f, _separatorPaint);
            DrawTab(canvas, 4, "关于软件", TabRowY(4));
        }

        // 页签：通用设置
        private void RenderTabGeneral(SKCanvas canvas)
        {
            DrawToggleCard(canvas, 12, "开机自启", "跟随系统启动自动运行该程序", _isAutoStartEnabled, _toggleHovered);
            // 窗口置顶（与下方消息通知整组互换位置）
            DrawToggleCard(canvas, 84, "窗口置顶", "开启后刘海将始终保持在其他窗口最上层", NotchWindow.IsTopmostEnabled, _topmostToggleHovered);
            //    分隔线 +TOAST_SEP_Y (222)
            //    分隔线 +SOUND_SEP_Y (284)
            var notifyCardRect = new SKRect(CONTENT_L, TITLE_BAR_HEIGHT + TOAST_ROW1_Y, WIDTH - CONTENT_RM, TITLE_BAR_HEIGHT + TOAST_CARD_BOTTOM);
            canvas.DrawRoundRect(notifyCardRect, 6, 6, _cardBg);
            canvas.DrawRoundRect(notifyCardRect, 6, 6, _cardBorder);

            // ── 行 1：总开关 ──
            DrawToggleRow(canvas, TOAST_ROW1_Y, "系统消息通知", "允许在刘海中显示Windows系统的Toast消息", NotchWindow.IsToastEnabled, _toastToggleHovered);

            canvas.DrawLine(CONTENT_TEXT_X, TITLE_BAR_HEIGHT + TOAST_SEP_Y, WIDTH - CONTENT_TEXT_RM, TITLE_BAR_HEIGHT + TOAST_SEP_Y, _separatorPaint);

            canvas.DrawText("消息通知内容", CONTENT_TEXT_X, TITLE_BAR_HEIGHT + TOAST_ROW2_TITLE_Y, _uiTextPaint);
            string toastModeDesc = _selectedToastModeIndex switch
            {
                2 => "完整显示应用名、发送者与消息主体",
                1 => "右侧展示“现在”与应用名",
                _ => "仅显示发送者与消息主体"
            };
            canvas.DrawText(toastModeDesc, CONTENT_TEXT_X, TITLE_BAR_HEIGHT + TOAST_ROW2_DESC_Y, _subTextPaint);

            DrawDropdownBox(canvas, TOAST_MODE_CTRL_X, TOAST_MODE_ROW_Y, TOAST_MODE_CTRL_W, TOAST_MODE_ROW_H,
                _toastModeOptions[_selectedToastModeIndex], _toastModeDropdownHovered, enabled: true);

            canvas.DrawLine(CONTENT_TEXT_X, TITLE_BAR_HEIGHT + SOUND_SEP_Y, WIDTH - CONTENT_TEXT_RM, TITLE_BAR_HEIGHT + SOUND_SEP_Y, _separatorPaint);

            // ── 行 3：消息提示音开关 ──
            DrawToggleRow(canvas, SOUND_ROW3_Y, "消息提示音",
                _soundHint.Length > 0 ? _soundHint : "新消息到达时播放提示音",
                ToastSoundConfig.IsEnabled, _soundToggleHovered);

            // ── 行 4：提示音设置（行 3 的附属，无开关）──
            bool soundRowEnabled = ToastSoundConfig.IsRowEnabled;
            bool soundReady = ToastSoundConfig.IsSourceReady;

            if (!soundRowEnabled) _uiTextPaint.Color = Neutral(100);
            canvas.DrawText("提示音", SOUND_LABEL_X, TITLE_BAR_HEIGHT + SOUND_ROW_TITLE_Y, _uiTextPaint);
            if (!soundRowEnabled) _uiTextPaint.Color = _fgColor;

            DrawDropdownBox(canvas, SOUND_CTRL_X, SOUND_BOX_Y, SOUND_CTRL_W, SOUND_ROW_H,
                ToastSoundConfig.CurrentDisplayText(), _toastSoundDropdownHovered, enabled: soundRowEnabled);

            DrawDropdownBox(canvas, SOUND_VOL_X, SOUND_BOX_Y, SOUND_VOL_W, SOUND_ROW_H,
                $"{ToastSoundConfig.VolumePercent}%", _soundVolumeDropdownHovered, enabled: soundReady);

            void DrawSoundButton(bool hovered, string label, float bx, float bw, bool enabled)
            {
                var btn = new SKRect(bx, TITLE_BAR_HEIGHT + SOUND_BTN_Y, bx + bw, TITLE_BAR_HEIGHT + SOUND_BTN_Y + SOUND_BTN_H);
                bool hot = enabled && hovered;
                if (!enabled) _dynamicFillPaint.Color = Overlay(6);
                else _dynamicFillPaint.Color = hot ? new SKColor(0, 140, 240) : new SKColor(0, 120, 212);
                canvas.DrawRoundRect(btn, 4, 4, _dynamicFillPaint);
                float tw = _uiTextPaint.MeasureText(label);
                _uiTextPaint.Color = enabled ? SKColors.White : Neutral(110);
                canvas.DrawText(label, bx + (bw - tw) / 2f, TITLE_BAR_HEIGHT + SOUND_BTN_Y + 17, _uiTextPaint);
                _uiTextPaint.Color = _fgColor;
            }

            DrawSoundButton(_soundPreviewHovered, "试听", SOUND_PREVIEW_X, SOUND_BTN_W, soundReady);
            DrawSoundButton(_soundResetHovered, "重置", SOUND_RESET_X, SOUND_BTN_W, soundReady);

            // MSP 接入（实验性）：把灵动岛暴露成本机 MSP 节点，外部程序可弹通知、读控媒体。
            // 默认关闭 —— 它会监听一个本地端口，属于对外接口面，得用户自己点头。
            DrawToggleCard(canvas, MSP_CARD_Y, "MSP 接入",
                "让本机 MSP 程序弹通知、读控媒体",
                MspNotchBridge.IsEnabled, _mspToggleHovered, badge: "实验性");

            var fontCard = new SKRect(CONTENT_L, TITLE_BAR_HEIGHT + FONT_CARD_Y, WIDTH - CONTENT_RM, TITLE_BAR_HEIGHT + FONT_CARD_Y + 62);
            canvas.DrawRoundRect(fontCard, 6, 6, _cardBg);
            canvas.DrawRoundRect(fontCard, 6, 6, _cardBorder);
            canvas.DrawText("切换灵动岛字体", CONTENT_TEXT_X, TITLE_BAR_HEIGHT + FONT_CARD_Y + ROW_TEXT_BASELINE, _uiTextPaint);

            bool fontError = _fontHint.Length > 0;
            string fontSub = fontError ? _fontHint : $"当前：{FontConfig.DisplayName}";
            _subTextPaint.Color = fontError ? new SKColor(232, 100, 100) : Neutral(170);
            canvas.DrawText(TruncateText(fontSub, _subTextPaint, FONT_PICK_X - CONTENT_TEXT_X - 8), CONTENT_TEXT_X,
                TITLE_BAR_HEIGHT + FONT_CARD_Y + ROW_TEXT_BASELINE + ROW_SUB_OFFSET, _subTextPaint);
            _subTextPaint.Color = Neutral(170);

            void DrawFontButton(bool hovered, string label, float bx, float bw)
            {
                var btn = new SKRect(bx, TITLE_BAR_HEIGHT + FONT_BTN_Y, bx + bw, TITLE_BAR_HEIGHT + FONT_BTN_Y + FONT_BTN_H);
                _dynamicFillPaint.Color = hovered ? new SKColor(0, 140, 240) : new SKColor(0, 120, 212);
                canvas.DrawRoundRect(btn, 4, 4, _dynamicFillPaint);
                float tw = _uiTextPaint.MeasureText(label);
                _uiTextPaint.Color = SKColors.White;   // 蓝底白字（浅色外观下 _fgColor 是黑的，压蓝底看不清）
                canvas.DrawText(label, bx + (bw - tw) / 2f, TITLE_BAR_HEIGHT + FONT_BTN_Y + 18, _uiTextPaint);
                _uiTextPaint.Color = _fgColor;
            }

            DrawFontButton(_fontPickHovered, "选择字体", FONT_PICK_X, FONT_PICK_W);
            DrawFontButton(_fontResetHovered, "重置", FONT_RESET_X, FONT_RESET_W);
        }

        private void DrawDropdownBox(SKCanvas canvas, float x, float yOffset, float w, float h,
                                     string text, bool hovered, bool enabled)
        {
            var rect = new SKRect(x, TITLE_BAR_HEIGHT + yOffset, x + w, TITLE_BAR_HEIGHT + yOffset + h);
            float bgAlpha = enabled ? (hovered ? 15 : 8) : 4;
            _dynamicFillPaint.Color = Overlay((byte)bgAlpha);
            canvas.DrawRoundRect(rect, 4, 4, _dynamicFillPaint);

            if (!enabled) _uiTextPaint.Color = Neutral(110);
            canvas.DrawText(TruncateText(text, _uiTextPaint, w - 26), rect.Left + 10, rect.Top + h / 2f + 5, _uiTextPaint);
            if (!enabled) _uiTextPaint.Color = _fgColor;

            float cx = rect.Right - 18;
            float cy = rect.MidY;
            if (!enabled) _chevronPaint.Color = Neutral(90);
            canvas.DrawLine(cx, cy - 2.5f, cx + 5, cy + 2.5f, _chevronPaint);
            canvas.DrawLine(cx + 5, cy + 2.5f, cx + 10, cy - 2.5f, _chevronPaint);
            if (!enabled) _chevronPaint.Color = Neutral(160);
        }

        // 页签：显示设置
        private void RenderTabDisplay(SKCanvas canvas)
        {
            float page = -_displayPageScroll;

            var styleCardRect = new SKRect(CONTENT_L, TITLE_BAR_HEIGHT + STYLE_CARD_Y + page,
                WIDTH - CONTENT_RM, TITLE_BAR_HEIGHT + STYLE_CARD_Y + STYLE_CARD_H + page);
            canvas.DrawRoundRect(styleCardRect, 6, 6, _cardBg);
            canvas.DrawRoundRect(styleCardRect, 6, 6, _cardBorder);

            void DrawStyleOption(int index, string name, float x, float y)
            {
                bool isSelected = Renderer.NotchStyle == index;
                bool isHovered = _hoveredStyleIndex == index;

                // 选项外框与背景反馈
                var optRect = new SKRect(x, y, x + STYLE_OPT_W, y + STYLE_OPT_H);
                _dynamicFillPaint.Color = isSelected ? new SKColor(0, 120, 212, 40) : (isHovered ? Overlay(15) : Overlay(8));
                canvas.DrawRoundRect(optRect, 6, 6, _dynamicFillPaint);
                _dynamicStrokePaint.Color = isSelected ? new SKColor(0, 120, 212) : Neutral(80);
                canvas.DrawRoundRect(optRect, 6, 6, _dynamicStrokePaint);

                // 绘制纯血 Skia 伪 PNG 视觉特效图
                float cx = x + 75; float cy = y + 35;

                _dynamicFillPaint.Color = IsLightPreviewCapsule() ? SKColors.White : SKColors.Black;

                if (index == 0) // 调整经典刘海的矢量绘图比例，使其视觉高度和灵动岛保持一致
                {
                    // 漏掉 it 就是"每次重绘泄漏一个原生路径"。
                    using var path = new SKPath();
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
                _dynamicTextPaint.Color = isSelected ? new SKColor(0, 140, 240) : _fgColor;
                canvas.DrawText(name, cx - 15, radioY + 1, _dynamicTextPaint);
            }

            DrawStyleOption(0, "经典刘海", STYLE_OPT_X, TITLE_BAR_HEIGHT + STYLE_OPT_Y + page);
            DrawStyleOption(1, "悬浮灵动岛", STYLE_OPT_X + STYLE_OPT_W + STYLE_OPT_GAP, TITLE_BAR_HEIGHT + STYLE_OPT_Y + page);

            float monitorRowY = TITLE_BAR_HEIGHT + MONITOR_ROW_Y + page;
            canvas.DrawText("目标显示器", CONTENT_TEXT_X, monitorRowY + ROW_LABEL_DY, _uiTextPaint);
            float mdX = WIDTH - CONTENT_RM - MONITOR_DD_W;
            float mdY = monitorRowY + (ROW_H - MONITOR_DD_H) / 2f;
            _dynamicFillPaint.Color = _monitorDropdownHovered ? Overlay(15) : Overlay(8);
            canvas.DrawRoundRect(new SKRect(mdX, mdY, mdX + MONITOR_DD_W, mdY + MONITOR_DD_H), 4, 4, _dynamicFillPaint);
            string mName = Renderer.TargetMonitorIndex < _monitorOptions.Length ? _monitorOptions[Renderer.TargetMonitorIndex] : "未知";
            canvas.DrawText(mName, mdX + 10, mdY + 21, _uiTextPaint);
            canvas.DrawLine(mdX + MONITOR_DD_W - 20, mdY + 14, mdX + MONITOR_DD_W - 15, mdY + 19, _chevronPaint);
            canvas.DrawLine(mdX + MONITOR_DD_W - 15, mdY + 19, mdX + MONITOR_DD_W - 10, mdY + 14, _chevronPaint);

            // ── 显示模式（单行分段器）：待机 / 普通 ──
            float modeRowY = TITLE_BAR_HEIGHT + MODE_ROW_Y + page;
            canvas.DrawText("显示模式", CONTENT_TEXT_X, modeRowY + ROW_LABEL_DY, _uiTextPaint);
            DrawSegmented(canvas, MODE_SEG_X, modeRowY + (ROW_H - SEG_H) / 2f, MODE_SEG_W, SEG_H,
                ["待机模式", "普通模式"], Renderer.StandbyActive ? 0 : 1, _hoveredDisplayModeIndex);

            float sceneRowY = TITLE_BAR_HEIGHT + SCENE_ROW_Y + page;
            canvas.DrawText("待机显示内容", CONTENT_TEXT_X, sceneRowY + ROW_LABEL_DY, _uiTextPaint);
            DrawSegmented(canvas, SCENE_SEG_X, sceneRowY + (ROW_H - SEG_H) / 2f, SCENE_SEG_W, SEG_H,
                ["时间", "空白", "媒体控制"], Renderer.StandbyScene - 1, _hoveredStandbySceneIndex - 1);

            //（见 Core/NotchWindow 的双击分支）。
            DrawToggleRow(canvas, TOGGLE_ROW_Y + page,
                "双击空白切换待机模式", "",
                Renderer.StandbyToggleByDoubleClick, _standbyToggleHovered);

            // ── 显示内容列表 ──
            // 靠「悬停才亮的那层底」表达可点，视觉上跟整页连成一体。
            float contentCardY = TITLE_BAR_HEIGHT + DISPLAY_CARD_Y + page;
            bool listEnabled = !Renderer.StandbyActive;

            var displayItems = PluginManager.Instance.DisplayItems;
            GetDisplayListLayout(out int visibleRows, out int maxFirstRow);
            _displayScroll = Math.Clamp(_displayScroll, 0, maxFirstRow);
            if (displayItems.Count == 0)
                canvas.DrawText("暂无可显示的内容", DISPLAY_ITEM_L + 10f, contentCardY + DISPLAY_FIRST_ROW_Y + 20, _subTextPaint);

            for (int slot = 0; slot < visibleRows; slot++)
            {
                int i = _displayScroll + slot;
                var item = displayItems[i];
                float rowY = contentCardY + DISPLAY_FIRST_ROW_Y + slot * DISPLAY_ROW_H;

                DrawDisplayItem(canvas, rowY, item.Name, item.IsBuiltin, item.IsShown,
                    listEnabled ? GetDisplayHoverProgress(slot) : 0f, listEnabled,
                    _hoveredDisplayMoveUp == i, _hoveredDisplayMoveDown == i,
                    PluginManager.Instance.CanMoveDisplay(item.Key, -1),
                    PluginManager.Instance.CanMoveDisplay(item.Key, 1));
            }

            if (maxFirstRow > 0 && visibleRows > 0)
            {
                GetListScrollbarLayout(out float listTrackTop, out float listTrackH);
                DrawScrollBar(canvas, WIDTH - 14, listTrackTop, listTrackH,
                    visibleRows / (float)displayItems.Count, _displayScroll / (float)maxFirstRow);
            }

            float pageMaxScroll = GetDisplayPageMaxScroll();
            if (pageMaxScroll > 0f)
            {
                GetPageScrollbarLayout(out float pageTrackTop, out float pageTrackH);
                float contentH = TITLE_BAR_HEIGHT + DISPLAY_CARD_Y + DISPLAY_CARD_H + 20f;
                DrawScrollBar(canvas, WIDTH - 8, pageTrackTop, pageTrackH,
                    HEIGHT / contentH, _displayPageScroll / pageMaxScroll, minThumbH: 24f);
            }
        }

        // 页签：媒体设置
        private void RenderTabMedia(SKCanvas canvas)
        {
            DrawToggleCard(canvas, 12, "媒体控制", "允许在刘海中显示和控制系统媒体播放", MediaController.IsMediaControlEnabled, _mediaToggleHovered);

            var platformCardRect = new SKRect(CONTENT_L, PLATFORM_CARD_Y, WIDTH - CONTENT_RM, PLATFORM_CARD_Y + 124);
            canvas.DrawRoundRect(platformCardRect, 6, 6, _cardBg);
            canvas.DrawRoundRect(platformCardRect, 6, 6, _cardBorder);

            // ── 第 1 行：目标媒体平台 ──
            canvas.DrawText("目标媒体平台", CONTENT_TEXT_X, TITLE_BAR_HEIGHT + 110, _uiTextPaint);
            canvas.DrawText(MediaController.TargetPlatform == "browser" ? "仅接管浏览器内的播放会话" : "多平台共存时，优先截获并接管的平台",
                CONTENT_TEXT_X, TITLE_BAR_HEIGHT + 130, _subTextPaint);

            float dW = 110; float dX = WIDTH - 140; float dY = TITLE_BAR_HEIGHT + 96; float dH = 32;
            var dRect = new SKRect(dX, dY, dX + dW, dY + dH);
            _dynamicFillPaint.Color = _dropdownHovered ? Overlay(15) : Overlay(8);
            canvas.DrawRoundRect(dRect, 4, 4, _dynamicFillPaint);
            canvas.DrawText(_platforms[_selectedPlatformIndex].Name, dX + 10, dY + 21, _uiTextPaint);

            canvas.DrawLine(dX + dW - 20, dY + 14, dX + dW - 15, dY + 19, _chevronPaint);
            canvas.DrawLine(dX + dW - 15, dY + 19, dX + dW - 10, dY + 14, _chevronPaint);

            // 两行之间的分隔线
            canvas.DrawLine(CONTENT_TEXT_X, TITLE_BAR_HEIGHT + 142, WIDTH - CONTENT_TEXT_RM, TITLE_BAR_HEIGHT + 142, _separatorPaint);

            bool matchEnabled = MediaController.TargetPlatform == "other";
            bool appBoxEnabled = matchEnabled && MediaController.IsManualSessionMatch;
            _uiTextPaint.Color = matchEnabled ? _fgColor : Neutral(100);
            canvas.DrawText("匹配方式", CONTENT_TEXT_X, PLATFORM_ROW2_Y + 26, _uiTextPaint);
            _uiTextPaint.Color = _fgColor;
            _subTextPaint.Color = matchEnabled ? Neutral(170) : Neutral(80);
            canvas.DrawText("自动匹配或手动指定", CONTENT_TEXT_X, PLATFORM_ROW2_Y + 46, _subTextPaint);
            _subTextPaint.Color = Neutral(170);

            float moX = MATCH_MODE_X, appX = MATCH_APP_X, mBoxY = MATCH_ROW_Y, mBoxW = MATCH_BOX_W, mBoxH = MATCH_BOX_H;

            // 左框：自动匹配 / 手动选择软件
            var moRect = new SKRect(moX, mBoxY, moX + mBoxW, mBoxY + mBoxH);
            _dynamicFillPaint.Color = !matchEnabled ? Overlay(4)
                : (_matchModeDropdownHovered ? Overlay(15) : Overlay(8));
            canvas.DrawRoundRect(moRect, 4, 4, _dynamicFillPaint);
            _dynamicTextPaint.Color = matchEnabled ? _fgColor : Neutral(100);
            canvas.DrawText(_matchModeOptions[MediaController.IsManualSessionMatch ? 1 : 0], moX + 10, mBoxY + 21, _dynamicTextPaint);
            _dynamicTextPaint.Color = _fgColor;
            _chevronPaint.Color = matchEnabled ? Neutral(150) : Neutral(90);
            canvas.DrawLine(moX + mBoxW - 20, mBoxY + 14, moX + mBoxW - 15, mBoxY + 19, _chevronPaint);
            canvas.DrawLine(moX + mBoxW - 15, mBoxY + 19, moX + mBoxW - 10, mBoxY + 14, _chevronPaint);

            var appRect = new SKRect(appX, mBoxY, appX + mBoxW, mBoxY + mBoxH);
            _dynamicFillPaint.Color = appBoxEnabled ? (_appDropdownHovered ? Overlay(15) : Overlay(8))
                : Overlay(4);
            canvas.DrawRoundRect(appRect, 4, 4, _dynamicFillPaint);
            string appLabel = !appBoxEnabled ? "自动匹配"
                : (!MediaController.HasActiveSessions ? ""
                    : (MediaController.ManualSessionAppId.Length > 0 ? MediaController.ManualSessionAppId : "未选择"));
            _dynamicTextPaint.Color = appBoxEnabled ? _fgColor : Neutral(100);
            canvas.DrawText(TruncateText(appLabel, _dynamicTextPaint, mBoxW - 32), appX + 10, mBoxY + 21, _dynamicTextPaint);
            _dynamicTextPaint.Color = _fgColor;
            _chevronPaint.Color = appBoxEnabled ? Neutral(150) : Neutral(90);
            canvas.DrawLine(appX + mBoxW - 20, mBoxY + 14, appX + mBoxW - 15, mBoxY + 19, _chevronPaint);
            canvas.DrawLine(appX + mBoxW - 15, mBoxY + 19, appX + mBoxW - 10, mBoxY + 14, _chevronPaint);
            _chevronPaint.Color = Neutral(150);

            float lyricY = LYRIC_CARD_Y;
            var lyricRect = new SKRect(CONTENT_L, lyricY, WIDTH - CONTENT_RM, lyricY + 176);
            canvas.DrawRoundRect(lyricRect, 6, 6, _cardBg);
            canvas.DrawRoundRect(lyricRect, 6, 6, _cardBorder);
            canvas.DrawText("歌词设置", CONTENT_TEXT_X, lyricY + 26, _uiTextPaint);

            // 歌词开关
            canvas.DrawText("在刘海中显示歌词", CONTENT_TEXT_X, lyricY + 52, _subTextPaint);
            float tW = 42, tH = 20;
            float tX = WIDTH - CONTENT_RM - 16 - tW, tY = lyricY + 37;
            var tRect = new SKRect(tX, tY, tX + tW, tY + tH);
            if (MediaController.IsLyricsEnabled)
            {
                _dynamicFillPaint.Color = _lyricToggleHovered ? new SKColor(0, 140, 240) : new SKColor(0, 120, 212);
                canvas.DrawRoundRect(tRect, tH / 2, tH / 2, _dynamicFillPaint);
                canvas.DrawCircle(tX + tW - tH / 2, tY + tH / 2, tH / 2 - 4, _toggleCirclePaint);
            }
            else
            {
                _dynamicStrokePaint.Color = _lyricToggleHovered ? Neutral(150) : Neutral(100);
                canvas.DrawRoundRect(tRect, tH / 2, tH / 2, _dynamicStrokePaint);
                _toggleCirclePaint.Color = _lyricToggleHovered ? Neutral(200) : Neutral(150);
                canvas.DrawCircle(tX + tH / 2, tY + tH / 2, tH / 2 - 4, _toggleCirclePaint);
                _toggleCirclePaint.Color = SKColors.White;
            }

            canvas.DrawText("显示翻译歌词（上下两行）", CONTENT_TEXT_X, lyricY + 92, _subTextPaint);
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
                _dynamicStrokePaint.Color = _transToggleHovered ? Neutral(150) : Neutral(100);
                canvas.DrawRoundRect(trRect, tH / 2, tH / 2, _dynamicStrokePaint);
                _toggleCirclePaint.Color = _transToggleHovered ? Neutral(200) : Neutral(150);
                canvas.DrawCircle(tX + tH / 2, trY + tH / 2, tH / 2 - 4, _toggleCirclePaint);
                _toggleCirclePaint.Color = SKColors.White;
            }

            canvas.DrawText("逐字歌词（不可用时自动回退卡拉OK）", CONTENT_TEXT_X, lyricY + 132, _subTextPaint);
            float kY = lyricY + 117;
            var kRect = new SKRect(tX, kY, tX + tW, kY + tH);
            if (MediaController.IsLyricScanEnabled)
            {
                _dynamicFillPaint.Color = _scanToggleHovered ? new SKColor(0, 140, 240) : new SKColor(0, 120, 212);
                canvas.DrawRoundRect(kRect, tH / 2, tH / 2, _dynamicFillPaint);
                canvas.DrawCircle(tX + tW - tH / 2, kY + tH / 2, tH / 2 - 4, _toggleCirclePaint);
            }
            else
            {
                _dynamicStrokePaint.Color = _scanToggleHovered ? Neutral(150) : Neutral(100);
                canvas.DrawRoundRect(kRect, tH / 2, tH / 2, _dynamicStrokePaint);
                _toggleCirclePaint.Color = _scanToggleHovered ? Neutral(200) : Neutral(150);
                canvas.DrawCircle(tX + tH / 2, kY + tH / 2, tH / 2 - 4, _toggleCirclePaint);
                _toggleCirclePaint.Color = SKColors.White;
            }

            // 延迟调整
            canvas.DrawText("歌词延迟补偿", CONTENT_TEXT_X, lyricY + 164, _subTextPaint);
            float cardRightX = WIDTH - CONTENT_TEXT_RM;
            float btnY = lyricY + 147;

            _dynamicFillPaint.Color = _lyricMinusHovered ? Overlay(30) : Overlay(15);
            canvas.DrawRoundRect(new SKRect(cardRightX - 175, btnY, cardRightX - 145, btnY + 24), 4, 4, _dynamicFillPaint);
            canvas.DrawText("-", cardRightX - 164, btnY + 17, _uiTextPaint);

            string valStr = $"{MediaController.LyricDelayOffset:F1} s";
            if (MediaController.LyricDelayOffset > 0) valStr = "+" + valStr;
            float textW = _uiTextPaint.MeasureText(valStr);
            canvas.DrawText(valStr, cardRightX - 90 - textW, btnY + 17, _uiTextPaint);

            _dynamicFillPaint.Color = _lyricPlusHovered ? Overlay(30) : Overlay(15);
            canvas.DrawRoundRect(new SKRect(cardRightX - 80, btnY, cardRightX - 50, btnY + 24), 4, 4, _dynamicFillPaint);
            canvas.DrawText("+", cardRightX - 69, btnY + 17, _uiTextPaint);

            _dynamicFillPaint.Color = _lyricResetHovered ? Overlay(30) : Overlay(15);
            canvas.DrawRoundRect(new SKRect(cardRightX - 40, btnY, cardRightX, btnY + 24), 4, 4, _dynamicFillPaint);
            canvas.DrawText("重置", cardRightX - 33, btnY + 17, _subTextPaint);

            DrawHotkeyCard(canvas);
        }

        private void DrawHotkeyCard(SKCanvas canvas)
        {
            float cardY = HOTKEY_CARD_Y;
            var cardRect = new SKRect(CONTENT_L, cardY, WIDTH - CONTENT_RM, cardY + HOTKEY_CARD_H);
            canvas.DrawRoundRect(cardRect, 6, 6, _cardBg);
            canvas.DrawRoundRect(cardRect, 6, 6, _cardBorder);

            canvas.DrawText("全局快捷键", CONTENT_TEXT_X, cardY + 19, _uiTextPaint);

            bool hasHint = _hotkeyHint.Length > 0;
            bool recording = _hotkeyRecordingIndex >= 0;
            // 优先级：错误提示 > 录制操作说明 > 平时说明。
            //    （框里只能放得下「按下按键…」四个字）。
            string sub = hasHint ? _hotkeyHint
                : (recording ? "按下按键录制 · Esc 取消 · Backspace 清空"
                             : "在其他窗口也能控制播放 · 点按键框可重录");
            _subTextPaint.Color = hasHint ? new SKColor(230, 122, 92) : Neutral(170);
            float subMax = (WIDTH - CONTENT_RM - 16f - 42f) - 12f - CONTENT_TEXT_X;
            DrawTextWithEmoji(canvas, sub, _subTextPaint, subMax, CONTENT_TEXT_X, cardY + 36);
            _subTextPaint.Color = Neutral(170);

            float tW = 42f, tH = 20f;
            float tX = WIDTH - CONTENT_RM - 16f - tW;
            float tY = cardY + 12f;
            var tRect = new SKRect(tX, tY, tX + tW, tY + tH);
            if (MediaHotkeys.IsEnabled)
            {
                _dynamicFillPaint.Color = _hotkeyToggleHovered ? new SKColor(0, 140, 240) : new SKColor(0, 120, 212);
                canvas.DrawRoundRect(tRect, tH / 2, tH / 2, _dynamicFillPaint);
                canvas.DrawCircle(tX + tW - tH / 2, tY + tH / 2, tH / 2 - 4, _toggleCirclePaint);
            }
            else
            {
                _dynamicStrokePaint.Color = _hotkeyToggleHovered ? Neutral(150) : Neutral(100);
                canvas.DrawRoundRect(tRect, tH / 2, tH / 2, _dynamicStrokePaint);
                _toggleCirclePaint.Color = _hotkeyToggleHovered ? Neutral(200) : Neutral(150);
                canvas.DrawCircle(tX + tH / 2, tY + tH / 2, tH / 2 - 4, _toggleCirclePaint);
                _toggleCirclePaint.Color = SKColors.White;
            }

            // ── 五行键位：左 = 动作名，右 = 键位框 ──
            for (int i = 0; i < MediaHotkeys.Count; i++)
            {
                float rowY = cardY + HOTKEY_HEAD_H + i * HOTKEY_ROW_H;
                canvas.DrawText(MediaHotkeys.Label(i), CONTENT_TEXT_X, rowY + 20, _subTextPaint);

                bool rowRecording = _hotkeyRecordingIndex == i;
                var boxRect = new SKRect(HOTKEY_BOX_X, rowY + 4, HOTKEY_BOX_RIGHT, rowY + 4 + HOTKEY_BOX_H);

                _dynamicFillPaint.Color = rowRecording ? new SKColor(0, 120, 212, 70)
                    : (_hoveredHotkeyRow == i ? Overlay(30) : Overlay(15));
                canvas.DrawRoundRect(boxRect, 4, 4, _dynamicFillPaint);
                if (rowRecording)
                {
                    float oldStroke = _dynamicStrokePaint.StrokeWidth;
                    _dynamicStrokePaint.Color = new SKColor(0, 140, 240);
                    _dynamicStrokePaint.StrokeWidth = 1f;
                    canvas.DrawRoundRect(boxRect, 4, 4, _dynamicStrokePaint);
                    _dynamicStrokePaint.StrokeWidth = oldStroke;
                }

                string text = rowRecording ? "按下按键…" : MediaHotkeys.FormatKey(i);
                _dynamicTextPaint.Color = rowRecording ? new SKColor(0, 150, 255)
                    : (MediaHotkeys.IsBound(i) ? _fgColor : Neutral(120));
                DrawTextWithEmoji(canvas, text, _dynamicTextPaint,
                    HOTKEY_BOX_W - 24f, HOTKEY_BOX_RIGHT - 12f, rowY + 20.5f, rightAlign: true);
                _dynamicTextPaint.Color = _fgColor;
            }
        }

        // 页签：交互设置
        private void RenderTabInteraction(SKCanvas canvas)
        {
            bool isAutoHideDisabled = false;
            bool isModeDisabled = !NotchWindow.IsAutoHideEnabled;
            var autoHideCardRect = new SKRect(CONTENT_L, TITLE_BAR_HEIGHT + 12, WIDTH - CONTENT_RM, TITLE_BAR_HEIGHT + 260);
            canvas.DrawRoundRect(autoHideCardRect, 6, 6, _cardBg);
            canvas.DrawRoundRect(autoHideCardRect, 6, 6, _cardBorder);

            // 行 1：总开关，关掉时下面三行一起置灰、不可点。
            DrawToggleRow(canvas, 12, "自动隐藏", "允许灵动岛自动隐藏",
                NotchWindow.IsAutoHideEnabled, _autoHideToggleHovered, isAutoHideDisabled);

            canvas.DrawLine(CONTENT_TEXT_X, TITLE_BAR_HEIGHT + 70, WIDTH - CONTENT_TEXT_RM, TITLE_BAR_HEIGHT + 70, _separatorPaint);

            // 三个模式行：总开关未开启时置灰、不可点，并显示为关闭。
            // 行 2：「焦点离开时自动隐藏岛」。
            DrawToggleRow(canvas, 74, "当焦点离开时自动隐藏岛",
                !NotchWindow.IsAutoHideEnabled ? "需先开启上方总开关" : "没有媒体会话时，焦点离开就收起",
                NotchWindow.IsFocusAutoHideEnabled,
                !isModeDisabled && _focusHideToggleHovered,
                isModeDisabled);

            canvas.DrawLine(CONTENT_TEXT_X, TITLE_BAR_HEIGHT + 132, WIDTH - CONTENT_TEXT_RM, TITLE_BAR_HEIGHT + 132, _separatorPaint);

            DrawToggleRow(canvas, 136, "暂停播放后自动隐藏",
                !NotchWindow.IsAutoHideEnabled ? "需先开启上方总开关" : "媒体暂停播放时，也把刘海藏起来",
                NotchWindow.IsPauseAutoHideEnabled,
                !isModeDisabled && _pauseHideToggleHovered,
                isModeDisabled);

            canvas.DrawLine(CONTENT_TEXT_X, TITLE_BAR_HEIGHT + 194, WIDTH - CONTENT_TEXT_RM, TITLE_BAR_HEIGHT + 194, _separatorPaint);

            DrawToggleRow(canvas, 198, "全屏自动隐藏",
                !NotchWindow.IsAutoHideEnabled ? "需先开启上方总开关" : "检测到全屏视频 / 游戏时隐藏",
                NotchWindow.IsFullscreenAutoHideEnabled,
                !isModeDisabled && _fsHideToggleHovered,
                isModeDisabled);

            DrawToggleCard(canvas, 270, "媒体交互方式",
                Renderer.MediaInteractionMode == 1
                    ? "展开功能已开启：入口见下方「双击封面跳转应用」"
                    : "展开功能已关闭：折叠态右键直达媒体设置",
                Renderer.MediaInteractionMode == 1, _mediaExpToggleHovered);

            // 双击封面跳转应用（「双击哪里」已统一到封面）：
            //    关掉后左键空闲，恢复左键单击展开。
            string appLaunchSub = Renderer.MediaInteractionMode == 0
                ? (MediaController.IsAppLaunchEnabled
                    ? "折叠态双击左半边，展开态双击封面"
                    : "双击跳转已关闭（展开功能已关闭）")
                : (MediaController.IsAppLaunchEnabled
                    ? "开启后展开走右键；双击左半边 / 封面跳转"
                    : "关闭时左键单击展开；开启后展开走右键");
            DrawToggleCard(canvas, 342, "双击封面跳转应用", appLaunchSub,
                MediaController.IsAppLaunchEnabled, _appLaunchToggleHovered);

            DrawToggleCard(canvas, 414, "穿透模式", "悬停时透明并允许鼠标穿透本体与底层窗口交互", Renderer.PassthroughModeEnabled, _passToggleHovered);
        }

        // 页签：关于软件
        private void RenderTabAbout(SKCanvas canvas)
        {
            const float centerX = 400f;
            float startY = TITLE_BAR_HEIGHT + 30f;

            if (_appIconBitmap != null)
            {
                var iconRect = new SKRect(centerX - 32, startY, centerX + 32, startY + 64);
                canvas.DrawBitmap(_appIconBitmap, iconRect, _hqSamplingOpts);
                startY += 90f;
            }

            _dynamicTextPaint.Color = _fgColor;
            _dynamicTextPaint.TextSize = 20f;
            _dynamicTextPaint.TextAlign = SKTextAlign.Center;
            canvas.DrawText("NotchPeninsula", centerX, startY, _dynamicTextPaint);
            startY += 22f;

            _dynamicTextPaint.Color = Neutral(170);
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
            // 这三项都是自动调整的，留着只会让人以为要手动设。
            void DrawMultiCard(float yOffset, string[] subLabels, int[] indices)
            {
                float cardHeight = 12 + subLabels.Length * 34;
                var cardRect = new SKRect(CONTENT_L, TITLE_BAR_HEIGHT + yOffset, WIDTH - CONTENT_RM, TITLE_BAR_HEIGHT + yOffset + cardHeight);
                canvas.DrawRoundRect(cardRect, 6, 6, _cardBg);
                canvas.DrawRoundRect(cardRect, 6, 6, _cardBorder);

                for (int i = 0; i < subLabels.Length; i++)
                {
                    int index = indices[i];
                    float cardBtnY = GetBtnY(index);

                    canvas.DrawText(subLabels[i], CONTENT_TEXT_X, cardBtnY + 17, _subTextPaint);

                    float afterLabelX = CONTENT_TEXT_X + _subTextPaint.MeasureText(subLabels[i]) + 8;
                    if (Math.Abs(_customValues[index] - _defaultCustomValues[index]) > 0.001f)
                    {
                        var tagRect = new SKRect(afterLabelX, cardBtnY + 3, afterLabelX + 38, cardBtnY + 21);
                        _dynamicFillPaint.Color = new SKColor(0, 120, 212, 35);
                        canvas.DrawRoundRect(tagRect, 3f, 3f, _dynamicFillPaint);
                        _dynamicTextPaint.TextSize = 10f; // 小文本样式
                        _dynamicTextPaint.Color = new SKColor(0, 140, 240);
                        canvas.DrawText("已生效", afterLabelX + 4, cardBtnY + 16, _dynamicTextPaint);
                        _dynamicTextPaint.TextSize = 13f; // 还原字号，防止污染后续文字渲染
                        afterLabelX += 46;
                    }

                    if (index == 7)
                    {
                        float hintA = GetHintAlpha(index);
                        if (hintA > 0.01f)
                        {
                            _subTextPaint.Color = new SKColor(0, 140, 240, (byte)(255 * hintA));
                            canvas.DrawText("刘海模式下生效", afterLabelX, cardBtnY + 17, _subTextPaint);
                            _subTextPaint.Color = Neutral(170);
                        }
                    }

                    float cardRightX = WIDTH - CONTENT_TEXT_RM;

                    _dynamicFillPaint.Color = _hoveredMinusIndex == index ? Overlay(30) : Overlay(15);
                    canvas.DrawRoundRect(new SKRect(cardRightX - 175, cardBtnY, cardRightX - 145, cardBtnY + 24), 4, 4, _dynamicFillPaint);
                    canvas.DrawText("-", cardRightX - 164, cardBtnY + 17, _uiTextPaint);

                    // 使用静态缓存字符串，零 GC 开销
                    string valStr = _valStrCache[index];
                    float textW = _uiTextPaint.MeasureText(valStr);
                    canvas.DrawText(valStr, cardRightX - 90 - textW, cardBtnY + 17, _uiTextPaint);

                    _dynamicFillPaint.Color = _hoveredPlusIndex == index ? Overlay(30) : Overlay(15);
                    canvas.DrawRoundRect(new SKRect(cardRightX - 80, cardBtnY, cardRightX - 50, cardBtnY + 24), 4, 4, _dynamicFillPaint);
                    canvas.DrawText("+", cardRightX - 69, cardBtnY + 17, _uiTextPaint);

                    _dynamicFillPaint.Color = _hoveredResetIndex == index ? Overlay(30) : Overlay(15);
                    canvas.DrawRoundRect(new SKRect(cardRightX - 40, cardBtnY, cardRightX, cardBtnY + 24), 4, 4, _dynamicFillPaint);
                    canvas.DrawText("重置", cardRightX - 33, cardBtnY + 17, _subTextPaint);
                }
            }

            // 绘制新增的主题卡片
            float themeY = TITLE_BAR_HEIGHT + THEME_CARD_Y;
            canvas.DrawRoundRect(new SKRect(CONTENT_L, themeY, WIDTH - CONTENT_RM, themeY + THEME_CARD_H), 6, 6, _cardBg);
            canvas.DrawRoundRect(new SKRect(CONTENT_L, themeY, WIDTH - CONTENT_RM, themeY + THEME_CARD_H), 6, 6, _cardBorder);

            canvas.DrawText("刘海 / 灵动岛主题", CONTENT_TEXT_X, themeY + 26, _uiTextPaint);
            canvas.DrawText("背景与文本颜色自适应反转", CONTENT_TEXT_X, themeY + 46, _subTextPaint);

            DrawSegmented(canvas, THEME_SEG_X, TITLE_BAR_HEIGHT + THEME_SEG_Y, THEME_SEG_W, SEG_H,
                ["黑", "白", "系统"], Renderer.ThemeMode, _hoveredThemeIndex);

            float opaY = TITLE_BAR_HEIGHT + OPACITY_CARD_Y;
            canvas.DrawRoundRect(new SKRect(CONTENT_L, opaY, WIDTH - CONTENT_RM, opaY + OPACITY_CARD_H), 6, 6, _cardBg);
            canvas.DrawRoundRect(new SKRect(CONTENT_L, opaY, WIDTH - CONTENT_RM, opaY + OPACITY_CARD_H), 6, 6, _cardBorder);

            canvas.DrawText("背景透明度", CONTENT_TEXT_X, opaY + 26, _subTextPaint);
            float sliderY = opaY + OPACITY_SLIDER_DY;
            float sliderX = CONTENT_TEXT_X;
            float sliderW = (WIDTH - CONTENT_TEXT_RM) - CONTENT_TEXT_X;
            // 背景透明度滑轨
            canvas.DrawLine(sliderX, sliderY, sliderX + sliderW, sliderY, _separatorPaint);
            float activePx = sliderX + (sliderW / 4) * Renderer.BgOpacityLevel;
            _dynamicStrokePaint.Color = new SKColor(0, 120, 212);
            _dynamicStrokePaint.StrokeWidth = 2f;
            canvas.DrawLine(sliderX, sliderY, activePx, sliderY, _dynamicStrokePaint);
            _dynamicStrokePaint.StrokeWidth = 1.5f;
            bool isOpacityDisabled = Renderer.PassthroughModeEnabled;
            _dynamicStrokePaint.Color = isOpacityDisabled ? Neutral(80) : new SKColor(0, 120, 212);
            for (int i = 0; i < 5; i++)
            {
                float px = sliderX + (sliderW / 4) * i;
                bool isSelected = Renderer.BgOpacityLevel == i;
                bool isHovered = _hoveredOpacityIndex == i;
                if (isSelected || isHovered)
                {
                    _dynamicFillPaint.Color = isOpacityDisabled ? Neutral(100) : (isSelected ? new SKColor(0, 120, 212) : Overlay(80));
                    canvas.DrawCircle(px, sliderY, isSelected ? 6 : 4, _dynamicFillPaint);
                }
                _dynamicTextPaint.Color = isOpacityDisabled ? Neutral(100) : (isSelected ? _fgColor : Neutral(150));
                _dynamicTextPaint.TextSize = 11f;
                string pct = OpacityStopLabels[i];   // 复用静态刻度文案，避免每帧插值
                float tw = _dynamicTextPaint.MeasureText(pct);
                canvas.DrawText(pct, px - tw / 2, sliderY + 18, _dynamicTextPaint);
                _dynamicTextPaint.TextSize = 13f;
            }
            DrawMultiCard(SIZE_CARD_Y, ["全局折叠态高度", "底部圆角", "消息通知弹出高度", "视觉比例"], [3, 7, 5, 6]);
        }

        private void RenderTabPlugins(SKCanvas canvas)
        {
            RefreshPluginView();

            // ── 顶部操作卡片 ──
            float topY = TITLE_BAR_HEIGHT + 12;
            var topRect = new SKRect(CONTENT_L, topY, WIDTH - CONTENT_RM, topY + 96);
            canvas.DrawRoundRect(topRect, 6, 6, _cardBg);
            canvas.DrawRoundRect(topRect, 6, 6, _cardBorder);
            canvas.DrawText("我的插件", CONTENT_TEXT_X, topY + 26, _uiTextPaint);
            canvas.DrawText("管理已安装的插件，或从下方插件市场获取新插件", CONTENT_TEXT_X, topY + 46, _subTextPaint);

            void DrawPluginButton(int index, string label, float bx, float by, float bw)
            {
                bool hovered = _hoveredPluginAction == index;
                var btn = new SKRect(bx, by, bx + bw, by + 24);
                _dynamicFillPaint.Color = hovered ? new SKColor(0, 140, 240) : new SKColor(0, 120, 212);
                canvas.DrawRoundRect(btn, 4, 4, _dynamicFillPaint);
                float tw = _uiTextPaint.MeasureText(label);
                _uiTextPaint.Color = SKColors.White;   // 蓝底白字（浅色外观下 _fgColor 是黑的，压蓝底看不清）
                canvas.DrawText(label, bx + (bw - tw) / 2f, by + 17, _uiTextPaint);
                _uiTextPaint.Color = _fgColor;
            }

            DrawPluginButton(0, "导入 DLL", CONTENT_TEXT_X, topY + 60, 96);
            DrawPluginButton(1, "打开目录", CONTENT_TEXT_X + 104, topY + 60, 96);
            DrawPluginButton(2, "插件市场", CONTENT_TEXT_X + 208, topY + 60, 96);

            // ── 已安装插件列表卡片（可滚动，整卡高度）──
            GetPluginListCardTop(out float listY);
            var listRect = new SKRect(CONTENT_L, listY, WIDTH - CONTENT_RM, HEIGHT - 20);
            canvas.DrawRoundRect(listRect, 6, 6, _cardBg);
            canvas.DrawRoundRect(listRect, 6, 6, _cardBorder);
            canvas.DrawText($"已安装插件 ({_pluginView.Count})", CONTENT_TEXT_X, listY + 26, _uiTextPaint);

            if (!string.IsNullOrEmpty(_pluginHint))
            {
                string header = $"已安装插件 ({_pluginView.Count})";
                float hintMax = (WIDTH - CONTENT_TEXT_RM) - CONTENT_TEXT_X - _uiTextPaint.MeasureText(header) - 16;
                _subTextPaint.Color = _pluginHintIsError ? new SKColor(232, 100, 100) : new SKColor(120, 200, 140);
                DrawTextWithEmoji(canvas, _pluginHint, _subTextPaint, hintMax, WIDTH - CONTENT_TEXT_RM, listY + 26, rightAlign: true);
                _subTextPaint.Color = Neutral(170);
            }

            // 条目变少时把滚动位置钳回可滚范围，避免停在一片空白上
            GetPluginListLayout(out int visibleRows, out int maxFirstRow);
            _pluginScroll = Math.Clamp(_pluginScroll, 0, maxFirstRow);

            // 上行：名称独占整行，可延展至卡片右边界外侧
            float nameTextMax = (WIDTH - CONTENT_TEXT_RM) - CONTENT_TEXT_X;                // 名称几乎全宽
            float infoTextMax = PLUGIN_BTN_RELOAD_X - CONTENT_TEXT_X - 8;     // 信息止于按钮区之前

            if (_pluginView.Count == 0)
                canvas.DrawText("暂无插件，点击「导入 DLL」或到「插件市场」下载", CONTENT_TEXT_X, listY + 66, _subTextPaint);

            for (int slot = 0; slot < visibleRows; slot++)
            {
                int i = _pluginScroll + slot;
                var entry = _pluginView[i];
                float rowY = listY + 44 + slot * PluginListRowH;
                if (slot > 0) canvas.DrawLine(CONTENT_TEXT_X, rowY - 5, WIDTH - CONTENT_TEXT_RM, rowY - 5, _separatorPaint);

                // ═══ 上行：插件名称（独占整行，无按钮遮挡） ═══
                canvas.DrawText(TruncateText(entry.FriendlyName, _uiTextPaint, nameTextMax), CONTENT_TEXT_X, rowY + 17, _uiTextPaint);

                string sub = i < _pluginSubTexts.Count ? _pluginSubTexts[i] : "";
                bool subDisabled = i < _pluginSubDisabled.Count && _pluginSubDisabled[i];
                float infoBaseline = rowY + 37;
                string shownSub = TruncateText(sub, _subTextPaint, infoTextMax);
                if (subDisabled && shownSub.StartsWith("已禁用", StringComparison.Ordinal))
                {
                    const string tag = "已禁用";
                    _subTextPaint.Color = new SKColor(0, 140, 240);
                    canvas.DrawText(tag, CONTENT_TEXT_X, infoBaseline, _subTextPaint);
                    _subTextPaint.Color = Neutral(170);
                    canvas.DrawText(shownSub[tag.Length..], CONTENT_TEXT_X + _subTextPaint.MeasureText(tag), infoBaseline, _subTextPaint);
                }
                else
                {
                    _subTextPaint.Color = Neutral(170);
                    canvas.DrawText(shownSub, CONTENT_TEXT_X, infoBaseline, _subTextPaint);
                }

                const float btnTop = 21f, btnH = 20f;       // 操作按钮矩形

                // 操作按钮（重载 / 卸载）+ 开关
                void DrawRowButton(float bx, bool hovered, string label, bool danger)
                {
                    var r = new SKRect(bx, rowY + btnTop, bx + 50, rowY + btnTop + btnH);
                    _dynamicFillPaint.Color = hovered
                        ? (danger ? new SKColor(180, 50, 50) : Overlay(30))
                        : Overlay(15);
                    canvas.DrawRoundRect(r, 4, 4, _dynamicFillPaint);
                    _uiTextPaint.Color = _fgColor;
                    DrawCenteredButtonLabel(canvas, label, r, _uiTextPaint);
                }
                DrawRowButton(PLUGIN_BTN_RELOAD_X, _hoveredPluginReload == i, "重载", false);
                DrawRowButton(PLUGIN_BTN_REMOVE_X, _hoveredPluginRemove == i, "卸载", true);

                float tW = 42, tH = 20;
                float tX = PLUGIN_BTN_TOGGLE_X, tY = rowY + 22;
                var tRect = new SKRect(tX, tY, tX + tW, tY + tH);
                if (entry.IsEnabled)
                {
                    _dynamicFillPaint.Color = _hoveredPluginToggle == i ? new SKColor(0, 140, 240) : new SKColor(0, 120, 212);
                    canvas.DrawRoundRect(tRect, tH / 2, tH / 2, _dynamicFillPaint);
                    canvas.DrawCircle(tX + tW - tH / 2, tY + tH / 2, tH / 2 - 4, _toggleCirclePaint);
                }
                else
                {
                    _dynamicStrokePaint.Color = _hoveredPluginToggle == i ? Neutral(150) : Neutral(100);
                    canvas.DrawRoundRect(tRect, tH / 2, tH / 2, _dynamicStrokePaint);
                    _toggleCirclePaint.Color = _hoveredPluginToggle == i ? Neutral(200) : Neutral(150);
                    canvas.DrawCircle(tX + tH / 2, tY + tH / 2, tH / 2 - 4, _toggleCirclePaint);
                    _toggleCirclePaint.Color = SKColors.White;
                }
            }

            if (maxFirstRow > 0 && visibleRows > 0)
            {
                float trackTop = listY + 44 - 2f;
                float trackH = visibleRows * PluginListRowH - 8f;
                DrawScrollBar(canvas, WIDTH - 26, trackTop, trackH,
                    visibleRows / (float)_pluginView.Count, _pluginScroll / (float)maxFirstRow);
            }

            // 拖走之后不存在任何后台重绘，不会留下空转的定时器。
            if (_pluginDropHovering)
            {
                var zone = GetPluginDropZone();

                _dynamicFillPaint.Color = new SKColor(0, 120, 212, 42);
                _dynamicStrokePaint.Color = new SKColor(0, 140, 240, 215);
                _dynamicStrokePaint.StrokeWidth = 2f;
                canvas.DrawRoundRect(zone, 8, 8, _dynamicFillPaint);
                canvas.DrawRoundRect(zone, 8, 8, _dynamicStrokePaint);
                _dynamicStrokePaint.StrokeWidth = 1.5f;   // 复位：该画笔被多处共用

                const string dropHint = "松开鼠标以导入插件";
                float hintW = _uiTextPaint.MeasureText(dropHint);
                float hintX = zone.MidX - hintW / 2f;
                float hintY = zone.MidY + 5f;
                _dynamicFillPaint.Color = new SKColor(0, 90, 170, 235);
                canvas.DrawRoundRect(new SKRect(hintX - 14, hintY - 22, hintX + hintW + 14, hintY + 10), 6, 6, _dynamicFillPaint);
                _uiTextPaint.Color = SKColors.White;
                canvas.DrawText(dropHint, hintX, hintY, _uiTextPaint);
                _uiTextPaint.Color = _fgColor;
            }

            if (_marketDialog == MarketDialog.LoadFailed)
            {
                DrawLoadFailedDialog(canvas);
            }
        }

        private void RenderTabMarket(SKCanvas canvas)
        {
            EnsureMarketData();
            ApplyPendingMarketResult();   // 消费后台安装结果：刷新列表 + 提示 + 评分弹窗
            if (_marketDataDirty) { _marketDataDirty = false; RefreshMarketFilter(); }   // UI 线程重建过滤视图

            float cardTop = TITLE_BAR_HEIGHT + 12;
            var cardRect = new SKRect(CONTENT_L, cardTop, WIDTH - CONTENT_RM, HEIGHT - 20);
            canvas.DrawRoundRect(cardRect, 6, 6, _cardBg);
            canvas.DrawRoundRect(cardRect, 6, 6, _cardBorder);

            var catRect = new SKRect(CONTENT_TEXT_X, MarketControlsY, CONTENT_TEXT_X + MarketCatBtnW, MarketControlsY + MarketControlH);
            _dynamicFillPaint.Color = _marketCategoryOpen || _hoveredMarketCategoryIndex != -1 ? Overlay(30) : Overlay(15);
            canvas.DrawRoundRect(catRect, 5, 5, _dynamicFillPaint);
            _dynamicStrokePaint.Color = Neutral(_marketCategoryOpen ? (byte)130 : (byte)90);
            canvas.DrawRoundRect(catRect, 5, 5, _dynamicStrokePaint);
            _marketTextPaint.Color = _fgColor;
            canvas.DrawText(MarketCategoryName(_marketCategoryKey), CONTENT_TEXT_X + 12, MarketControlsY + 17, _marketTextPaint);
            // 下拉箭头
            float ax = catRect.Right - 16, ay = MarketControlsY + 11;
            _dynamicStrokePaint.Color = Neutral(170);
            _dynamicStrokePaint.StrokeWidth = 1.6f;
            canvas.DrawLine(ax - 4, ay, ax, ay + 4, _dynamicStrokePaint);
            canvas.DrawLine(ax, ay + 4, ax + 4, ay, _dynamicStrokePaint);
            _dynamicStrokePaint.StrokeWidth = 1.5f;   // 复位：共用画笔

            var searchRect = new SKRect(MarketSearchX, MarketControlsY, MarketSearchX + MarketSearchW, MarketControlsY + MarketControlH);
            _dynamicFillPaint.Color = Overlay(15);
            canvas.DrawRoundRect(searchRect, 5, 5, _dynamicFillPaint);
            _dynamicStrokePaint.Color = _marketSearchFocused ? new SKColor(0, 140, 240) : Neutral(_marketSearchHovered ? (byte)130 : (byte)90);
            canvas.DrawRoundRect(searchRect, 5, 5, _dynamicStrokePaint);
            float mgx = MarketSearchX + 14, mgy = MarketControlsY + 12;
            _dynamicStrokePaint.Color = Neutral(150);
            _dynamicStrokePaint.StrokeWidth = 1.5f;
            canvas.DrawCircle(mgx, mgy, 5f, _dynamicStrokePaint);
            canvas.DrawLine(mgx + 3.6f, mgy + 3.6f, mgx + 7f, mgy + 7f, _dynamicStrokePaint);
            _marketTextPaint.Color = _marketSearch.Length == 0 && _marketImeComposing.Length == 0 ? Neutral(120) : _fgColor;
            float caretX = MarketSearchTextX;
            if (_marketSearch.Length > 0)
            {
                int viewStart = MarketSearchViewStart();
                if (_marketSearchFocused && MarketHasSelection)
                {
                    float hx1 = MarketSearchXAtIndex(MarketSelStart);
                    float hx2 = MarketSearchXAtIndex(MarketSelEnd);
                    if (hx2 - hx1 > 0.5f)
                    {
                        _dynamicFillPaint.Color = new SKColor(0, 120, 212, 96);
                        canvas.DrawRect(new SKRect(hx1, MarketControlsY + 5f, hx2, MarketControlsY + 21f), _dynamicFillPaint);
                    }
                }
                string shownSearch = TruncateText(_marketSearch[viewStart..], _marketTextPaint, MarketSearchTextMax);
                canvas.DrawText(shownSearch, MarketSearchTextX, MarketControlsY + 17, _marketTextPaint);
                caretX = MarketSearchXAtIndex(_marketSearchCaret);
            }
            else if (_marketImeComposing.Length == 0)
            {
                canvas.DrawText("搜索插件", MarketSearchTextX, MarketControlsY + 17, _marketTextPaint);
            }
            if (_marketImeComposing.Length > 0)
            {
                _marketTextPaint.Color = Neutral(150);
                string comp = TruncateText(_marketImeComposing, _marketTextPaint, MarketSearchX + MarketSearchW - 8 - caretX);
                canvas.DrawText(comp, caretX, MarketControlsY + 17, _marketTextPaint);
                float cw = _marketTextPaint.MeasureText(comp);
                canvas.DrawLine(caretX, MarketControlsY + 21, caretX + cw, MarketControlsY + 21, _separatorPaint);
                caretX += cw;
                if (_marketSearchCaret < _marketSearch.Length)
                {
                    _marketTextPaint.Color = _fgColor;
                    string tail = TruncateText(_marketSearch[_marketSearchCaret..], _marketTextPaint,
                        MarketSearchX + MarketSearchW - 8 - caretX);
                    canvas.DrawText(tail, caretX, MarketControlsY + 17, _marketTextPaint);
                }
            }
            if (_marketSearchFocused)
            {
                _dynamicFillPaint.Color = new SKColor(0, 140, 240);
                canvas.DrawRect(new SKRect(caretX + 1, MarketControlsY + 6, caretX + 2.4f, MarketControlsY + 20), _dynamicFillPaint);
            }

            var chkBox = new SKRect(MarketChkX, MarketChkY, MarketChkX + MarketChkBoxSize, MarketChkY + MarketChkBoxSize);
            _dynamicStrokePaint.Color = _marketOnlyInstalled
                ? new SKColor(0, 120, 212)
                : Neutral(_hoveredMarketChk ? (byte)150 : (byte)100);
            _dynamicFillPaint.Color = _marketOnlyInstalled ? new SKColor(0, 120, 212) : SKColors.Transparent;
            canvas.DrawRoundRect(chkBox, 3, 3, _dynamicFillPaint);
            canvas.DrawRoundRect(chkBox, 3, 3, _dynamicStrokePaint);
            if (_marketOnlyInstalled)
            {
                // 白色对勾（与「显示内容」列表的勾选框同一套画法）
                _iconPaint.Color = SKColors.White;
                canvas.DrawLine(MarketChkX + 3, MarketChkY + 8, MarketChkX + 6, MarketChkY + 11, _iconPaint);
                canvas.DrawLine(MarketChkX + 6, MarketChkY + 11, MarketChkX + 13, MarketChkY + 4, _iconPaint);
                _iconPaint.Color = _fgColor;
            }
            _marketTextPaint.Color = _hoveredMarketChk ? _fgColor : Neutral(190);
            canvas.DrawText(MarketChkLabel, MarketChkLabelX, MarketStatusBaseline, _marketTextPaint);

            string status = _marketFetching ? "正在加载插件市场…"
                : _marketError.Length > 0 ? _marketError
                : $"共 {_marketView.Count} 个插件" + (string.Equals(_marketCategoryKey, "all", StringComparison.OrdinalIgnoreCase) && _marketSearch.Length == 0 && !_marketOnlyInstalled ? "" : "（已筛选）");
            float statusX = MarketChkLabelX + _marketTextPaint.MeasureText(MarketChkLabel) + 18f;
            float statusMax = MarketRefreshX - 10f - statusX;
            _marketTextPaint.Color = _marketError.Length > 0 && !_marketFetching ? new SKColor(232, 100, 100) : Neutral(140);
            canvas.DrawText(TruncateText(status, _marketTextPaint, statusMax), statusX, MarketStatusBaseline, _marketTextPaint);
            _marketTextPaint.Color = Neutral(140);
            if (!string.IsNullOrEmpty(_marketHint) && !_marketFetching)
            {
                float hintRight = MarketRefreshX - 10f;   // 提示右对齐到刷新按钮左侧，别钻到按钮底下
                float hintMax = hintRight - statusX - _marketTextPaint.MeasureText(status) - 16;
                _marketTextPaint.Color = _marketHintIsError ? new SKColor(232, 100, 100) : new SKColor(120, 200, 140);
                DrawTextWithEmoji(canvas, _marketHint, _marketTextPaint, hintMax, hintRight, MarketStatusBaseline, rightAlign: true);
                _marketTextPaint.Color = Neutral(140);
            }

            var refreshRect = new SKRect(MarketRefreshX, MarketStatusRowY, MarketRefreshX + MarketRefreshW, MarketStatusRowY + MarketControlH);
            bool refreshActive = !_marketFetching;
            _dynamicFillPaint.Color = refreshActive && _hoveredMarketRefresh ? Overlay(30) : Overlay(15);
            canvas.DrawRoundRect(refreshRect, 5, 5, _dynamicFillPaint);
            _dynamicStrokePaint.Color = Neutral(refreshActive && _hoveredMarketRefresh ? (byte)130 : (byte)90);
            canvas.DrawRoundRect(refreshRect, 5, 5, _dynamicStrokePaint);
            _marketTextPaint.Color = refreshActive ? _fgColor : Neutral(110);
            float refreshTw = _marketTextPaint.MeasureText("刷新");
            canvas.DrawText("刷新", MarketRefreshX + (MarketRefreshW - refreshTw) / 2f, MarketStatusBaseline, _marketTextPaint);
            _marketTextPaint.Color = Neutral(170);
            _subTextPaint.Color = Neutral(170);   // 下面列表区继续用这支，保持默认灰

            float rowsTop = MarketRowsTop;
            float infoTextMax = MarketBtn1X - CONTENT_TEXT_X - 8;          // 信息止于按钮区之前

            GetMarketListLayout(out int mVisibleRows, out int mMaxFirst);
            _marketScroll = Math.Clamp(_marketScroll, 0, mMaxFirst);

            if (!_marketFetching && _marketError.Length == 0 && _marketView.Count == 0)
            {
                string empty = _marketSearch.Length > 0 ? $"没有找到「{_marketSearch}」相关的插件"
                    : string.Equals(_marketCategoryKey, "all", StringComparison.OrdinalIgnoreCase) ? "市场暂时没有可安装的插件"
                    : "该分类下暂无插件";
                canvas.DrawText(TruncateText(empty, _subTextPaint, WIDTH - CONTENT_TEXT_RM - CONTENT_TEXT_X), CONTENT_TEXT_X, rowsTop + 18, _subTextPaint);
            }

            for (int slot = 0; slot < mVisibleRows; slot++)
            {
                int i = _marketScroll + slot;
                var mp = _marketView[i];
                float rowY = rowsTop + slot * PluginListRowH;
                if (slot > 0) canvas.DrawLine(CONTENT_TEXT_X, rowY - 5, WIDTH - CONTENT_TEXT_RM, rowY - 5, _separatorPaint);

                var local = MatchLocalPlugin(mp);
                bool busy = string.Equals(_marketBusyId, mp.Id, StringComparison.Ordinal);
                bool dim = PluginManager.Instance.IsDisabled(local);
                //    再多一行「已装 vX」纯属重复信息。
                //    也绝不显示（无从判断版本是否一致）。
                string localVer = local?.Version ?? "";
                bool hasLocalVer = localVer.Length > 0;
                bool versionDiff = local != null && hasLocalVer && CompareVersions(mp.Version, localVer) != 0;

                //    路径绘制恒定饱满（与评分弹窗那排星同一套路径）。
                // 分数只显示数字（如「4.5」），不带「分」字，简洁。
                const float scoreGap = 8f;     // 星星与分数之间
                const float dlGap = 14f;       // 评分簇与下载量之间
                const float starR = 5.5f;      // 星外径
                float rightEdge = WIDTH - MarketRightPad;

                string dlText = $"{mp.Downloads} 次下载";
                float dlw = _subTextPaint.MeasureText(dlText);
                float dlX = rightEdge - dlw;

                bool hasRating = mp.RatingCount > 0;
                string scoreText = hasRating ? FormatScore(mp.Rating) : "暂无评分";
                float scoreW = _subTextPaint.MeasureText(scoreText);
                float scoreX = dlX - dlGap - scoreW;

                _subTextPaint.Color = Neutral(140);
                canvas.DrawText(dlText, dlX, rowY + 17, _subTextPaint);

                float ratingLeft = scoreX;   // 名称可用宽度的右界（下方算 nameMaxNow 用）
                if (hasRating)
                {
                    float starCx = scoreX - scoreGap - starR;
                    using var star = BuildStarPath(starCx, rowY + 12f, starR, starR * 0.42f);
                    _dynamicFillPaint.Color = new SKColor(232, 168, 48);
                    canvas.DrawPath(star, _dynamicFillPaint);
                    _dynamicFillPaint.Color = Overlay(15);   // 复位：该画笔多处共用
                    ratingLeft = starCx - starR;
                }
                _subTextPaint.Color = hasRating ? Neutral(185) : Neutral(115);
                canvas.DrawText(scoreText, scoreX, rowY + 17, _subTextPaint);
                _subTextPaint.Color = Neutral(170);

                string tagOfficial = mp.Official ? "官方" : "";
                //    绝不拼成半截的「已装 v」。
                string tagInstalled = versionDiff ? $"已装 v{localVer}" : "";
                float tagW = (tagOfficial.Length > 0 ? 6f + _subTextPaint.MeasureText(tagOfficial) : 0f)
                           + (tagInstalled.Length > 0 ? 8f + _subTextPaint.MeasureText(tagInstalled) : 0f);
                float nameMaxNow = Math.Max(40f, ratingLeft - 12f - tagW - CONTENT_TEXT_X);

                string shownName = TruncateText(mp.Name, _uiTextPaint, nameMaxNow);
                _uiTextPaint.Color = dim ? Neutral(105) : _fgColor;
                canvas.DrawText(shownName, CONTENT_TEXT_X, rowY + 17, _uiTextPaint);
                float afterNameX = CONTENT_TEXT_X + _uiTextPaint.MeasureText(shownName);

                if (tagOfficial.Length > 0)
                {
                    _subTextPaint.Color = dim ? Neutral(95) : new SKColor(0, 140, 240);
                    canvas.DrawText(tagOfficial, afterNameX + 6, rowY + 17, _subTextPaint);
                    afterNameX += 6 + _subTextPaint.MeasureText(tagOfficial);
                }
                if (tagInstalled.Length > 0)
                {
                    _subTextPaint.Color = dim ? Neutral(95) : new SKColor(0, 140, 240);
                    canvas.DrawText(tagInstalled, afterNameX + 8, rowY + 17, _subTextPaint);
                    afterNameX += 8 + _subTextPaint.MeasureText(tagInstalled);
                }
                _subTextPaint.Color = Neutral(170);

                if (busy)
                {
                    _subTextPaint.Color = new SKColor(0, 140, 240);
                    canvas.DrawText("正在下载安装…", CONTENT_TEXT_X, rowY + 37, _subTextPaint);
                    _subTextPaint.Color = Neutral(170);
                }
                else if (dim)
                {
                    //    同时说明这一行为什么是灰的
                    _subTextPaint.Color = Neutral(95);
                    canvas.DrawText("已禁用", CONTENT_TEXT_X, rowY + 37, _subTextPaint);
                    _subTextPaint.Color = Neutral(170);
                }
                else
                {
                    _subTextPaint.Color = Neutral(170);
                    canvas.DrawText(TruncateText($"v{mp.Version} · {mp.Author}", _subTextPaint, infoTextMax),
                        CONTENT_TEXT_X, rowY + 37, _subTextPaint);
                }

                const float btnTop = 21f, btnH = 20f;

                // 主操作（下载 / 更新 / 重装）：三态三种颜色
                {
                    bool enabled = !busy;
                    bool hovered = _hoveredMarketInstall == i;
                    var r = new SKRect(MarketBtn1X, rowY + btnTop, MarketBtn1X + MarketBtnW, rowY + btnTop + btnH);
                    var (normal, hover) = InstallButtonColors(local, mp);
                    _dynamicFillPaint.Color = !enabled ? Overlay(20) : hovered ? hover : normal;
                    canvas.DrawRoundRect(r, 4, 4, _dynamicFillPaint);
                    _uiTextPaint.Color = enabled ? SKColors.White : Neutral(110);
                    DrawCenteredButtonLabel(canvas, MarketInstallLabel(mp, local), r, _uiTextPaint);
                }

                // 卸载：灰实底，悬停转红；本机没装则置灰
                {
                    bool enabled = local != null && !busy;
                    bool hovered = _hoveredMarketUninstall == i;
                    var r = new SKRect(MarketBtn2X, rowY + btnTop, MarketBtn2X + MarketBtnW, rowY + btnTop + btnH);
                    _dynamicFillPaint.Color = !enabled ? Overlay(8)
                        : hovered ? new SKColor(180, 50, 50) : Overlay(15);
                    canvas.DrawRoundRect(r, 4, 4, _dynamicFillPaint);
                    _uiTextPaint.Color = !enabled ? Neutral(110) : hovered ? SKColors.White : Neutral(200);
                    DrawCenteredButtonLabel(canvas, "卸载", r, _uiTextPaint);
                }

                // 详情：中性灰
                {
                    bool hovered = _hoveredMarketDetail == i;
                    var r = new SKRect(MarketBtn3X, rowY + btnTop, MarketBtn3X + MarketBtnW, rowY + btnTop + btnH);
                    _dynamicFillPaint.Color = hovered ? Overlay(40) : Overlay(20);
                    canvas.DrawRoundRect(r, 4, 4, _dynamicFillPaint);
                    _uiTextPaint.Color = hovered ? _fgColor : Neutral(200);
                    DrawCenteredButtonLabel(canvas, "详情", r, _uiTextPaint);
                }
            }

            if (mMaxFirst > 0 && mVisibleRows > 0)
            {
                float trackTop = rowsTop - 2f;
                float trackH = mVisibleRows * PluginListRowH - 8f;
                DrawScrollBar(canvas, WIDTH - 26, trackTop, trackH,
                    mVisibleRows / (float)_marketView.Count, _marketScroll / (float)mMaxFirst);
            }

            if (_marketCategoryOpen)
            {
                const float rowH = 26f;
                float mY = MarketControlsY + MarketControlH + 4;
                var menu = new SKRect(CONTENT_TEXT_X, mY, CONTENT_TEXT_X + MarketCatBtnW, mY + MarketCategories.Length * rowH);
                canvas.DrawRoundRect(menu, 6, 6, _menuBg);
                canvas.DrawRoundRect(menu, 6, 6, _menuBorder);
                for (int k = 0; k < MarketCategories.Length; k++)
                {
                    var item = new SKRect(menu.Left + 2, mY + k * rowH, menu.Right - 2, mY + (k + 1) * rowH);
                    bool selected = string.Equals(MarketCategories[k].Key, _marketCategoryKey, StringComparison.OrdinalIgnoreCase);
                    if (_hoveredMarketCategoryIndex == k) { _dynamicFillPaint.Color = Overlay(40); canvas.DrawRoundRect(item, 4, 4, _dynamicFillPaint); }
                    _subTextPaint.Color = selected ? new SKColor(0, 140, 240) : Neutral(200);
                    canvas.DrawText(MarketCategories[k].Name, menu.Left + 12, mY + k * rowH + 18, _subTextPaint);
                    if (selected)
                    {
                        DrawTextWithEmoji(canvas, "✓", _subTextPaint, 20, menu.Right - 12, mY + k * rowH + 18, rightAlign: true);
                    }
                }
                _subTextPaint.Color = Neutral(170);
            }

            if (_marketDialog == MarketDialog.LoadFailed)
            {
                DrawLoadFailedDialog(canvas);
            }
            else if (_marketDialogIndex >= 0)
            {
                var mp = GetMarketAt(_marketDialogIndex);
                if (mp == null) CloseMarketDialog();
                else DrawMarketDialog(canvas, mp);
            }
        }

        private static string FormatScore(double score)
            => score.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture);

        private static (SKColor Normal, SKColor Hover) InstallButtonColors(PluginEntry? local, MarketPlugin mp)
        {
            if (local == null) return (new SKColor(44, 160, 92), new SKColor(58, 184, 108));       // 下载：绿
            if (local.Version.Length > 0 && CompareVersions(mp.Version, local.Version) > 0)
                return (new SKColor(214, 126, 24), new SKColor(238, 146, 34));                    // 更新：橙
            return (new SKColor(0, 120, 212), new SKColor(0, 140, 240));                          // 重装：主题蓝
        }

        private void DrawCenteredButtonLabel(SKCanvas canvas, string label, SKRect button, SKPaint paint)
        {
            float tw = paint.MeasureText(label);
            canvas.DrawText(label, button.MidX - tw / 2f, button.MidY + paint.TextSize * 0.35f, paint);
            paint.Color = _fgColor;   // 共用画笔，画完必须还原
        }

        private float DrawDialogHeader(SKCanvas canvas, SKRect rect, string title)
        {
            float titleMax = rect.Width - DialogPad * 2 - 30;
            DrawTextWithEmoji(canvas, TruncateText(title, _uiTextPaint, titleMax), _uiTextPaint, titleMax,
                rect.Left + DialogPad, rect.Top + DialogTitleH - 4);

            var close = GetMarketDialogCloseRect(rect);
            if (_hoveredDialogClose)
            {
                _dynamicFillPaint.Color = Overlay(30);
                canvas.DrawRoundRect(close, 4, 4, _dynamicFillPaint);
            }
            float ccx = close.MidX, ccy = close.MidY, half = 5.5f;
            using (var cross = new SKPaint
            {
                IsAntialias = true,
                Style = SKPaintStyle.Stroke,
                StrokeWidth = _hoveredDialogClose ? 1.9f : 1.5f,
                StrokeCap = SKStrokeCap.Round,
                Color = _hoveredDialogClose ? _fgColor : Neutral(150)
            })
            {
                canvas.DrawLine(ccx - half, ccy - half, ccx + half, ccy + half, cross);
                canvas.DrawLine(ccx - half, ccy + half, ccx + half, ccy - half, cross);
            }

            return rect.Top + DialogTitleH + 8f;
        }

        private void DrawMarketDialog(SKCanvas canvas, MarketPlugin mp)
        {
            var rect = GetCurrentDialogRect();
            if (rect.Width <= 0) return;

            canvas.DrawRoundRect(rect, 8, 8, _menuBg);
            canvas.DrawRoundRect(rect, 8, 8, _menuBorder);

            string title = _marketDialog switch
            {
                MarketDialog.Rate => $"评价「{mp.Name}」",
                _ => mp.Name,
            };

            // ── 头部：左上标题 + 右上关闭按钮 ──
            float bodyY = DrawDialogHeader(canvas, rect, title);
            switch (_marketDialog)
            {
                case MarketDialog.Detail: DrawDialogDetail(canvas, rect, mp, bodyY); break;
                case MarketDialog.Rate: DrawDialogRate(canvas, rect, mp, bodyY); break;
            }
        }

        // 详情正文：元数据 → 分隔线 → 介绍 → 标签
        private void DrawDialogDetail(SKCanvas canvas, SKRect rect, MarketPlugin mp, float y)
        {
            float innerW = DialogInnerW;
            var descLines = WrapText(mp.Desc, _subTextPaint, innerW);
            const int maxDescLines = 7;
            bool truncated = descLines.Count > maxDescLines;
            if (truncated) descLines = descLines.Take(maxDescLines).ToList();

            string ratingPart = mp.RatingCount > 0
                ? $"{FormatScore(mp.Rating)} 分 · "
                : "暂无评分 · ";
            string meta = ratingPart + $"v{mp.Version} · {mp.Author} · {mp.Updated} 更新 · {mp.Downloads} 次下载";
            _subTextPaint.Color = Neutral(150);
            DrawTextWithEmoji(canvas, TruncateText(meta, _subTextPaint, innerW), _subTextPaint, innerW,
                rect.Left + DialogPad, y + 12);
            _subTextPaint.Color = Neutral(170);
            y += DialogLineH;
            canvas.DrawLine(rect.Left + DialogPad, y, rect.Right - DialogPad, y, _separatorPaint);
            y += 18;
            foreach (var line in descLines)
            {
                DrawTextWithEmoji(canvas, line, _subTextPaint, innerW, rect.Left + DialogPad, y + 12);
                y += DialogLineH;
            }
            if (truncated) { canvas.DrawText("……", rect.Left + DialogPad, y + 12, _subTextPaint); y += DialogLineH; }
            if (mp.Tags.Length > 0)
                canvas.DrawText(TruncateText("标签：" + mp.Tags, _subTextPaint, innerW), rect.Left + DialogPad, y + 12, _subTextPaint);
        }

        // 评分正文：平均分行 → 五颗星（可点半星）→ 状态行
        private void DrawDialogRate(SKCanvas canvas, SKRect rect, MarketPlugin mp, float y)
        {
            string summary = _rateCount > 0 ? $"{FormatScore(_rateAverage)} 分 · {_rateCount} 人评分" : "还没有人评分";
            if (_rateLoading) summary = "正在读取…";
            _subTextPaint.Color = Neutral(150);
            canvas.DrawText(summary, rect.Left + DialogPad, y + 12, _subTextPaint);
            _subTextPaint.Color = Neutral(170);

            double shown = _rateStars > 0 ? _rateStars : _rateMine;
            var stars = GetRateStarsRect(rect);
            DrawStarRow(canvas, stars, shown);

            // 状态行（弹窗底部那行常驻文案）
            float sy = stars.Bottom + 16f;
            string status = _rateStatus;
            if (status.Length == 0)
            {
                status = _rateMine > 0
                    ? $"您已经给此插件打了 {FormatScore(_rateMine)} 分，感谢您的参与"
                    : _rateLoading ? "" : "拖到星星上选分，点一下提交（支持半星，每人只能评一次）";
            }
            if (status.Length > 0)
            {
                _subTextPaint.Color = _rateStatus.Length > 0
                    ? (_rateStatusIsError ? new SKColor(232, 100, 100) : new SKColor(120, 200, 140))
                    : Neutral(160);
                DrawTextWithEmoji(canvas, TruncateText(status, _subTextPaint, DialogInnerW), _subTextPaint,
                    DialogInnerW, rect.Left + DialogPad, sy + 12);
                _subTextPaint.Color = Neutral(170);
            }
        }

        private void DrawStarRow(SKCanvas canvas, SKRect area, double value)
        {
            const int stars = 5;
            float slotW = area.Width / stars;
            float size = Math.Min(slotW - 4f, area.Height);
            float r = size / 2f;
            float cy = area.MidY;

            using var star = BuildStarPath(0f, 0f, r * 0.92f, r * 0.40f);

            for (int i = 0; i < stars; i++)
            {
                float cx = area.Left + slotW * i + slotW / 2f;
                float fillRatio = (float)Math.Clamp(value - i, 0d, 1d);

                canvas.Save();
                canvas.Translate(cx, cy);

                _dynamicStrokePaint.Color = Neutral(150);
                _dynamicStrokePaint.StrokeWidth = 1.4f;
                canvas.DrawPath(star, _dynamicStrokePaint);

                if (fillRatio > 0.001f)
                {
                    canvas.Save();
                    // 按比例裁切：左半颗 = 只露出左边一半宽度的实心星
                    canvas.ClipRect(new SKRect(-r, -r, -r + size * fillRatio, r));
                    _dynamicFillPaint.Color = new SKColor(0, 140, 240);
                    canvas.DrawPath(star, _dynamicFillPaint);
                    canvas.Restore();
                }

                canvas.Restore();
            }
        }

        private static SKPath BuildStarPath(float cx, float cy, float outer, float inner)
        {
            var p = new SKPath();
            for (int i = 0; i < 10; i++)
            {
                float angle = (float)(-Math.PI / 2 + i * Math.PI / 5);
                float rad = (i % 2 == 0) ? outer : inner;
                float x = cx + rad * (float)Math.Cos(angle);
                float y = cy + rad * (float)Math.Sin(angle);
                if (i == 0) p.MoveTo(x, y); else p.LineTo(x, y);
            }
            p.Close();
            return p;
        }

        private void DrawLoadFailedDialog(SKCanvas canvas)
        {
            var rect = GetNoticeDialogRect();
            canvas.DrawRoundRect(rect, 8, 8, _menuBg);
            canvas.DrawRoundRect(rect, 8, 8, _menuBorder);

            float bodyY = DrawDialogHeader(canvas, rect, "插件加载失败");
            _subTextPaint.Color = Neutral(200);
            canvas.DrawText(TruncateText("请确认您已使用最新 NPS。", _subTextPaint, DialogInnerW),
                rect.Left + DialogPad, bodyY + 12, _subTextPaint);
            _subTextPaint.Color = Neutral(150);
            canvas.DrawText(TruncateText("可前往 QQ 群下载最新版本的 NPS。", _subTextPaint, DialogInnerW),
                rect.Left + DialogPad, bodyY + 12 + DialogLineH, _subTextPaint);
            _subTextPaint.Color = Neutral(170);

            var btn = GetDialogSingleButtonRect(rect);
            _dynamicFillPaint.Color = _hoveredDialogButton == 0 ? new SKColor(0, 140, 240) : new SKColor(0, 120, 212);
            canvas.DrawRoundRect(btn, 4, 4, _dynamicFillPaint);
            _uiTextPaint.Color = SKColors.White;
            DrawCenteredButtonLabel(canvas, "好的", btn, _uiTextPaint);
        }

        private void RenderDropdownList(SKCanvas canvas, float x, float yOffset, float w,
                                        string[] options, int selectedIndex, int hoveredIndex, int dimmedIndex,
                                        bool upward = false, int scrollFirst = 0, int visibleRowsOverride = 0)
        {
            // 浮层高度必须钳制在窗口内：
            float anchorY = TITLE_BAR_HEIGHT + yOffset;   // 向下：控件下沿；向上：控件上沿
            int total = options.Length;
            float availFrom = upward ? anchorY - 2 : anchorY;
            int maxRows = upward
                ? Math.Max(1, (int)((availFrom - TITLE_BAR_HEIGHT - 12) / DROPDOWN_ROW_H))
                : Math.Max(1, (int)((HEIGHT - 12 - availFrom) / DROPDOWN_ROW_H));
            int visible = visibleRowsOverride > 0
                ? Math.Min(total, visibleRowsOverride)
                : Math.Min(total, maxRows);
            float listH = visible * DROPDOWN_ROW_H;
            float dY = upward ? anchorY - 2 - listH : anchorY;
            var dRect = new SKRect(x, dY, x + w, dY + listH);
            canvas.DrawRoundRect(dRect, 4, 4, _menuBg);
            canvas.DrawRoundRect(dRect, 4, 4, _menuBorder);

            int maxFirst = Math.Max(0, total - visible);
            int first = Math.Clamp(scrollFirst, 0, maxFirst);

            for (int row = 0; row < visible; row++)
            {
                int i = first + row;
                float itemY = dY + row * DROPDOWN_ROW_H;
                if (hoveredIndex == i)
                    canvas.DrawRoundRect(new SKRect(x + 2, itemY + 2, x + w - 2, itemY + 24), 3, 3, _tabBgSelected);

                _dynamicTextPaint.Color = i == selectedIndex
                    ? new SKColor(0, 120, 212)
                    : (i == dimmedIndex ? Neutral(130) : _fgColor);
                canvas.DrawText(TruncateText(options[i], _dynamicTextPaint, w - 24), x + 12, itemY + 18, _dynamicTextPaint);
            }
            _dynamicTextPaint.Color = _fgColor;

            if (total > visible)
            {
                float trackH = listH - 8;
                //    这就是「下拉滑轨溢出菜单主体」那个现象。
                DrawScrollBar(canvas, x + w - 6, dY + 4, trackH, visible / (float)total, first / (float)Math.Max(1, maxFirst));
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
                    _dynamicTextPaint.Color = i == _selectedPlatformIndex ? new SKColor(0, 120, 212) : _fgColor;
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
                    _dynamicTextPaint.Color = i == selectedMode ? new SKColor(0, 120, 212) : _fgColor;
                    canvas.DrawText(_matchModeOptions[i], mX + 12, itemY + 18, _dynamicTextPaint);
                }
            }

            if (_selectedTab == 2 && _appDropdownOpen)
            {
                int rows = Math.Clamp(_appOptions.Length, 1, 8);
                float mW = APP_MENU_W; float mX = MATCH_MENU_RIGHT - mW; float mY = MATCH_MENU_Y; float mH = rows * 26;
                var mRect = new SKRect(mX, mY, mX + mW, mY + mH);
                canvas.DrawRoundRect(mRect, 4, 4, _menuBg);
                canvas.DrawRoundRect(mRect, 4, 4, _menuBorder);

                if (_appOptions.Length == 0)
                {
                    _dynamicTextPaint.Color = Neutral(150);
                    canvas.DrawText("暂无活动会话", mX + 12, mY + 18, _dynamicTextPaint);
                    _dynamicTextPaint.Color = _fgColor;
                }
                else
                {
                    for (int i = 0; i < rows; i++)
                    {
                        float itemY = mY + i * 26;
                        if (_hoveredAppIndex == i)
                            canvas.DrawRoundRect(new SKRect(mX + 2, itemY + 2, mX + mW - 2, itemY + 24), 3, 3, _tabBgSelected);
                        _dynamicTextPaint.Color = string.Equals(_appOptions[i], MediaController.ManualSessionAppId, StringComparison.OrdinalIgnoreCase)
                            ? new SKColor(0, 120, 212) : _fgColor;
                        canvas.DrawText(TruncateText(_appOptions[i], _dynamicTextPaint, mW - 24), mX + 12, itemY + 18, _dynamicTextPaint);
                    }
                    _dynamicTextPaint.Color = _fgColor;
                }
            }

            // 消息通知内容下拉菜单（通用设置）
            if (_selectedTab == 0 && _toastModeDropdownOpen)
            {
                RenderDropdownList(canvas, TOAST_MODE_CTRL_X, TOAST_MODE_ROW_Y + TOAST_MODE_ROW_H + 2, TOAST_MODE_CTRL_W,
                    _toastModeOptions, _selectedToastModeIndex, _hoveredToastModeIndex, dimmedIndex: -1);
            }

            if (_selectedTab == 0 && _toastSoundDropdownOpen)
            {
                GetToastSoundMenuLayout(out float soundMenuTop, out int soundVisible, out _);
                RenderDropdownList(canvas, SOUND_CTRL_X, soundMenuTop - TITLE_BAR_HEIGHT, SOUND_CTRL_W,
                    ToastSoundConfig.BuildOptionLabels(), ToastSoundConfig.SelectedIndex, _hoveredToastSoundIndex,
                    dimmedIndex: ToastSoundConfig.IsUsableFile(ToastSoundConfig.CustomPath, out _)
                        ? -1 : ToastSoundConfig.CustomIndex,
                    scrollFirst: _dropdownScroll, visibleRowsOverride: soundVisible);
            }

            //    所以向上展开 —— 向下展开会一路拖出窗口。
            if (_selectedTab == 0 && _soundVolumeDropdownOpen)
            {
                GetVolumeMenuLayout(out _, out _, out int volVisible);
                RenderDropdownList(canvas, SOUND_VOL_X, SOUND_BOX_Y, SOUND_VOL_W,
                    VolumeOptionLabels, ToastSoundConfig.VolumeIndex, _hoveredSoundVolumeIndex, dimmedIndex: -1,
                    upward: true, scrollFirst: 0, visibleRowsOverride: volVisible);
            }

            if (_selectedTab == 1 && _monitorDropdownOpen)
            {
                float dX = WIDTH - CONTENT_RM - MONITOR_DD_W;
                float dY = TITLE_BAR_HEIGHT + MONITOR_ROW_Y + (ROW_H - MONITOR_DD_H) / 2f + MONITOR_DD_H + 2f - _displayPageScroll;
                float dW = MONITOR_DD_W; float dH = _monitorOptions.Length * 26;
                var dRect = new SKRect(dX, dY, dX + dW, dY + dH);
                canvas.DrawRoundRect(dRect, 4, 4, _menuBg);
                canvas.DrawRoundRect(dRect, 4, 4, _menuBorder);
                for (int i = 0; i < _monitorOptions.Length; i++)
                {
                    float itemY = dY + i * 26;
                    if (_hoveredMonitorDropdownIndex == i) canvas.DrawRoundRect(new SKRect(dX + 2, itemY + 2, dX + dW - 2, itemY + 24), 3, 3, _tabBgSelected);
                    _dynamicTextPaint.Color = i == Renderer.TargetMonitorIndex ? new SKColor(0, 120, 212) : _fgColor;
                    canvas.DrawText(_monitorOptions[i], dX + 12, itemY + 18, _dynamicTextPaint);
                }
            }
        }
    }
}
