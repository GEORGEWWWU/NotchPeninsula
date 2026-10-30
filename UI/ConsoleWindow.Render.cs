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

        // 侧栏「插件市场」右上角的更新数角标：黄底白字小胶囊，>9 显示 9+。
        // 纯展示，不单独吃点击（整行本来就是页签热区）。
        private void DrawMarketTabBadge(SKCanvas canvas, float rowTop)
        {
            string text = MarketUpdateCount > 9 ? "9+" : MarketUpdateCount.ToString();
            const float badgeH = 13f;
            _dynamicTextPaint.TextSize = 9.5f;
            float tw = _dynamicTextPaint.MeasureText(text);
            float w = Math.Max(tw + 8f, badgeH);
            float right = TAB_BOX_R - 12f;
            var r = new SKRect(right - w, rowTop + 4f, right, rowTop + 4f + badgeH);

            _dynamicFillPaint.Color = new SKColor(232, 168, 48);   // 与列表评分星星同一套黄
            canvas.DrawRoundRect(r, badgeH / 2f, badgeH / 2f, _dynamicFillPaint);

            _dynamicTextPaint.Color = SKColors.White;
            _dynamicTextPaint.TextAlign = SKTextAlign.Center;
            canvas.DrawText(text, r.MidX, r.MidY + _dynamicTextPaint.TextSize * 0.35f, _dynamicTextPaint);

            // 共用画笔复位：侧栏后面还有别的文字要画
            _dynamicTextPaint.TextAlign = SKTextAlign.Left;
            _dynamicTextPaint.TextSize = 13f;
            _dynamicTextPaint.Color = _fgColor;
        }

        // 选中态的整体：背景方块 + 左侧蓝色竖条，一起滑到选中行。
        // 急出缓入位移（先快后慢）。**只有蓝条做「拉丝」**：背景块保持 36 高，方块跟着拉长反而显得不跟手。
        private void DrawTabSelection(SKCanvas canvas)
        {
            float p = TabEase(_tabSlideT);
            float d = _tabSlideToY - _tabSlideFromY;
            float top = _tabSlideFromY + d * p;

            canvas.DrawRoundRect(new SKRect(TAB_BOX_L, TITLE_BAR_HEIGHT + top, TAB_BOX_R, TITLE_BAR_HEIGHT + top + TAB_ROW_H), 4, 4, _tabBgSelected);

            // 蓝条以自身中线为中心对称外扩：中点最强、两端归零，所以起止两端没有跳变。
            // 幅度固定、与滑动距离无关，最大伸展时 20 + 10 = 30 仍稳稳落在 36 高的方块里。
            float stretch = TAB_SLIDE_STRETCH * (float)Math.Sin(Math.PI * Math.Sqrt(Math.Clamp(_tabSlideT, 0f, 1f)));
            float mid = top + TAB_ROW_H * 0.5f;
            float half = (TAB_ROW_H * 0.5f - TAB_BAR_DY) + stretch * 0.5f;

            canvas.DrawRoundRect(new SKRect(TAB_BOX_L, TITLE_BAR_HEIGHT + mid - half, TAB_BOX_L + 3, TITLE_BAR_HEIGHT + mid + half),
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

            if (_tabSlideDst != _selectedTab)
            {
                _tabSlideFromY += (_tabSlideToY - _tabSlideFromY) * TabEase(_tabSlideT);
                _tabSlideToY = TabRowY(_selectedTab);
                _tabSlideDst = _selectedTab;
                _tabSlideStarted = Environment.TickCount64;
                _tabSlideT = 0f;
                Logger.Debug($"[侧边栏] 起滑 from={_tabSlideFromY:F0} to={_tabSlideToY:F0}");
            }

            // 进度**每帧按时钟现算**（不靠「拍了多少帧」累计）：掉帧、节拍晚到都只是少画几帧，
            // 位置永远对；哪怕节拍彻底停了，下一次 Render 也会把它推到终点，不会把块悬在半路。
            if (_tabSlideT < 1f)
            {
                _tabSlideT = Math.Clamp((Environment.TickCount64 - _tabSlideStarted) / TAB_SLIDE_MS, 0f, 1f);
                if (_tabSlideT >= 1f)
                    Logger.Debug($"[侧边栏] 滑完 → dst={_tabSlideDst} 实际用时 {Environment.TickCount64 - _tabSlideStarted}ms");
                else
                    StartDisplayHoverAnim();   // 还没走完：保证节拍在跑（幂等，重复调用无副作用）
            }
        }

        // 画整张卡片底 + 一行开关内容。
        private void DrawToggleCard(SKCanvas canvas, float yOffset, string title, string sub, bool state, bool hovered, bool disabled = false, string? badge = null)
        {
            var cardRect = new SKRect(CONTENT_L, TITLE_BAR_HEIGHT + yOffset, WIDTH - CONTENT_RM, TITLE_BAR_HEIGHT + yOffset + TOGGLE_CARD_H);
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

        // 外链小图标：方框里一枚指向右上的短箭头（MSP 卡片标签后面那枚）。
        // 线条图标，hover 变主题蓝 —— 和「关于软件」页的链接同一套反馈。
        private void DrawExternalLinkIcon(SKCanvas canvas, SKRect hit, bool hovered)
        {
            float s = MSP_WIKI_ICON;
            float x = hit.MidX - s / 2f;
            float y = hit.MidY - s / 2f;

            float oldStroke = _dynamicStrokePaint.StrokeWidth;
            var oldCap = _dynamicStrokePaint.StrokeCap;
            var oldJoin = _dynamicStrokePaint.StrokeJoin;

            _dynamicStrokePaint.Color = hovered ? new SKColor(0, 140, 240) : Neutral(150);
            _dynamicStrokePaint.StrokeWidth = 1.4f;
            _dynamicStrokePaint.StrokeCap = SKStrokeCap.Round;
            _dynamicStrokePaint.StrokeJoin = SKStrokeJoin.Round;

            canvas.DrawRoundRect(new SKRect(x + 1.3f, y + 1.3f, x + s - 1.3f, y + s - 1.3f), 3.4f, 3.4f, _dynamicStrokePaint);

            using (var arrow = new SKPath())
            {
                arrow.MoveTo(x + 4.5f, y + 10.5f);      // 斜线
                arrow.LineTo(x + 10.5f, y + 4.5f);
                arrow.MoveTo(x + 6.7f, y + 4.5f);       // 箭头两条短边
                arrow.LineTo(x + 10.5f, y + 4.5f);
                arrow.LineTo(x + 10.5f, y + 8.3f);
                canvas.DrawPath(arrow, _dynamicStrokePaint);
            }

            _dynamicStrokePaint.StrokeWidth = oldStroke;
            _dynamicStrokePaint.StrokeCap = oldCap;
            _dynamicStrokePaint.StrokeJoin = oldJoin;
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
            string[] labels, int selected, int hovered, bool disabled = false)
        {
            int n = labels.Length;
            float segW = w / n;

            _dynamicFillPaint.Color = disabled ? Overlay(5) : Overlay(8);
            canvas.DrawRoundRect(new SKRect(x, y, x + w, y + h), 6, 6, _dynamicFillPaint);

            for (int i = 0; i < n; i++)
            {
                var r = new SKRect(x + i * segW + (i > 0 ? 2f : 0f), y,
                    x + (i + 1) * segW - (i < n - 1 ? 2f : 0f), y + h);
                if (i == selected)
                {
                    _dynamicFillPaint.Color = disabled ? Neutral(80) : new SKColor(0, 120, 212);
                    canvas.DrawRoundRect(r, 5, 5, _dynamicFillPaint);
                }
                else if (i == hovered && !disabled)
                {
                    _dynamicFillPaint.Color = Overlay(24);
                    canvas.DrawRoundRect(r, 5, 5, _dynamicFillPaint);
                }

                // 文本用完立刻恢复基准色：本帧后面还要用同一支画笔
                _uiTextPaint.Color = disabled
                    ? (i == selected ? Neutral(190) : Neutral(110))
                    : (i == selected ? SKColors.White : _fgColor);
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

        // ---- 动画节拍（列表行悬停淡入 / 侧边栏选中块滑动，共用这一条）----
        // 唯一来源：一条后台轻量线程，每 15ms 往窗口 Post 一条私有消息；UI 线程收到就画一帧。
        //
        // **不再用 SetTimer / WM_TIMER。** 那张表会静默停摆 —— 标志位说「在跑」而表其实已经没了、
        // 定时器被同 id 顶掉、或者队列里一直有别的消息轮不到 WM_TIMER，任一种都表现为「动画僵住，
        // 重开窗口才好」，而且没有任何报错。PostMessage + UI 线程渲染这条路是点一下页面就在用的，
        // 不会被这类状态卡住，也拦不住它的原因能被日志看见。
        //
        // 线程里**只允许** PostMessage：不渲染、不碰 COM、不碰 Z 序（同 Renderer 那边的铁律）。
        private void StartDisplayHoverAnim()
        {
            lock (_animSync)
            {
                _animWant = true;                       // 幂等：正在跑就只是把「继续跑」再确认一次
                if (_animThread != null || _hwnd == IntPtr.Zero) return;
                _animThread = new Thread(AnimTickLoop) { IsBackground = true, Name = "NPS-ConsoleAnim" };
                _animThread.Start();
            }
        }

        private void StopDisplayHoverAnim()
        {
            lock (_animSync)
            {
                if (!_animWant && _animThread == null) return;
                _animWant = false;                      // 表在 ≤15ms 内自己退出，这里不 Join（UI 线程不能等）
            }
        }

        private void AnimTickLoop()
        {
            Win32.TimeBeginPeriod(1);                   // 15ms 的 Sleep 不至于被系统 tick 拉成 ~31ms
            try
            {
                while (true)
                {
                    lock (_animSync) { if (!_animWant) { _animThread = null; return; } }
                    Win32.PostMessage(_hwnd, WM_ANIM_TICK, IntPtr.Zero, IntPtr.Zero);
                    Thread.Sleep(ANIM_TICK_MS);
                }
            }
            finally
            {
                Win32.TimeEndPeriod(1);
                lock (_animSync) { _animThread = null; }
            }
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

            // 侧边栏：选中块的滑动 —— 进度由 SyncTabSlide 在每帧 Render 里按时钟推进，
            // 这里只提供「还在滑」这个事实（别在这儿算 T，否则和渲染那侧的口径就分家了）。
            if (_tabSlideT < 1f) animating = true;

            // 否则会停在 0.99 那种「差一点点」的状态上。
            Render();
            // Render() 里的 SyncTabSlide 可能刚好起了一段新滑动：按渲染**之后**的真实状态决定表留不留，
            // 别拿渲染前算出的旧结论把它停掉 —— 那会让这段滑动永远走不完。
            return animating || _tabSlideT < 1f;
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
            // 有可更新插件才画角标：数字超 9 显示 9+，与市场页「一键更新」共用同一份计数
            if (MarketUpdateCount > 0) DrawMarketTabBadge(canvas, TITLE_BAR_HEIGHT + TabRowY(7));
            canvas.DrawLine(20, TITLE_BAR_HEIGHT + TabRowY(7) + 42f, 160, TITLE_BAR_HEIGHT + TabRowY(7) + 42f, _separatorPaint);
            DrawTab(canvas, 4, "关于软件", TabRowY(4));

            DrawExportBundleButton(canvas);
        }

        // 侧边栏左下角：导出诊断包（常驻，不绑页签）。
        // 用户报 bug 时唯一要按的地方 —— 一个 zip 到手，版本 / 系统 / 显示器 / 配置快照 /
        // 插件清单 / 最近日志全在里头，省掉一轮轮追问。
        private void DrawExportBundleButton(SKCanvas canvas)
        {
            var rect = new SKRect(EXPORT_BTN_X, EXPORT_BTN_Y,
                                  EXPORT_BTN_X + EXPORT_BTN_W, EXPORT_BTN_Y + EXPORT_BTN_H);

            _dynamicFillPaint.Color = _aboutExportHovered ? Overlay(30) : Overlay(15);
            canvas.DrawRoundRect(rect, 5f, 5f, _dynamicFillPaint);

            _dynamicTextPaint.TextAlign = SKTextAlign.Center;
            _dynamicTextPaint.TextSize = 12.5f;
            _dynamicTextPaint.Color = _aboutExportHovered ? new SKColor(0, 140, 240) : Neutral(200);
            canvas.DrawText(_aboutExporting ? "正在打包…" : "导出诊断包",
                EXPORT_BTN_X + EXPORT_BTN_W / 2f, EXPORT_BTN_Y + 18.5f, _dynamicTextPaint);

            // 成功不说、失败说：导出成功靠资源管理器选中文件来表达。失败就在按钮上方挂一行。
            // 提示行比按钮宽（可占满侧栏），左对齐 —— 居中放不下时两边会顶到侧栏外。
            if (_aboutExportHint.Length > 0)
            {
                _dynamicTextPaint.TextSize = 11f;
                _dynamicTextPaint.TextAlign = SKTextAlign.Left;
                _dynamicTextPaint.Color = new SKColor(200, 80, 80);
                canvas.DrawText(TruncateText(_aboutExportHint, _dynamicTextPaint, TAB_BOX_R - EXPORT_BTN_X),
                    EXPORT_BTN_X, EXPORT_BTN_Y - 8f, _dynamicTextPaint);
            }

            // 共享画笔复位：后面的页签内容默认按左对齐排版
            _dynamicTextPaint.TextAlign = SKTextAlign.Left;
            _dynamicTextPaint.TextSize = 13f;
        }

        // 页签：通用设置
        private void RenderTabGeneral(SKCanvas canvas)
        {
            DrawToggleCard(canvas, CARD_TOP_Y, "开机自启", "跟随系统启动自动运行该程序", _isAutoStartEnabled, _toggleHovered);
            // 窗口置顶（与下方消息通知整组互换位置）
            DrawToggleCard(canvas, TOPMOST_CARD_Y, "窗口置顶", "开启后刘海将始终保持在其他窗口最上层", NotchWindow.IsTopmostEnabled, _topmostToggleHovered);
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

            DrawDropdownBox(canvas, TOAST_MODE_CTRL_X, TITLE_BAR_HEIGHT + TOAST_MODE_ROW_Y, TOAST_MODE_CTRL_W, TOAST_MODE_ROW_H,
                _toastModeOptions[_selectedToastModeIndex], _toastModeDropdownHovered, enabled: true,
                open: _toastModeDropdownOpen);

            canvas.DrawLine(CONTENT_TEXT_X, TITLE_BAR_HEIGHT + SOUND_SEP_Y, WIDTH - CONTENT_TEXT_RM, TITLE_BAR_HEIGHT + SOUND_SEP_Y, _separatorPaint);

            // ── 行 3：消息提示音开关（内容随分隔线下方两行统一下移 SEP_ROW_CONTENT_DY）──
            DrawToggleRow(canvas, SOUND_ROW3_Y + SEP_ROW_CONTENT_DY, "消息提示音",
                _soundHint.Length > 0 ? _soundHint : "新消息到达时播放提示音",
                ToastSoundConfig.IsEnabled, _soundToggleHovered);

            // ── 行 4：提示音设置（行 3 的附属，无开关）──
            bool soundRowEnabled = ToastSoundConfig.IsRowEnabled;
            bool soundReady = ToastSoundConfig.IsSourceReady;

            if (!soundRowEnabled) _uiTextPaint.Color = Neutral(100);
            canvas.DrawText("提示音", SOUND_LABEL_X, TITLE_BAR_HEIGHT + SOUND_ROW_TITLE_Y, _uiTextPaint);
            if (!soundRowEnabled) _uiTextPaint.Color = _fgColor;

            DrawDropdownBox(canvas, SOUND_CTRL_X, TITLE_BAR_HEIGHT + SOUND_BOX_Y, SOUND_CTRL_W, SOUND_ROW_H,
                ToastSoundConfig.CurrentDisplayText(), _toastSoundDropdownHovered, enabled: soundRowEnabled,
                open: _toastSoundDropdownOpen);

            DrawDropdownBox(canvas, SOUND_VOL_X, TITLE_BAR_HEIGHT + SOUND_BOX_Y, SOUND_VOL_W, SOUND_ROW_H,
                $"{ToastSoundConfig.VolumePercent}%", _soundVolumeDropdownHovered, enabled: soundReady,
                open: _soundVolumeDropdownOpen);

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
            DrawToggleCard(canvas, MSP_CARD_Y, MspCardTitle,
                "让本机 MSP 程序弹通知、读控媒体",
                MspNotchBridge.IsEnabled, _mspToggleHovered, badge: MspBadgeText);
            DrawExternalLinkIcon(canvas, MspWikiIconRect(), _mspWikiHovered);

            var fontCard = new SKRect(CONTENT_L, TITLE_BAR_HEIGHT + FONT_CARD_Y, WIDTH - CONTENT_RM, TITLE_BAR_HEIGHT + FONT_CARD_Y + TOGGLE_CARD_H);
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

        private void DrawDropdownBox(SKCanvas canvas, float x, float y, float w, float h,
                                     string text, bool hovered, bool enabled,
                                     bool open = false, bool builtinTag = false)
        {
            var rect = new SKRect(x, y, x + w, y + h);
            bool hot = enabled && (hovered || open);
            _dynamicFillPaint.Color = !enabled ? Overlay(5) : hot ? Overlay(20) : Overlay(10);
            canvas.DrawRoundRect(rect, 7, 7, _dynamicFillPaint);

            // 展开时描一圈淡蓝：让「正在选」这件事有明确的视觉落点
            if (enabled && open)
            {
                _dynamicStrokePaint.Color = new SKColor(0, 120, 212, 150);
                canvas.DrawRoundRect(rect, 7, 7, _dynamicStrokePaint);
            }

            const float chevronArea = 26f;
            float tagW = builtinTag ? _subTextPaint.MeasureText("内置") + 14f : 0f;
            float textLimit = w - 12f - chevronArea - (tagW > 0f ? tagW + 8f : 0f);
            _uiTextPaint.Color = enabled ? _fgColor : Neutral(110);
            canvas.DrawText(TruncateText(text, _uiTextPaint, textLimit),
                rect.Left + 12f, rect.Top + h / 2f + 5f, _uiTextPaint);
            _uiTextPaint.Color = _fgColor;

            if (builtinTag)
                DrawBuiltinTag(canvas, rect.Right - chevronArea - tagW - 2f, rect.Top + (h - 16f) / 2f);

            float cx = rect.Right - 19f;
            float cy = rect.MidY;
            float dir = open ? -1f : 1f;   // 展开时箭头朝上
            float tipY = cy + dir * 2.5f;
            _chevronPaint.Color = !enabled ? Neutral(90) : hot ? Neutral(200) : Neutral(150);
            canvas.DrawLine(cx, tipY - dir * 5f, cx + 5f, tipY, _chevronPaint);
            canvas.DrawLine(cx + 5f, tipY, cx + 10f, tipY - dir * 5f, _chevronPaint);
            _chevronPaint.Color = Neutral(150);
        }

        // 蓝色「内置」小胶囊：列表项和折叠框共用，宽度返回给调用方排版。
        private float DrawBuiltinTag(SKCanvas canvas, float x, float top)
        {
            const string tag = "内置";
            float tagW = _subTextPaint.MeasureText(tag) + 14f;
            _dynamicFillPaint.Color = new SKColor(0, 120, 212, 40);
            canvas.DrawRoundRect(new SKRect(x, top, x + tagW, top + 16f), 8, 8, _dynamicFillPaint);
            _subTextPaint.Color = new SKColor(0, 140, 240);
            canvas.DrawText(tag, x + 7f, top + 12.5f, _subTextPaint);
            _subTextPaint.Color = Neutral(170);
            return tagW;
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

                // 单选 Radio 按钮与文本：作为整体在框内居中（文字宽度随名字变，钉死偏移会歪）。
                float radioY = y + 72;
                const float radioDiameter = 12f, radioTextGap = 9f;
                float radioGroupW = radioDiameter + radioTextGap + _dynamicTextPaint.MeasureText(name);
                float radioGroupLeft = x + (STYLE_OPT_W - radioGroupW) / 2f;
                float radioCx = radioGroupLeft + radioDiameter / 2f;
                canvas.DrawCircle(radioCx, radioY - 4, 6, _dynamicStrokePaint);
                if (isSelected)
                {
                    _dynamicFillPaint.Color = new SKColor(0, 120, 212);
                    canvas.DrawCircle(radioCx, radioY - 4, 3, _dynamicFillPaint);
                }
                _dynamicTextPaint.Color = isSelected ? new SKColor(0, 140, 240) : _fgColor;
                canvas.DrawText(name, radioGroupLeft + radioDiameter + radioTextGap, radioY + 1, _dynamicTextPaint);
            }

            DrawStyleOption(0, "经典刘海", STYLE_OPT_X, TITLE_BAR_HEIGHT + STYLE_OPT_Y + page);
            DrawStyleOption(1, "悬浮灵动岛", STYLE_OPT_X + STYLE_OPT_W + STYLE_OPT_GAP, TITLE_BAR_HEIGHT + STYLE_OPT_Y + page);

            float monitorRowY = TITLE_BAR_HEIGHT + MONITOR_ROW_Y + page;
            canvas.DrawText("目标显示器", CONTENT_TEXT_X, monitorRowY + ROW_LABEL_DY, _uiTextPaint);
            float mdX = DISPLAY_CTRL_RIGHT - MONITOR_DD_W;
            float mdY = monitorRowY + (ROW_H - MONITOR_DD_H) / 2f;
            string mName = Renderer.TargetMonitorIndex < _monitorOptions.Length ? _monitorOptions[Renderer.TargetMonitorIndex] : "未知";
            DrawDropdownBox(canvas, mdX, mdY, MONITOR_DD_W, MONITOR_DD_H, mName,
                _monitorDropdownHovered, enabled: true, open: _monitorDropdownOpen);

            // ── 显示模式（单行分段器）：待机 / 普通 ──
            float modeRowY = TITLE_BAR_HEIGHT + MODE_ROW_Y + page;
            canvas.DrawText("显示模式", CONTENT_TEXT_X, modeRowY + ROW_LABEL_DY, _uiTextPaint);
            DrawSegmented(canvas, MODE_SEG_X, modeRowY + (ROW_H - SEG_H) / 2f, MODE_SEG_W, SEG_H,
                ["待机模式", "普通模式"], Renderer.StandbyActive ? 0 : 1, _hoveredDisplayModeIndex);

            float sceneRowY = TITLE_BAR_HEIGHT + SCENE_ROW_Y + page;
            canvas.DrawText("待机显示内容", CONTENT_TEXT_X, sceneRowY + ROW_LABEL_DY, _uiTextPaint);
            DrawDropdownBox(canvas, SCENE_SEG_X, sceneRowY + (ROW_H - SCENE_DD_H) / 2f, SCENE_SEG_W, SCENE_DD_H,
                CurrentStandbySceneName(), _sceneDropdownHovered, enabled: true,
                open: _sceneDropdownOpen, builtinTag: BuiltinWidgets.IsBuiltin(Renderer.StandbySceneKey));

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
                float contentH = TITLE_BAR_HEIGHT + DISPLAY_CARD_Y + DISPLAY_CARD_H + 6f;
                DrawScrollBar(canvas, WIDTH - 8, pageTrackTop, pageTrackH,
                    HEIGHT / contentH, _displayPageScroll / pageMaxScroll, minThumbH: 24f);
            }
        }

        // 页签：媒体设置
        private void RenderTabMedia(SKCanvas canvas)
        {
            DrawToggleCard(canvas, 12, "媒体控制", "允许在刘海中显示和控制系统媒体播放", MediaController.IsMediaControlEnabled, _mediaToggleHovered);

            var platformCardRect = new SKRect(CONTENT_L, PLATFORM_CARD_Y, WIDTH - CONTENT_RM, PLATFORM_CARD_Y + PLATFORM_CARD_H);
            canvas.DrawRoundRect(platformCardRect, 6, 6, _cardBg);
            canvas.DrawRoundRect(platformCardRect, 6, 6, _cardBorder);

            // ── 第 1 行：目标媒体平台 ──
            canvas.DrawText("目标媒体平台", CONTENT_TEXT_X, TITLE_BAR_HEIGHT + 110, _uiTextPaint);
            canvas.DrawText(MediaController.TargetPlatform == "browser" ? "仅接管浏览器内的播放会话" : "多平台共存时，优先截获并接管的平台",
                CONTENT_TEXT_X, TITLE_BAR_HEIGHT + 130, _subTextPaint);

            float dW = 110; float dX = WIDTH - 140; float dY = TITLE_BAR_HEIGHT + 96; float dH = 32;
            DrawDropdownBox(canvas, dX, dY, dW, dH, _platforms[_selectedPlatformIndex].Name,
                _dropdownHovered, enabled: true, open: _dropdownOpen);

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
            DrawDropdownBox(canvas, moX, mBoxY, mBoxW, mBoxH,
                _matchModeOptions[MediaController.IsManualSessionMatch ? 1 : 0],
                _matchModeDropdownHovered, enabled: matchEnabled, open: _matchModeDropdownOpen);

            string appLabel = !appBoxEnabled ? "自动匹配"
                : (!MediaController.HasActiveSessions ? ""
                    : (MediaController.ManualSessionAppId.Length > 0 ? MediaController.ManualSessionAppId : "未选择"));
            DrawDropdownBox(canvas, appX, mBoxY, mBoxW, mBoxH, appLabel,
                _appDropdownHovered, enabled: appBoxEnabled, open: _appDropdownOpen);

            float lyricY = LYRIC_CARD_Y;
            var lyricRect = new SKRect(CONTENT_L, lyricY, WIDTH - CONTENT_RM, lyricY + LYRIC_CARD_H);
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
            var autoHideCardRect = new SKRect(CONTENT_L, TITLE_BAR_HEIGHT + AUTOHIDE_CARD_Y, WIDTH - CONTENT_RM, TITLE_BAR_HEIGHT + AUTOHIDE_CARD_Y + AUTOHIDE_CARD_H);
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

            DrawToggleCard(canvas, MEDIA_EXP_CARD_Y, "媒体交互方式",
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
            DrawToggleCard(canvas, APP_LAUNCH_CARD_Y, "双击封面跳转应用", appLaunchSub,
                MediaController.IsAppLaunchEnabled, _appLaunchToggleHovered);

            DrawToggleCard(canvas, PASS_CARD_Y, "穿透模式", "悬停时透明并允许鼠标穿透本体与底层窗口交互", Renderer.PassthroughModeEnabled, _passToggleHovered);

            DrawIslandHotkeyCard(canvas);
        }

        // 交互页「唤出灵动岛」卡片：只有一个键位，结构与媒体页「全局快捷键」卡片同源
        //（标题 + 副标题 + 一行「名字 … [键位框]」）。
        private void DrawIslandHotkeyCard(SKCanvas canvas)
        {
            // ⚠️ 这里必须跟 DrawToggleCard 一样补上 TITLE_BAR_HEIGHT：
            // ISLAND_HOTKEY_CARD_Y 等常量都是「相对内容区顶部」的偏移，而画布用的是全窗口坐标。
            // 少加这一段的后果：卡片被画到上方「穿透模式」卡身上（看着像两张卡叠在一起），
            // 而命中区（ConsoleWindow.WndProc.cs）用的是 TITLE_BAR_HEIGHT + ISLAND_HOTKEY_CARD_Y + …，
            // 于是「看得见的卡片点不动、点得动的地方反而是空的」。
            float cardY = TITLE_BAR_HEIGHT + ISLAND_HOTKEY_CARD_Y;
            var cardRect = new SKRect(CONTENT_L, cardY, WIDTH - CONTENT_RM, cardY + ISLAND_HOTKEY_CARD_H);
            canvas.DrawRoundRect(cardRect, 6, 6, _cardBg);
            canvas.DrawRoundRect(cardRect, 6, 6, _cardBorder);

            canvas.DrawText("唤出灵动岛", CONTENT_TEXT_X, cardY + 19, _uiTextPaint);

            // 总开关（出厂关，与媒体页那张卡的开关同一套画法）
            float tW = 42f, tH = 20f;
            float tX = WIDTH - CONTENT_RM - 16f - tW;
            float tY = cardY + 12f;
            var tRect = new SKRect(tX, tY, tX + tW, tY + tH);
            if (IslandHotkey.IsEnabled)
            {
                _dynamicFillPaint.Color = _islandHotkeyToggleHovered ? new SKColor(0, 140, 240) : new SKColor(0, 120, 212);
                canvas.DrawRoundRect(tRect, tH / 2, tH / 2, _dynamicFillPaint);
                canvas.DrawCircle(tX + tW - tH / 2, tY + tH / 2, tH / 2 - 4, _toggleCirclePaint);
            }
            else
            {
                _dynamicStrokePaint.Color = _islandHotkeyToggleHovered ? Neutral(150) : Neutral(100);
                canvas.DrawRoundRect(tRect, tH / 2, tH / 2, _dynamicStrokePaint);
                _toggleCirclePaint.Color = _islandHotkeyToggleHovered ? Neutral(200) : Neutral(150);
                canvas.DrawCircle(tX + tH / 2, tY + tH / 2, tH / 2 - 4, _toggleCirclePaint);
                _toggleCirclePaint.Color = SKColors.White;
            }

            // 优先级：错误提示 > 录制操作说明 > 未开启 > 平时说明。
            // 注册失败（键被别的程序占着）在没录制时也要一直挂着，否则用户只会觉得「按了没反应」。
            string hint = _islandHotkeyHint.Length > 0 ? _islandHotkeyHint : IslandHotkey.LastError;
            bool hasHint = hint.Length > 0;
            string sub = hasHint ? hint
                : (_islandHotkeyRecording ? "按下按键录制 · Esc 取消 · Backspace 清空"
                    : (!IslandHotkey.IsEnabled ? "开启后按下可隐藏 / 唤出灵动岛 · 点按键框可重录"
                                               : "全局生效 · 点按键框可重录"));
            _subTextPaint.Color = hasHint ? new SKColor(230, 122, 92) : Neutral(170);
            float subMax = (WIDTH - CONTENT_RM - 16f - tW) - 12f - CONTENT_TEXT_X;
            DrawTextWithEmoji(canvas, sub, _subTextPaint, subMax, CONTENT_TEXT_X, cardY + 36);
            _subTextPaint.Color = Neutral(170);

            float rowY = cardY + HOTKEY_HEAD_H;
            canvas.DrawText("快捷键", CONTENT_TEXT_X, rowY + 20, _subTextPaint);

            var boxRect = new SKRect(HOTKEY_BOX_X, rowY + 4, HOTKEY_BOX_RIGHT, rowY + 4 + HOTKEY_BOX_H);

            _dynamicFillPaint.Color = _islandHotkeyRecording ? new SKColor(0, 120, 212, 70)
                : (_islandHotkeyHovered ? Overlay(30) : Overlay(15));
            canvas.DrawRoundRect(boxRect, 4, 4, _dynamicFillPaint);
            if (_islandHotkeyRecording)
            {
                float oldStroke = _dynamicStrokePaint.StrokeWidth;
                _dynamicStrokePaint.Color = new SKColor(0, 140, 240);
                _dynamicStrokePaint.StrokeWidth = 1f;
                canvas.DrawRoundRect(boxRect, 4, 4, _dynamicStrokePaint);
                _dynamicStrokePaint.StrokeWidth = oldStroke;
            }

            string text = _islandHotkeyRecording ? "按下按键…" : IslandHotkey.FormatKey();
            _dynamicTextPaint.Color = _islandHotkeyRecording ? new SKColor(0, 150, 255)
                : (IslandHotkey.IsBound ? _fgColor : Neutral(120));
            DrawTextWithEmoji(canvas, text, _dynamicTextPaint,
                HOTKEY_BOX_W - 24f, HOTKEY_BOX_RIGHT - 12f, rowY + 20.5f, rightAlign: true);
            _dynamicTextPaint.Color = _fgColor;
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

            // 液态玻璃下明暗由桌面背景决定，黑/白/系统摆着只会误导（悬停/点击已一并屏蔽）：
            // 换成一句明说，告诉用户文字颜色正在自适应。
            if (Renderer.IslandLiquidGlass)
            {
                const string glassThemeHint = "液态玻璃材质下文字自适应颜色";
                var prev = _subTextPaint.Color;
                _subTextPaint.Color = Neutral(150);
                canvas.DrawText(glassThemeHint,
                    THEME_SEG_X + THEME_SEG_W - _subTextPaint.MeasureText(glassThemeHint),
                    TITLE_BAR_HEIGHT + THEME_SEG_Y + SEG_H / 2f + 4.5f, _subTextPaint);
                _subTextPaint.Color = prev;
            }
            else
            {
                DrawSegmented(canvas, THEME_SEG_X, TITLE_BAR_HEIGHT + THEME_SEG_Y, THEME_SEG_W, SEG_H,
                    ["黑", "白", "系统"], Renderer.ThemeMode, _hoveredThemeIndex);
            }

            // 第二行：背景材质（独立选项，与主题配色互不影响）
            canvas.DrawText("背景材质", CONTENT_TEXT_X, themeY + MAT_TITLE_DY, _uiTextPaint);
            // 亚克力仍是实验特性：复用通用设置里 MSP 那枚「实验性」标签
            DrawInlineBadge(canvas, CONTENT_TEXT_X + _uiTextPaint.MeasureText("背景材质") + BADGE_GAP_X,
                themeY + MAT_TITLE_DY - TEXT_INK_MID_OFFSET, "实验性");
            canvas.DrawText("胶囊内背景材质", CONTENT_TEXT_X, themeY + MAT_SUB_DY, _subTextPaint);
            DrawSegmented(canvas, MAT_SEG_X, TITLE_BAR_HEIGHT + MAT_SEG_Y, MAT_SEG_W, SEG_H,
                ["实体", "亚克力", "液态玻璃"], Renderer.IslandLiquidGlass ? 2 : (Renderer.IslandAcrylic ? 1 : 0),
                _hoveredMaterialIndex);

            // 液态玻璃模式下整卡不画：玻璃的着色由「玻璃雾度」管，涂层恒为 0（见 ApplyThemeColors），
            // 这条滑轨摆在那儿只会让人以为能调。尺寸卡/玻璃卡跟着上移（见 SIZE_CARD_Y）。
            if (ShowOpacityCard)
            {
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

                // ⚠️ 这几笔必须自己把画笔状态摆正：_dynamicStrokePaint 是全局复用的，
                //    玻璃卡的滑轨也在改它的 StrokeWidth；一旦 Style/Cap 被别处动过，
                //    DrawLine 什么都不会画，画面上只剩 _separatorPaint 那条灰轨道（"进度条不显示"）。
                _dynamicStrokePaint.Style = SKPaintStyle.Stroke;
                _dynamicStrokePaint.StrokeCap = SKStrokeCap.Round;
                _dynamicStrokePaint.Color = new SKColor(0, 120, 212);
                _dynamicStrokePaint.StrokeWidth = 2f;
                canvas.DrawLine(sliderX, sliderY, activePx, sliderY, _dynamicStrokePaint);

                bool isOpacityDisabled = Renderer.PassthroughModeEnabled;
                // 禁用（穿透模式）也照样画出进度，只是压暗 —— 整条消失会让人以为设置没生效
                _dynamicStrokePaint.Color = isOpacityDisabled ? Neutral(80) : new SKColor(0, 120, 212);
                for (int i = 0; i < 5; i++)
                {
                    float px = sliderX + (sliderW / 4) * i;
                    bool isSelected = Renderer.BgOpacityLevel == i;
                    bool isHovered = _hoveredOpacityIndex == i;
                    if (isSelected || isHovered)
                    {
                        _dynamicFillPaint.Color = isSelected
                            ? (isOpacityDisabled ? Neutral(120) : new SKColor(0, 120, 212))
                            : Overlay(80);
                        canvas.DrawCircle(px, sliderY, isSelected ? 6 : 4, _dynamicFillPaint);
                    }
                    _dynamicTextPaint.Color = isOpacityDisabled ? Neutral(100) : (isSelected ? _fgColor : Neutral(150));
                    _dynamicTextPaint.TextSize = 11f;
                    string pct = OpacityStopLabels[i];   // 复用静态刻度文案，避免每帧插值
                    float tw = _dynamicTextPaint.MeasureText(pct);
                    canvas.DrawText(pct, px - tw / 2, sliderY + 18, _dynamicTextPaint);
                    _dynamicTextPaint.TextSize = 13f;
                }
            }
            DrawMultiCard(SIZE_CARD_Y, ["全局折叠态高度", "底部圆角", "消息通知弹出高度", "视觉比例"], [3, 7, 5, 6]);

            // 液态玻璃参数卡：只在材质选到「液态玻璃」时出现 —— 实体 / 亚克力下这些滑块没有任何意义
            if (Renderer.IslandLiquidGlass)
            {
                float gY = TITLE_BAR_HEIGHT + GLASS_CARD_Y;
                var gRect = new SKRect(CONTENT_L, gY, WIDTH - CONTENT_RM, gY + GLASS_CARD_H);
                canvas.DrawRoundRect(gRect, 6, 6, _cardBg);
                canvas.DrawRoundRect(gRect, 6, 6, _cardBorder);

                // ── 第一行：玻璃质量（左） + 恢复默认参数（右）──
                // 抓屏方式不再暴露：放大镜可用即用、不可用自动回退兼容抓屏，没有开关也没有状态行。
                float segY = gY + GLASS_SEG_Y;
                float segTextY = segY + 20f;
                canvas.DrawText("玻璃质量", GLASS_TEXT_X, segTextY, _uiTextPaint);
                DrawSegmented(canvas, GLASS_QUALITY_SEG_X, segY, GLASS_QUALITY_SEG_W, SEG_H,
                    ["流畅", "均衡", "精细"], LiquidGlass.Quality, _hoveredGlassQualityIndex);

                // 「恢复默认参数」：与质量分段同一行，右对齐到卡片文字右缘（原卡底位让给滑轨）
                float rY = segY + (SEG_H - GLASS_RESET_H) / 2f;
                _dynamicFillPaint.Color = _hoveredGlassReset ? Overlay(30) : Overlay(15);
                canvas.DrawRoundRect(new SKRect(GLASS_RESET_X, rY, GLASS_RESET_X + GLASS_RESET_W, rY + GLASS_RESET_H),
                    4, 4, _dynamicFillPaint);
                const string resetLabel = "恢复默认参数";
                float resetW = _uiTextPaint.MeasureText(resetLabel);
                canvas.DrawText(resetLabel, GLASS_RESET_X + (GLASS_RESET_W - resetW) / 2f, rY + 18, _uiTextPaint);

                // ── 12 根滑轨：3 列 × 4 行 ──
                for (int row = 0; row < GLASS_ROWS; row++)
                {
                    float rowY = gY + GLASS_ROW0_Y + row * GLASS_ROW_H;
                    for (int col = 0; col < 3; col++)
                        DrawGlassSlider(canvas, rowY, col, (int)(row * 3) + col);
                }
            }
        }

        // 液态玻璃的一根 5 档滑轨：标签 + 滑轨 + 档位值（数值型参数直接显示具体值）。
        private void DrawGlassSlider(SKCanvas canvas, float rowY, int col, int paramIndex)
        {
            if (paramIndex >= LiquidGlass.ParamCount) return;

            float colX = GLASS_COL_X + col * (GLASS_COL_W + GLASS_COL_GAP);
            float x0 = colX + GLASS_SLIDER_DX, x1 = x0 + GLASS_SLIDER_W;
            float textY = rowY + 17f;
            // 滑轨对齐的是文字的**视觉中线**，不是基线 —— 按基线摆会显得整条轨道往下掉
            float railY = textY - 4.5f;

            // 玻璃卡的字号比其他页大一号：12px 下"标准"这类两字值挤在一格里很难读
            _subTextPaint.TextSize = 13f;

            canvas.DrawText(LiquidGlass.ParamLabels[paramIndex], colX, textY, _subTextPaint);

            int level = Math.Clamp(LiquidGlass.GetLevel(paramIndex), 0, LiquidGlass.MaxLevel);
            bool hovered = _hoveredGlassParamIndex == paramIndex;
            int hoveredLevel = hovered ? _hoveredGlassParamLevel : -1;

            canvas.DrawLine(x0, railY, x1, railY, _separatorPaint);
            _dynamicStrokePaint.Color = new SKColor(0, 120, 212);
            _dynamicStrokePaint.StrokeWidth = 2f;
            canvas.DrawLine(x0, railY, x0 + (GLASS_SLIDER_W / LiquidGlass.MaxLevel) * level, railY, _dynamicStrokePaint);
            _dynamicStrokePaint.StrokeWidth = 1.5f;
            for (int i = 0; i <= LiquidGlass.MaxLevel; i++)
            {
                bool sel = level == i, hov = hoveredLevel == i;
                if (!sel && !hov) continue;
                float px = x0 + (GLASS_SLIDER_W / LiquidGlass.MaxLevel) * i;
                _dynamicFillPaint.Color = sel ? new SKColor(0, 120, 212) : Overlay(80);
                canvas.DrawCircle(px, railY, sel ? 5.5f : 4.5f, _dynamicFillPaint);
            }

            // 档位值右对齐：左对齐时三字值（"1.08x"）会扎进下一列的标签里。
            // 「刷新」档的 ms 单位挪到数字下方 —— 单行会被 max 档的滑块压住 "6"；
            // s 档（"1s"/"0.5s"）本来就是单行，照原样画。
            string valueText = LiquidGlass.ValueTexts(paramIndex)[level];
            float right = colX + GLASS_VALUE_RIGHT;
            if (valueText.EndsWith("ms", StringComparison.Ordinal))
            {
                string num = valueText.Substring(0, valueText.Length - 2);
                canvas.DrawText(num, right - _subTextPaint.MeasureText(num), textY, _subTextPaint);
                float prevSize = _subTextPaint.TextSize;
                _subTextPaint.TextSize = 10f;
                canvas.DrawText("ms", right - _subTextPaint.MeasureText("ms"), textY + 11f, _subTextPaint);
                _subTextPaint.TextSize = prevSize;
            }
            else
            {
                canvas.DrawText(valueText, right - _subTextPaint.MeasureText(valueText), textY, _subTextPaint);
            }

            _subTextPaint.TextSize = 12f;   // 恢复基准字号：同一支画笔后面还要用
        }

        private void RenderTabPlugins(SKCanvas canvas)
        {
            RefreshPluginView();

            // ── 顶部操作卡片 ──
            float topY = TITLE_BAR_HEIGHT + CARD_TOP_Y;
            var topRect = new SKRect(CONTENT_L, topY, WIDTH - CONTENT_RM, topY + PluginTopCardH);
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

            DrawPluginButton(0, "导入插件", CONTENT_TEXT_X, topY + 60, 96);
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
                canvas.DrawText("暂无插件，点击「导入插件」或到「插件市场」下载", CONTENT_TEXT_X, listY + 62, _subTextPaint);

            for (int slot = 0; slot < visibleRows; slot++)
            {
                int i = _pluginScroll + slot;
                var entry = _pluginView[i];
                float rowY = listY + PluginRowsTopPad + slot * PluginListRowH;
                if (slot > 0) canvas.DrawLine(CONTENT_TEXT_X, rowY - 5, WIDTH - CONTENT_TEXT_RM, rowY - 5, _separatorPaint);

                // ═══ 上行：插件名称（独占整行，无按钮遮挡） ═══
                canvas.DrawText(TruncateText(entry.FriendlyName, _uiTextPaint, nameTextMax), CONTENT_TEXT_X, rowY + 17, _uiTextPaint);

                string sub = i < _pluginSubTexts.Count ? _pluginSubTexts[i] : "";
                bool subDisabled = i < _pluginSubDisabled.Count && _pluginSubDisabled[i];
                float infoBaseline = rowY + 37;
                string shownSub = TruncateText(sub, _subTextPaint, infoTextMax);
                if (entry.State == PluginState.Failed && shownSub.StartsWith("加载失败", StringComparison.Ordinal))
                {
                    // 红字：这一行是弹窗关掉之后唯一还能说明「它没跑起来」的地方
                    const string tag = "加载失败";
                    _subTextPaint.Color = new SKColor(232, 100, 100);
                    canvas.DrawText(tag, CONTENT_TEXT_X, infoBaseline, _subTextPaint);
                    _subTextPaint.Color = Neutral(170);
                    canvas.DrawText(shownSub[tag.Length..], CONTENT_TEXT_X + _subTextPaint.MeasureText(tag), infoBaseline, _subTextPaint);
                }
                else if (subDisabled && shownSub.StartsWith("已禁用", StringComparison.Ordinal))
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

            if (visibleRows > 0 && maxFirstRow > 0)
            {
                float trackTop = listY + PluginRowsTopPad - 2f;
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

                const string dropHint = "松开鼠标以导入插件（DLL / ZIP）";
                float hintW = _uiTextPaint.MeasureText(dropHint);
                float hintX = zone.MidX - hintW / 2f;
                float hintY = zone.MidY + 5f;
                _dynamicFillPaint.Color = new SKColor(0, 90, 170, 235);
                canvas.DrawRoundRect(new SKRect(hintX - 14, hintY - 22, hintX + hintW + 14, hintY + 10), 6, 6, _dynamicFillPaint);
                _uiTextPaint.Color = SKColors.White;
                canvas.DrawText(dropHint, hintX, hintY, _uiTextPaint);
                _uiTextPaint.Color = _fgColor;
            }
        }

        private void RenderTabMarket(SKCanvas canvas)
        {
            EnsureMarketData();
            if (_marketDataDirty) { _marketDataDirty = false; RefreshMarketFilter(); }   // UI 线程重建过滤视图

            float cardTop = TITLE_BAR_HEIGHT + CARD_TOP_Y;
            var cardRect = new SKRect(CONTENT_L, cardTop, WIDTH - CONTENT_RM, HEIGHT - 20);
            canvas.DrawRoundRect(cardRect, 6, 6, _cardBg);
            canvas.DrawRoundRect(cardRect, 6, 6, _cardBorder);

            // 工具栏下拉框（分类 / 排序）与设置页共用同一套 DrawDropdownBox。
            var catRect = new SKRect(CONTENT_TEXT_X, MarketControlsY, CONTENT_TEXT_X + MarketCatBtnW, MarketControlsY + MarketControlH);
            DrawDropdownBox(canvas, catRect.Left, catRect.Top, MarketCatBtnW, MarketControlH,
                MarketCategoryName(_marketCategoryKey), _hoveredMarketCategoryIndex == -2,
                enabled: true, open: _marketCategoryOpen);

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

            // 只说总数，不追加「（已筛选）」：分类 / 搜索 / 「只看已安装」三个控件本身就在旁边，
            // 再挂一个后缀只会把这句话挤成省略号。
            string status = _marketFetching ? "正在加载插件市场…"
                : _marketError.Length > 0 ? _marketError
                : $"共 {_marketView.Count} 个插件";
            float statusX = MarketChkLabelX + _marketTextPaint.MeasureText(MarketChkLabel) + 18f;
            float statusMax = MarketSortX - 10f - statusX;   // 止于排序下拉左侧
            _marketTextPaint.Color = _marketError.Length > 0 && !_marketFetching ? new SKColor(232, 100, 100) : Neutral(140);
            canvas.DrawText(TruncateText(status, _marketTextPaint, statusMax), statusX, MarketStatusBaseline, _marketTextPaint);
            _marketTextPaint.Color = Neutral(140);
            if (!string.IsNullOrEmpty(_marketHint) && !_marketFetching)
            {
                float hintRight = MarketSortX - 10f;   // 提示右对齐到排序下拉左侧，别钻到控件底下
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

            // ── 排序下拉（最热 / 评分 / 最新 / 名称，官方插件始终置顶）──
            var sortRect = new SKRect(MarketSortX, MarketStatusRowY, MarketSortX + MarketSortW, MarketStatusRowY + MarketControlH);
            DrawDropdownBox(canvas, sortRect.Left, sortRect.Top, MarketSortW, MarketControlH,
                MarketSortOptions[_marketSortIndex], _hoveredMarketSortIndex == -2,
                enabled: true, open: _marketSortOpen);
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

                // 主操作（下载 / 更新 / 重装 / 启用）：四态四种颜色
                {
                    bool enabled = !busy;
                    bool hovered = _hoveredMarketInstall == i;
                    var r = new SKRect(MarketBtn1X, rowY + btnTop, MarketBtn1X + MarketBtnW, rowY + btnTop + btnH);
                    var (normal, hover) = InstallButtonColors(local, mp, dim);
                    _dynamicFillPaint.Color = !enabled ? Overlay(20) : hovered ? hover : normal;
                    canvas.DrawRoundRect(r, 4, 4, _dynamicFillPaint);
                    // 「启用」是灰底，白字压在浅色主题的浅灰底上会糊掉 —— 这类按钮的文字跟着底色走。
                    _uiTextPaint.Color = !enabled ? Neutral(110)
                        : (local != null && dim) ? Neutral(235) : SKColors.White;
                    DrawCenteredButtonLabel(canvas, MarketInstallLabel(mp, local, dim), r, _uiTextPaint);
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

            if (mVisibleRows > 0 && mMaxFirst > 0)
            {
                float trackTop = rowsTop - 2f;
                float trackH = mVisibleRows * PluginListRowH - 8f;
                DrawScrollBar(canvas, WIDTH - 26, trackTop, trackH,
                    mVisibleRows / (float)_marketView.Count, _marketScroll / (float)mMaxFirst);
            }

            // ── 一键更新全部：有可更新插件才出现（列表高度已在 GetMarketListLayout 让位）──
            if (MarketUpdateCount > 0)
            {
                var bar = MarketUpdateBarRect;
                bool running = _updateAllRunning;
                bool enabled = !running && _marketBusyId.Length == 0;
                // 主操作色跟行内「更新」按钮同一套橙：一眼看出这条是干什么的
                _dynamicFillPaint.Color = running ? Overlay(20)
                    : enabled && _hoveredMarketUpdateAll ? new SKColor(238, 146, 34)
                    : new SKColor(214, 126, 24);
                canvas.DrawRoundRect(bar, 5, 5, _dynamicFillPaint);
                _uiTextPaint.Color = running ? Neutral(200) : SKColors.White;
                DrawCenteredButtonLabel(canvas, running ? UpdateAllProgressText : $"一键更新全部插件（{MarketUpdateCount}）", bar, _uiTextPaint);
            }

            if (_marketCategoryOpen)
            {
                int catSelected = Array.FindIndex(MarketCategories, c =>
                    string.Equals(c.Key, _marketCategoryKey, StringComparison.OrdinalIgnoreCase));
                if (catSelected < 0) catSelected = 0;
                RenderDropdownList(canvas, CONTENT_TEXT_X, MarketControlsY + MarketControlH + 4, MarketCatBtnW,
                    MarketCategoryNames, catSelected, _hoveredMarketCategoryIndex, dimmedIndex: -1);
            }

            if (_marketSortOpen)
            {
                RenderDropdownList(canvas, MarketSortX, MarketStatusRowY + MarketControlH + 4, MarketSortW,
                    MarketSortOptions, _marketSortIndex, _hoveredMarketSortIndex, dimmedIndex: -1);
            }

            // 从通知点进来时市场数据可能刚落地：在本帧末尾打开详情，
            // 弹窗由 RenderPluginDialogs 紧接着绘制，不会闪一帧空列表。
            TryOpenPendingMarketDetail();
        }

        /// <summary>
        /// 弹窗绘制总入口。三分支＝三种弹窗，放在这里而不是各自的页签渲染里：
        /// 加载失败可能在任何页签发生（市场后台装完、插件页重载 / 启用），
        /// 只画在 6、7 就会出现「点击被模态拦掉、画面上什么都没弹」的假死。
        /// </summary>
        private void RenderPluginDialogs(SKCanvas canvas)
        {
            if (_marketDialog == MarketDialog.UninstallConfirm || _marketDialog == MarketDialog.UninstallData)
            {
                DrawUninstallDialog(canvas);
            }
            else if (_marketDialog == MarketDialog.LoadFailed)
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

        private static (SKColor Normal, SKColor Hover) InstallButtonColors(PluginEntry? local, MarketPlugin mp, bool disabled)
        {
            // 已装但没在跑：中性灰，和「下载绿 / 更新橙 / 重装蓝」区分开 —— 它不下载任何东西。
            if (local != null && disabled) return (Neutral(95), Neutral(115));
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

            // 右下角常驻安装按钮：没装 = 安装，已装 = 已安装（灰掉点击），禁用态 = 启用。
            // 三种状态都用绿色，和列表里的「下载」按钮同一套视觉。
            var local = MatchLocalPlugin(mp);
            bool disabled = local != null && PluginManager.Instance.IsDisabled(local);
            bool busy = string.Equals(_marketBusyId, mp.Id, StringComparison.Ordinal);
            bool actionable = local == null || disabled;
            string label = local == null ? (busy ? "安装中…" : "安装")
                : disabled ? "启用" : "已安装";

            var button = GetDialogButtonRect(rect, 0, 1);
            bool hovered = actionable && !busy && _hoveredDialogButton == 0;
            _dynamicFillPaint.Color = actionable
                ? (hovered ? DialogAccentGreen.Hover : DialogAccentGreen.Normal)
                : DialogAccentGreen.Normal;   // 已安装：还是绿色，只是不再响应悬停
            canvas.DrawRoundRect(button, 4, 4, _dynamicFillPaint);
            _uiTextPaint.Color = SKColors.White;
            DrawCenteredButtonLabel(canvas, label, button, _uiTextPaint);
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

        /// <summary>
        /// 弹窗按钮只有两种：主操作（彩色实底白字，accent 有值）与次操作（灰底浅字，accent 为 null）。
        /// 弹窗样式统一的要求全落在这里 —— 别在各自的 Draw 里再手搓颜色。
        /// </summary>
        private void DrawDialogButton(SKCanvas canvas, SKRect r, string label, int index, (SKColor Normal, SKColor Hover)? accent)
        {
            bool hovered = _hoveredDialogButton == index;
            _dynamicFillPaint.Color = accent is { } a
                ? (hovered ? a.Hover : a.Normal)
                : (hovered ? Overlay(30) : Overlay(15));
            canvas.DrawRoundRect(r, 4, 4, _dynamicFillPaint);
            _uiTextPaint.Color = accent == null ? Neutral(200) : SKColors.White;
            DrawCenteredButtonLabel(canvas, label, r, _uiTextPaint);
        }

        private static readonly (SKColor Normal, SKColor Hover) DialogAccentBlue = (new SKColor(0, 120, 212), new SKColor(0, 140, 240));
        private static readonly (SKColor Normal, SKColor Hover) DialogAccentRed = (new SKColor(180, 50, 50), new SKColor(200, 60, 60));
        private static readonly (SKColor Normal, SKColor Hover) DialogAccentGreen = (new SKColor(44, 160, 92), new SKColor(58, 184, 108));

        private void DrawLoadFailedDialog(SKCanvas canvas)
        {
            var rect = GetNoticeDialogRect();
            canvas.DrawRoundRect(rect, 8, 8, _menuBg);
            canvas.DrawRoundRect(rect, 8, 8, _menuBorder);

            float bodyY = DrawDialogHeader(canvas, rect, "插件加载失败") + LoadFailedBodyDY;
            float left = rect.Left + DialogPad;

            // 逐行逐段画：群号那一段单独上色 —— 折行位置是打开弹窗时算好的，
            // 这里只按结果摆，别再自己折一遍（否则链接的命中区会跟画面错开）。
            for (int i = 0; i < _loadFailedLines.Count; i++)
            {
                float lineY = bodyY + i * DialogLineH;
                float x = left;
                foreach (var (text, isLink) in _loadFailedLines[i])
                {
                    _subTextPaint.Color = isLink
                        ? (_loadFailedLinkHovered ? new SKColor(0, 140, 240) : new SKColor(0, 120, 212))
                        : Neutral(200);
                    canvas.DrawText(text, x, lineY, _subTextPaint);
                    x += _subTextPaint.MeasureText(text);
                }
            }

            // 悬停才出下划线：静态下划线容易被当成正文里的分隔符
            if (_loadFailedLinkHovered && _loadFailedLinkLine >= 0)
            {
                float y = bodyY + _loadFailedLinkLine * DialogLineH + 3f;
                float x0 = left + _loadFailedLinkOffsetX;
                _dynamicFillPaint.Color = new SKColor(0, 140, 240);
                canvas.DrawRect(new SKRect(x0, y, x0 + _loadFailedLinkWidth, y + 1f), _dynamicFillPaint);
            }

            _subTextPaint.Color = Neutral(170);
            DrawDialogButton(canvas, GetDialogButtonRect(rect, 0, 1), "好的", 0, DialogAccentBlue);
        }

        /// <summary>
        /// 卸载确认弹窗（两级）。
        /// 第一级只有一句「确定卸载吗」，第二级才把插件登记的数据目录摆出来 ——
        /// 没登记的插件根本走不到第二级，行为与以前一致，只是多了第一级的确认。
        /// </summary>
        private void DrawUninstallDialog(SKCanvas canvas)
        {
            bool askDir = _marketDialog == MarketDialog.UninstallData;
            var rect = askDir ? GetUninstallDirDialogRect() : GetUninstallConfirmDialogRect();
            canvas.DrawRoundRect(rect, 8, 8, _menuBg);
            canvas.DrawRoundRect(rect, 8, 8, _menuBorder);

            string name = _uninstallEntry?.FriendlyName ?? "";
            float bodyY = DrawDialogHeader(canvas, rect, askDir ? "删除插件目录" : "卸载插件");

            if (!askDir)
            {
                _subTextPaint.Color = Neutral(200);
                canvas.DrawText(TruncateText($"确定要卸载「{name}」吗？", _subTextPaint, DialogInnerW),
                    rect.Left + DialogPad, bodyY + 12, _subTextPaint);
                _subTextPaint.Color = Neutral(150);
                canvas.DrawText(TruncateText("插件文件会移入回收站，之后可以重新导入。", _subTextPaint, DialogInnerW),
                    rect.Left + DialogPad, bodyY + 12 + DialogLineH, _subTextPaint);
                _subTextPaint.Color = Neutral(170);

                DrawDialogButton(canvas, GetDialogButtonRect(rect, 0, 1), "卸载", 0, DialogAccentRed);
                return;
            }

            _subTextPaint.Color = Neutral(200);
            canvas.DrawText(TruncateText("这个插件登记了它创建的数据目录：", _subTextPaint, DialogInnerW),
                rect.Left + DialogPad, bodyY + 12, _subTextPaint);
            _subTextPaint.Color = Neutral(150);
            canvas.DrawText(TruncateText(_uninstallDir, _subTextPaint, DialogInnerW),
                rect.Left + DialogPad, bodyY + 12 + DialogLineH, _subTextPaint);
            canvas.DrawText(TruncateText("删除后无法恢复；点「保留」只卸载插件。", _subTextPaint, DialogInnerW),
                rect.Left + DialogPad, bodyY + 12 + DialogLineH * 2, _subTextPaint);
            _subTextPaint.Color = Neutral(170);

            DrawDialogButton(canvas, GetDialogButtonRect(rect, 0, 3), "打开目录", 0, null);
            DrawDialogButton(canvas, GetDialogButtonRect(rect, 1, 3), "保留", 1, null);
            DrawDialogButton(canvas, GetDialogButtonRect(rect, 2, 3), "删除", 2, DialogAccentRed);
        }

        // 统一下拉面板：圆角 8、悬停整行提亮、选中蓝字 + 左侧蓝条、内置项带蓝色胶囊，超高时内部滚动。
        // y 为面板顶端（upward 时是控件上沿，面板朝上展开）。
        private void RenderDropdownList(SKCanvas canvas, float x, float y, float w,
                                        string[] options, int selectedIndex, int hoveredIndex, int dimmedIndex,
                                        bool upward = false, int scrollFirst = 0, int visibleRowsOverride = 0,
                                        bool[]? builtinFlags = null, string? emptyText = null)
        {
            int total = options.Length;
            float availFrom = upward ? y - 2 : y;
            int maxRows = upward
                ? Math.Max(1, (int)((availFrom - TITLE_BAR_HEIGHT - 12) / DROPDOWN_ROW_H))
                : Math.Max(1, (int)((HEIGHT - 12 - availFrom) / DROPDOWN_ROW_H));

            int visible;
            if (total == 0)
            {
                if (emptyText == null) return;
                visible = 1;
            }
            else
            {
                visible = visibleRowsOverride > 0
                    ? Math.Min(total, visibleRowsOverride)
                    : Math.Min(total, maxRows);
            }

            float listH = visible * DROPDOWN_ROW_H;
            float dY = upward ? availFrom - listH : availFrom;
            var dRect = new SKRect(x, dY, x + w, dY + listH);
            canvas.DrawRoundRect(dRect, 8, 8, _menuBg);
            canvas.DrawRoundRect(dRect, 8, 8, _menuBorder);

            if (total == 0)
            {
                _dynamicTextPaint.Color = Neutral(150);
                canvas.DrawText(emptyText!, x + 12, dY + 18, _dynamicTextPaint);
                _dynamicTextPaint.Color = _fgColor;
                return;
            }

            int maxFirst = Math.Max(0, total - visible);
            int first = Math.Clamp(scrollFirst, 0, maxFirst);

            for (int row = 0; row < visible; row++)
            {
                int i = first + row;
                float rowTop = dY + row * DROPDOWN_ROW_H;
                bool selected = i == selectedIndex;

                var rowRect = new SKRect(x + 4, rowTop + 1, x + w - 4, rowTop + DROPDOWN_ROW_H - 1);
                if (hoveredIndex == i) canvas.DrawRoundRect(rowRect, 5, 5, _tabBgSelected);
                else if (selected)
                {
                    _dynamicFillPaint.Color = Overlay(10);
                    canvas.DrawRoundRect(rowRect, 5, 5, _dynamicFillPaint);
                }

                // 文字统一留出左侧蓝条的位置，选中与否都不左右跳
                float textX = x + 18f;
                if (selected)
                {
                    _dynamicFillPaint.Color = new SKColor(0, 120, 212);
                    canvas.DrawRoundRect(new SKRect(x + 6f, rowTop + 7f, x + 8.5f, rowTop + DROPDOWN_ROW_H - 7f),
                        1.5f, 1.5f, _dynamicFillPaint);
                }

                bool builtin = builtinFlags != null && i < builtinFlags.Length && builtinFlags[i];
                float tagW = builtin ? _subTextPaint.MeasureText("内置") + 14f : 0f;
                float nameMaxW = x + w - 12f - textX - (tagW > 0f ? tagW + 8f : 0f);
                string shown = TruncateText(options[i], _dynamicTextPaint, nameMaxW);
                float nameW = _dynamicTextPaint.MeasureText(shown);
                _dynamicTextPaint.Color = selected ? new SKColor(0, 120, 212)
                    : (i == dimmedIndex ? Neutral(130) : _fgColor);
                canvas.DrawText(shown, textX, rowTop + 18f, _dynamicTextPaint);
                _dynamicTextPaint.Color = _fgColor;

                if (builtin)
                    DrawBuiltinTag(canvas, Math.Min(textX + nameW + 8f, x + w - 12f - tagW), rowTop + 5f);
            }

            if (total > visible)
            {
                // 滚动条贴着面板内沿，避免溢出到菜单主体之外
                DrawScrollBar(canvas, x + w - 7f, dY + 4f, listH - 8f,
                    visible / (float)total, first / (float)Math.Max(1, maxFirst));
            }
        }

        // 待机显示内容下拉面板：空白置顶，内置项带蓝色「内置」胶囊，插件按名称排序；
        // 选中项用蓝字 + 左侧蓝条，悬停整行上底色，条目超长时列表内部滚动。
        private void RenderSceneDropdownList(SKCanvas canvas)
        {
            if (_sceneOptionLabels.Length == 0) return;

            GetSceneMenuLayout(out float menuTop, out int visible, out _);
            int selected = Array.FindIndex(_sceneChoices, c =>
                string.Equals(c.Key, Renderer.StandbySceneKey, StringComparison.OrdinalIgnoreCase));
            RenderDropdownList(canvas, SCENE_SEG_X, menuTop, SCENE_SEG_W,
                _sceneOptionLabels, selected, _hoveredSceneIndex, dimmedIndex: -1,
                scrollFirst: _sceneScroll, visibleRowsOverride: visible, builtinFlags: _sceneBuiltinFlags);
        }

        private void RenderDropdowns(SKCanvas canvas)
        {
            if (_selectedTab == 2 && _dropdownOpen)
            {
                RenderDropdownList(canvas, WIDTH - 140, TITLE_BAR_HEIGHT + 130, 110,
                    _platformNames, _selectedPlatformIndex, _hoveredDropdownIndex, dimmedIndex: -1);
            }

            // 匹配方式下拉菜单（左框）
            if (_selectedTab == 2 && _matchModeDropdownOpen)
            {
                int selectedMode = MediaController.IsManualSessionMatch ? 1 : 0;
                RenderDropdownList(canvas, MATCH_MODE_X, MATCH_MENU_Y, MATCH_BOX_W,
                    _matchModeOptions, selectedMode, _hoveredMatchModeIndex, dimmedIndex: -1);
            }

            if (_selectedTab == 2 && _appDropdownOpen)
            {
                int appRows = Math.Clamp(_appOptions.Length, 1, 8);
                float appMenuX = MATCH_MENU_RIGHT - APP_MENU_W;
                int appSelected = Array.FindIndex(_appOptions, a =>
                    string.Equals(a, MediaController.ManualSessionAppId, StringComparison.OrdinalIgnoreCase));
                RenderDropdownList(canvas, appMenuX, MATCH_MENU_Y, APP_MENU_W,
                    _appOptions, appSelected, _hoveredAppIndex, dimmedIndex: -1,
                    visibleRowsOverride: appRows, emptyText: "暂无活动会话");
            }

            // 消息通知内容下拉菜单（通用设置）
            if (_selectedTab == 0 && _toastModeDropdownOpen)
            {
                RenderDropdownList(canvas, TOAST_MODE_CTRL_X, TITLE_BAR_HEIGHT + TOAST_MODE_ROW_Y + TOAST_MODE_ROW_H + 2, TOAST_MODE_CTRL_W,
                    _toastModeOptions, _selectedToastModeIndex, _hoveredToastModeIndex, dimmedIndex: -1);
            }

            if (_selectedTab == 0 && _toastSoundDropdownOpen)
            {
                GetToastSoundMenuLayout(out float soundMenuTop, out int soundVisible, out _);
                RenderDropdownList(canvas, SOUND_CTRL_X, soundMenuTop, SOUND_CTRL_W,
                    ToastSoundConfig.BuildOptionLabels(), ToastSoundConfig.SelectedIndex, _hoveredToastSoundIndex,
                    dimmedIndex: ToastSoundConfig.IsUsableFile(ToastSoundConfig.CustomPath, out _)
                        ? -1 : ToastSoundConfig.CustomIndex,
                    scrollFirst: _dropdownScroll, visibleRowsOverride: soundVisible);
            }

            //    所以向上展开 —— 向下展开会一路拖出窗口。
            if (_selectedTab == 0 && _soundVolumeDropdownOpen)
            {
                GetVolumeMenuLayout(out _, out _, out int volVisible);
                RenderDropdownList(canvas, SOUND_VOL_X, TITLE_BAR_HEIGHT + SOUND_BOX_Y, SOUND_VOL_W,
                    VolumeOptionLabels, ToastSoundConfig.VolumeIndex, _hoveredSoundVolumeIndex, dimmedIndex: -1,
                    upward: true, scrollFirst: 0, visibleRowsOverride: volVisible);
            }

            if (_selectedTab == 1 && _monitorDropdownOpen)
            {
                float dX = DISPLAY_CTRL_RIGHT - MONITOR_DD_W;
                float dY = TITLE_BAR_HEIGHT + MONITOR_ROW_Y + (ROW_H - MONITOR_DD_H) / 2f + MONITOR_DD_H + 2f - _displayPageScroll;
                RenderDropdownList(canvas, dX, dY, MONITOR_DD_W, _monitorOptions,
                    Renderer.TargetMonitorIndex, _hoveredMonitorDropdownIndex, dimmedIndex: -1);
            }

            // 待机显示内容下拉（显示设置）：放最后画，要盖住下面的切换行和显示内容列表
            if (_selectedTab == 1 && _sceneDropdownOpen) RenderSceneDropdownList(canvas);
        }
    }
}
