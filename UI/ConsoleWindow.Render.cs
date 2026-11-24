using SkiaSharp;
using NotchPeninsula.Plugins;

namespace NotchPeninsula
{
    public partial class ConsoleWindow
    {
        // ----  ----
        // ----  ----

        // 侧栏「插件市场」右上角的更新数角标：黄底白字小胶囊，>9 显示 9+。
        // 纯展示，不单独吃点击（整行本来就是页签热区）。
        private void DrawMarketTabBadge(SKCanvas canvas, float rowTop)
        {
            string text = MarketUpdateCount > 9 ? "9+" : MarketUpdateCount.ToString();
            const float badgeH = 13f;
            _dynamicTextPaint.TextSize = 9.5f;
            float tw = _dynamicTextPaint.MeasureText(text);
            float w = Math.Max(tw + 8f, badgeH);
            float right = TabButtonRight - (SidebarCollapsed ? 6f : 12f);
            var r = new SKRect(right - w, rowTop + 4f, right, rowTop + 4f + badgeH);

            _dynamicFillPaint.Color = new SKColor(232, 168, 48);
            canvas.DrawRoundRect(r, badgeH / 2f, badgeH / 2f, _dynamicFillPaint);

            _dynamicTextPaint.Color = SKColors.White;
            _dynamicTextPaint.TextAlign = SKTextAlign.Center;
            canvas.DrawText(text, r.MidX, r.MidY + _dynamicTextPaint.TextSize * 0.35f, _dynamicTextPaint);

            // 共用画笔复位：侧栏后面还有别的文字要画
            _dynamicTextPaint.TextAlign = SKTextAlign.Left;
            _dynamicTextPaint.TextSize = 13f;
            _dynamicTextPaint.Color = _fgColor;
        }

        // 选中背景滑到新页签，展开侧栏时同时绘制蓝色竖条。
        // 急出缓入位移（先快后慢）。**只有蓝条做「拉丝」**：背景块保持 36 高，方块跟着拉长反而显得不跟手。
        private void DrawTabSelection(SKCanvas canvas)
        {
            float p = TabEase(_tabSlideT);
            float d = _tabSlideToY - _tabSlideFromY;
            float top = _tabSlideFromY + d * p;

            canvas.DrawRoundRect(new SKRect(TabButtonLeft, TITLE_BAR_HEIGHT + top, TabButtonRight, TITLE_BAR_HEIGHT + top + TAB_ROW_H), 4, 4, _tabBgSelected);
            if (SidebarCollapsed)
                return;

            // 蓝条以自身中线为中心对称外扩：中点最强、两端归零，所以起止两端没有跳变。
            // 幅度固定、与滑动距离无关，最大伸展时 20 + 10 = 30 仍稳稳落在 36 高的方块里。
            float stretch = TAB_SLIDE_STRETCH * (float)Math.Sin(Math.PI * Math.Sqrt(Math.Clamp(_tabSlideT, 0f, 1f)));
            float mid = top + TAB_ROW_H * 0.5f;
            float half = (TAB_ROW_H * 0.5f - TAB_BAR_DY) + stretch * 0.5f;

            canvas.DrawRoundRect(new SKRect(TabButtonLeft, TITLE_BAR_HEIGHT + mid - half, TabButtonLeft + 3, TITLE_BAR_HEIGHT + mid + half),
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
            else if (_tabSlideT >= 1f)
            {
                // 页签没换、但整列位置变了（某个页签被收起 / 出现，后面的整体上移）：
                // 已经停稳的选中块直接跟着走，别让它留在旧位置。
                float targetY = TabRowY(_selectedTab);
                if (Math.Abs(_tabSlideToY - targetY) > 0.01f)
                    _tabSlideFromY = _tabSlideToY = targetY;
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
        private void DrawToggleCard(SKCanvas canvas, float yOffset, string title, string sub, bool state, bool hovered, bool disabled = false, string? badge = null, bool rightColumn = false, bool leftColumn = false)
        {
            float left = rightColumn ? RightColumnLeft : CONTENT_L;
            float right = leftColumn ? LeftColumnRight : WIDTH - CONTENT_RM;
            var cardRect = new SKRect(left, TITLE_BAR_HEIGHT + yOffset, right, TITLE_BAR_HEIGHT + yOffset + TOGGLE_CARD_H);
            DrawSettingsCard(canvas, cardRect);
            DrawToggleRow(canvas, yOffset, title, sub, state, hovered, disabled,
                cardRight: right, textLeft: left + 16f);

            // 徽标跟在标题文字后面（不是钉在卡片角上）：标题改字数时自己跟着挪，
            // 纵向按全页统一的「墨迹中线」对齐，和开关同一套锚定。
            if (!string.IsNullOrEmpty(badge))
            {
                float titleBaseline = TITLE_BAR_HEIGHT + yOffset
                    + (sub.Length > 0 ? ROW_TEXT_BASELINE : ROW_TEXT_BASELINE_SINGLE);
                float badgeLeft = left + 16f + _uiTextPaint.MeasureText(title) + BADGE_GAP_X;
                DrawInlineBadge(canvas, badgeLeft, titleBaseline - TEXT_INK_MID_OFFSET, badge!);
            }
        }

        // 设置卡片内的中性状态标签。
        // 不用红色 —— 红在这套界面里是「错误 / 失效」的语义，实验性是「留意一下」。
        private void DrawInlineBadge(SKCanvas canvas, float left, float centerY, string text)
        {
            float w = _badgeTextPaint.MeasureText(text) + BADGE_PAD_X * 2f;
            float top = centerY - BADGE_H / 2f;
            var rect = new SKRect(left, top, left + w, top + BADGE_H);

            _badgeBgPaint.Color = Overlay(17);
            canvas.DrawRoundRect(rect, BADGE_RADIUS, BADGE_RADIUS, _badgeBgPaint);

            _badgeTextPaint.Color = SecondaryText;
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

            _dynamicStrokePaint.Color = hovered ? AccentText : Neutral(150);
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

        // 页签 8：实况窗管理（侧边栏「显示设置」下面一项）
        // 媒体只做提示（开关在「媒体设置」）；插件逐个开关，默认全部启用；关掉后插件内容只留在主岛。
        private void RenderTabLiveWindows(SKCanvas canvas)
        {
            var owners = LiveWindowHost.KnownPluginOwners;

            float introTop = TITLE_BAR_HEIGHT + CARD_TOP_Y;
            var introRect = new SKRect(CONTENT_L, introTop, LeftColumnRight, introTop + LW_INTRO_CARD_H);
            DrawSettingsCard(canvas, introRect);
            canvas.DrawText("实况窗管理", CONTENT_TEXT_X, introTop + 26, _uiTextPaint);
            canvas.DrawText(TruncateText("控制哪些插件可以使用左侧实况窗；关闭后内容只留在主岛。", _subTextPaint, LeftColumnRight - CONTENT_TEXT_X - 16),
                CONTENT_TEXT_X, introTop + 48, _subTextPaint);

            // ── 媒体：状态跟随「媒体设置」，这里只给蓝字入口 ──
            float mediaTop = LiveWindowMediaCardTop;
            var mediaRect = new SKRect(CONTENT_L, mediaTop, LeftColumnRight, mediaTop + LW_MEDIA_CARD_H);
            DrawSettingsCard(canvas, mediaRect);
            if (_liveWindowMediaHovered)
            {
                _dynamicFillPaint.Color = Overlay(12);
                canvas.DrawRoundRect(mediaRect, 6, 6, _dynamicFillPaint);
            }
            canvas.DrawText("媒体", CONTENT_TEXT_X, mediaTop + 25, _uiTextPaint);
            canvas.DrawText(MediaLiveWindowController.IsEnabled ? "已启用（跟随「媒体设置」）" : "默认关闭（在「媒体设置」里开启）",
                CONTENT_TEXT_X, mediaTop + 44, _subTextPaint);
            canvas.DrawText("待机模式不支持实况窗：媒体会留在岛内显示。",
                CONTENT_TEXT_X, mediaTop + 62, _subTextPaint);

            string hint = "去「媒体设置」修改";
            float hintW = _subTextPaint.MeasureText(hint);
            _subTextPaint.Color = AccentText;
            canvas.DrawText(hint, LeftColumnRight - 16f - hintW, mediaTop + 25, _subTextPaint);
            _subTextPaint.Color = SecondaryText;

            // ── 插件：逐个开关 ──
            int visible = Math.Min(owners.Count, LiveWindowVisibleRows);
            _liveWindowScroll = Math.Clamp(_liveWindowScroll, 0, owners.Count - visible);
            float cardTop = LiveWindowPluginCardTop;
            var cardRect = new SKRect(RightColumnLeft, cardTop, WIDTH - CONTENT_RM, GetLiveWindowCardBottom(visible));
            DrawSettingsCard(canvas, cardRect);
            canvas.DrawText("插件", RightColumnText, cardTop + 23, _uiTextPaint);

            if (owners.Count == 0)
            {
                canvas.DrawText("暂无插件上报实况窗", RightColumnText, LiveWindowPluginRowTop + 25, _subTextPaint);
                return;
            }

            float tW = 42f, tH = TOGGLE_TRACK_H;
            for (int row = 0; row < visible; row++)
            {
                int i = _liveWindowScroll + row;
                float rowY = LiveWindowPluginRowTop + row * LW_ROW_H;
                bool hovered = _liveWindowHoverRow == i;
                if (hovered)
                {
                    _dynamicFillPaint.Color = Overlay(12);
                    canvas.DrawRoundRect(new SKRect(RightColumnLeft + 4f, rowY + 2f, WIDTH - CONTENT_RM - 4f, rowY + LW_ROW_H - 2f),
                        5f, 5f, _dynamicFillPaint);
                }
                float baseline = rowY + LW_ROW_H / 2f + 4.5f;
                string name = PluginManager.Instance.Host.GetPluginDisplayName(owners[i]);
                float maxNameW = WIDTH - CONTENT_RM - 16f - tW - 14f - RightColumnText;
                canvas.DrawText(TruncateText(name, _uiTextPaint, maxNameW), RightColumnText, baseline, _uiTextPaint);

                bool on = LiveWindowHost.IsOwnerEnabled(owners[i]);
                float tX = WIDTH - CONTENT_RM - 16f - tW;
                float tY = rowY + (LW_ROW_H - tH) / 2f;
                var tRect = new SKRect(tX, tY, tX + tW, tY + tH);
                DrawSettingsSwitch(canvas, tRect, on, hovered);
            }
        }

        // rowHovered：整行悬停（可选）。不传时退回「开关本体悬停」—— 否则调用方只传 rightHint
        // 会得到一个永远不显示的提示（参数默认 false 是个坑），这里用 null 表示「没单独跟踪，用 hovered」。
        private void DrawToggleRow(SKCanvas canvas, float yOffset, string title, string sub, bool state, bool hovered, bool disabled = false, string rightHint = "", bool? rowHovered = null, float? cardRight = null, float? textLeft = null)
        {
            float labelX = textLeft ?? CONTENT_TEXT_X;
            float titleBaseline = TITLE_BAR_HEIGHT + yOffset
                + (sub.Length > 0 ? ROW_TEXT_BASELINE : ROW_TEXT_BASELINE_SINGLE);

            _uiTextPaint.Color = disabled ? Neutral(100) : _fgColor;
            canvas.DrawText(title, labelX, titleBaseline, _uiTextPaint);
            _uiTextPaint.Color = _fgColor;

            if (sub.Length > 0)
            {
                _subTextPaint.Color = disabled ? Neutral(80) : SecondaryText;
                string description = cardRight.HasValue
                    ? TruncateText(sub, _subTextPaint, cardRight.Value - labelX - 70)
                    : sub;
                canvas.DrawText(description, labelX, titleBaseline + ROW_SUB_OFFSET, _subTextPaint);
                _subTextPaint.Color = SecondaryText;
            }

            float tW = 42; float tH = TOGGLE_TRACK_H; float tX = (cardRight ?? WIDTH - CONTENT_RM) - 16 - tW;
            float tY = TITLE_BAR_HEIGHT + yOffset + ROW_ANCHOR_Y - tH / 2f;
            var tRect = new SKRect(tX, tY, tX + tW, tY + tH);

            DrawSettingsSwitch(canvas, tRect, state, hovered, !disabled);

            // 右侧蓝字提示：塞在标题与开关之间的空档里；悬浮才出现。
            // 没单独跟踪整行悬停时用开关悬停兜底，避免「传了 rightHint 却永远不显示」。
            if (rightHint.Length > 0 && (rowHovered ?? hovered))
            {
                var prevColor = _subTextPaint.Color;
                float prevSize = _subTextPaint.TextSize;
                _subTextPaint.TextSize = 12f;
                _subTextPaint.Color = disabled ? Neutral(90) : AccentText;
                float hintRight = tX - 6f;
                canvas.DrawText(rightHint, hintRight - _subTextPaint.MeasureText(rightHint), titleBaseline, _subTextPaint);
                _subTextPaint.TextSize = prevSize;
                _subTextPaint.Color = prevColor;
            }
        }

        // 分段控件的几何唯一来源：绘制与命中都从这里取，别再各算一遍。
        // fitLabels = true 时各格仍然**等宽**（整条平分），只把字号按最长标签缩到放得下的那一档。
        // 以前按实测文字宽分格，同一排会宽窄不一（「默认 / DXGI / GDI（兼容）」选中块一大一小），
        // 看着像没对齐；等分 + 缩字号既整齐，也不会把长标签压到邻格。
        private void LayoutSegments(string[] labels, float w, bool fitLabels,
            float[] lefts, float[] widths, out float textScale)
        {
            int n = labels.Length;
            textScale = 1f;
            if (n == 0) return;

            float segW = w / n;
            for (int i = 0; i < n; i++)
            {
                lefts[i] = i * segW;
                widths[i] = segW;
            }

            if (!fitLabels) return;

            float maxLabel = 0f;
            for (int i = 0; i < n; i++) maxLabel = MathF.Max(maxLabel, _uiTextPaint.MeasureText(labels[i]));
            float usable = segW - 2f * SEG_TEXT_PAD_X;

            // 每格放不下最长标签时才缩字号兜底（下限 9px，与旧逻辑一致）
            if (maxLabel > 0f && usable > 0f && maxLabel > usable)
            {
                float baseSize = MathF.Max(_uiTextPaint.TextSize, 1f);
                textScale = MathF.Max(9f / baseSize, usable / maxLabel);
            }
        }

        // fitLabels 时每格文字左右的最小呼吸（不含选中块 2px 内缩）
        private const float SEG_TEXT_PAD_X = 6f;

        // 与 DrawSegmented 同一份几何反查命中格：看着在这一格、点着是那一格，就是这里算岔了
        private int SegmentIndexAt(string[] labels, float w, bool fitLabels, float dx)
        {
            int n = labels.Length;
            if (n == 0 || dx < 0f || dx > w) return -1;

            var lefts = new float[n];
            var widths = new float[n];
            LayoutSegments(labels, w, fitLabels, lefts, widths, out _);

            for (int i = 0; i < n; i++)
                if (dx < lefts[i] + widths[i]) return i;
            return n - 1;
        }

        private void DrawSegmented(SKCanvas canvas, float x, float y, float w, float h,
            string[] labels, int selected, int hovered, bool disabled = false, bool fitLabels = false)
        {
            int n = labels.Length;
            if (n == 0) return;

            var lefts = new float[n];
            var widths = new float[n];
            LayoutSegments(labels, w, fitLabels, lefts, widths, out float textScale);

            float baseSize = _uiTextPaint.TextSize;
            if (textScale < 1f) _uiTextPaint.TextSize = MathF.Max(9f, baseSize * textScale);

            for (int i = 0; i < n; i++)
            {
                var rect = new SKRect(x + lefts[i] + 2f, y, x + lefts[i] + widths[i] - 2f, y + h);
                DrawSettingsButton(canvas, rect, labels[i], i == hovered, !disabled, selected: i == selected);
            }
            _uiTextPaint.TextSize = baseSize;   // 恢复基准字号：同一支画笔本帧后面还要用
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

        private void DrawDragHandle(SKCanvas canvas, float slotX, float cy, bool hovered, bool enabled)
        {
            var stroke = !enabled ? _sortArrowDisabledStroke
                : hovered ? _sortArrowHoverStroke
                : _sortArrowStroke;

            if (hovered && enabled)
            {
                _dynamicFillPaint.Color = Overlay(26);
                canvas.DrawRoundRect(new SKRect(slotX, cy - 11f, slotX + SORT_TRI_W, cy + 11f), 5, 5, _dynamicFillPaint);
            }

            // 三条横线（≡）：和上下箭头同一套线宽 / 颜色，一眼看出是「抓手」。
            float cx = slotX + SORT_TRI_W / 2f;
            for (int i = -1; i <= 1; i++)
                canvas.DrawLine(cx - 4f, cy + i * 4f, cx + 4f, cy + i * 4f, stroke);
        }

        private void DrawDisplayItem(SKCanvas canvas, float y, string name, bool isBuiltin, bool isShown, float hoverP,
            bool enabled, bool hoverUp, bool hoverDown, bool hoverDrag, bool dragging, bool canUp, bool canDown)
        {
            if (dragging)
            {
                // 拖动中的行：抬升一层底，和普通悬停区分开
                _dynamicFillPaint.Color = Overlay(48);
                canvas.DrawRoundRect(new SKRect(DISPLAY_ITEM_L, y, DISPLAY_ITEM_R, y + DISPLAY_ITEM_H), 6, 6, _dynamicFillPaint);
            }
            else if (enabled && hoverP > 0.01f)
            {
                _dynamicFillPaint.Color = Overlay((byte)(28 * hoverP));
                canvas.DrawRoundRect(new SKRect(DISPLAY_ITEM_L, y, DISPLAY_ITEM_R, y + DISPLAY_ITEM_H), 6, 6, _dynamicFillPaint);
            }

            const float boxS = 16f;
            float boxX = DISPLAY_ITEM_L + 10f;
            float boxY = y + (DISPLAY_ITEM_H - boxS) / 2f;
            var box = new SKRect(boxX, boxY, boxX + boxS, boxY + boxS);
            DrawSettingsCheckBox(canvas, box, isShown, hoverP > 0.5f, enabled);

            float tagW = isBuiltin ? _subTextPaint.MeasureText("内置") + 14f : 0f;
            float textX = box.Right + 10f;
            string shownName = TruncateText(name, _uiTextPaint, DISPLAY_DRAG_X - 12f - textX - tagW);
            _uiTextPaint.Color = !enabled ? Neutral(100) : isShown ? _fgColor : SecondaryText;
            canvas.DrawText(shownName, textX, y + 20.5f, _uiTextPaint);
            _uiTextPaint.Color = _fgColor;

            if (isBuiltin)
                DrawBuiltinTag(canvas, textX + _uiTextPaint.MeasureText(shownName) + 8f, y + 7f, enabled);

            float cy = y + DISPLAY_ITEM_H / 2f;
            DrawDragHandle(canvas, DISPLAY_DRAG_X, cy, hoverDrag || dragging, enabled);
            DrawMoveArrow(canvas, DISPLAY_MOVE_UP_X, cy, hoverUp, enabled && canUp, true);
            DrawMoveArrow(canvas, DISPLAY_MOVE_DOWN_X, cy, hoverDown, enabled && canDown, false);
        }


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

            // 记下这一帧画没画静默页那一行：静默状态变了要主动补一帧（见 OnStandbyActiveChanged）。
            _sidebarSilentTabShown = UsesSilentMode;

            foreach (int tab in SidebarOrder)
            {
                if (!IsSidebarTabVisible(tab)) continue;
                float top = TITLE_BAR_HEIGHT + TabRowY(tab);
                if (tab == _hoveredTab && tab != _selectedTab)
                {
                    _dynamicFillPaint.Color = Overlay(14);
                    canvas.DrawRoundRect(new SKRect(TabButtonLeft, top, TabButtonRight, top + TAB_ROW_H), 4, 4, _dynamicFillPaint);
                }
                DrawSidebarIcon(canvas, SidebarIcons[tab], SidebarIconCenterX, top + TAB_ROW_H / 2);
                if (!SidebarCollapsed)
                    canvas.DrawText(SidebarLabels[tab], 54, top + 24, _uiTextPaint);
                if (tab == 7 && MarketUpdateCount > 0)
                    DrawMarketTabBadge(canvas, top);
            }

            DrawExportBundleButton(canvas);
        }

        // 侧边栏左下角：清理解包缓存 / 导出诊断包（都常驻，不绑页签）。
        // 导出诊断包是用户报 bug 时唯一要按的地方 —— 一个 zip 到手，版本 / 系统 / 显示器 /
        // 配置快照 / 插件清单 / 最近日志全在里头，省掉一轮轮追问。
        private void DrawExportBundleButton(SKCanvas canvas)
        {
            DrawSideToolButton(canvas, CACHE_BTN_X, CACHE_BTN_Y, CACHE_BTN_W, CACHE_BTN_H,
                _cacheCleaning ? "清理中…" : "清理解包缓存", _cacheCleanHovered, 12f);
            DrawSideToolButton(canvas, EXPORT_BTN_X, EXPORT_BTN_Y, EXPORT_BTN_W, EXPORT_BTN_H,
                _aboutExporting ? "正在打包…" : "导出诊断包", _aboutExportHovered, 12.5f);

            // 结果行画在按钮组上方：两个按钮共用一行提示（一次只会有一个人说话）。
            // 提示行比按钮宽（可占满侧栏），左对齐 —— 居中放不下时两边会顶到侧栏外。
            string hint = _cacheCleanHint.Length > 0 ? _cacheCleanHint : _aboutExportHint;
            if (!SidebarCollapsed && hint.Length > 0)
            {
                _dynamicTextPaint.TextSize = 11f;
                _dynamicTextPaint.TextAlign = SKTextAlign.Left;
                _dynamicTextPaint.Color = _cacheCleanHint.Length > 0
                    ? (_cacheCleanFailed ? new SKColor(200, 80, 80) : new SKColor(110, 180, 130))
                    : new SKColor(200, 80, 80);
                canvas.DrawText(TruncateText(hint, _dynamicTextPaint, TabButtonRight - EXPORT_BTN_X),
                    EXPORT_BTN_X, CACHE_BTN_Y - 8f, _dynamicTextPaint);
            }

            // 共享画笔复位：后面的页签内容默认按左对齐排版
            _dynamicTextPaint.TextAlign = SKTextAlign.Left;
            _dynamicTextPaint.TextSize = 13f;
        }

        private void DrawSideToolButton(SKCanvas canvas, float x, float y, float w, float h,
                                        string label, bool hovered, float textSize)
        {
            var rect = new SKRect(x, y, x + w, y + h);

            _dynamicFillPaint.Color = hovered ? Overlay(30) : Overlay(15);
            canvas.DrawRoundRect(rect, SettingsButtonRadius, SettingsButtonRadius, _dynamicFillPaint);

            _dynamicTextPaint.TextAlign = SKTextAlign.Center;
            if (SidebarCollapsed)
            {
                DrawSidebarIcon(canvas, y == CACHE_BTN_Y ? "\uE74D" : "\uE898", rect.MidX, rect.MidY);
                return;
            }

            _dynamicTextPaint.TextSize = textSize;
            _dynamicTextPaint.Color = hovered ? new SKColor(0, 140, 240) : Neutral(200);
            canvas.DrawText(label, x + w / 2f, y + 18.5f, _dynamicTextPaint);
            _dynamicTextPaint.TextAlign = SKTextAlign.Left;
        }

        // 页签：通用设置
        private void RenderTabGeneral(SKCanvas canvas)
        {
            DrawToggleCard(canvas, CARD_TOP_Y, "开机自启", "跟随系统启动自动运行该程序", _isAutoStartEnabled, _toggleHovered, leftColumn: true);
            // 灵动岛置顶（与下方消息通知整组互换位置）
            DrawToggleCard(canvas, TOPMOST_CARD_Y, "灵动岛置顶", "开启后刘海将始终保持在其他窗口最上层", NotchWindow.IsTopmostEnabled, _topmostToggleHovered, leftColumn: true);
            //    分隔线 +TOAST_SEP_Y (222)
            //    分隔线 +SOUND_SEP_Y (284)
            var notifyCardRect = new SKRect(RightColumnLeft, TITLE_BAR_HEIGHT + TOAST_ROW1_Y, WIDTH - CONTENT_RM, TITLE_BAR_HEIGHT + TOAST_CARD_BOTTOM);
            DrawSettingsCard(canvas, notifyCardRect);

            // ── 行 1：总开关 ──
            DrawToggleRow(canvas, TOAST_ROW1_Y, "系统消息通知", "允许在刘海中显示Windows系统的Toast消息", NotchWindow.IsToastEnabled, _toastToggleHovered, textLeft: RightColumnText);

            canvas.DrawLine(RightColumnText, TITLE_BAR_HEIGHT + TOAST_SEP_Y, WIDTH - CONTENT_TEXT_RM, TITLE_BAR_HEIGHT + TOAST_SEP_Y, _separatorPaint);

            canvas.DrawText("消息通知内容", RightColumnText, TITLE_BAR_HEIGHT + TOAST_ROW2_TITLE_Y, _uiTextPaint);
            string toastModeDesc = _selectedToastModeIndex switch
            {
                2 => "完整显示应用名、发送者与消息主体",
                1 => "右侧展示“现在”与应用名",
                _ => "仅显示发送者与消息主体"
            };
            canvas.DrawText(toastModeDesc, RightColumnText, TITLE_BAR_HEIGHT + TOAST_ROW2_DESC_Y, _subTextPaint);

            DrawDropdownBox(canvas, TOAST_MODE_CTRL_X, TITLE_BAR_HEIGHT + TOAST_MODE_ROW_Y, TOAST_MODE_CTRL_W, TOAST_MODE_ROW_H,
                _toastModeOptions[_selectedToastModeIndex], _toastModeDropdownHovered, enabled: true,
                open: _toastModeDropdownOpen);

            canvas.DrawLine(RightColumnText, TITLE_BAR_HEIGHT + SOUND_SEP_Y, WIDTH - CONTENT_TEXT_RM, TITLE_BAR_HEIGHT + SOUND_SEP_Y, _separatorPaint);

            // ── 行 3：消息提示音开关（内容随分隔线下方两行统一下移 SEP_ROW_CONTENT_DY）──
            DrawToggleRow(canvas, SOUND_ROW3_Y + SEP_ROW_CONTENT_DY, "消息提示音",
                _soundHint.Length > 0 ? _soundHint : "新消息到达时播放提示音",
                ToastSoundConfig.IsEnabled, _soundToggleHovered, textLeft: RightColumnText);

            // ── 行 4：提示音设置（行 3 的附属，无开关）──
            bool soundRowEnabled = ToastSoundConfig.IsRowEnabled;
            bool soundReady = ToastSoundConfig.IsSourceReady;

            DrawDropdownBox(canvas, SOUND_CTRL_X, TITLE_BAR_HEIGHT + SOUND_BOX_Y, SOUND_CTRL_W, SOUND_ROW_H,
                ToastSoundConfig.CurrentDisplayText(), _toastSoundDropdownHovered, enabled: soundRowEnabled,
                open: _toastSoundDropdownOpen);

            DrawDropdownBox(canvas, SOUND_VOL_X, TITLE_BAR_HEIGHT + SOUND_BOX_Y, SOUND_VOL_W, SOUND_ROW_H,
                $"{ToastSoundConfig.VolumePercent}%", _soundVolumeDropdownHovered, enabled: soundReady,
                open: _soundVolumeDropdownOpen);

            void DrawSoundButton(bool hovered, string label, float bx, float bw, bool enabled)
            {
                var btn = new SKRect(bx, TITLE_BAR_HEIGHT + SOUND_BTN_Y, bx + bw, TITLE_BAR_HEIGHT + SOUND_BTN_Y + SOUND_BTN_H);
                DrawSettingsButton(canvas, btn, label, hovered, enabled);
            }

            DrawSoundButton(_soundPreviewHovered, "试听", SOUND_PREVIEW_X, SOUND_BTN_W, soundReady);
            DrawSoundButton(_soundResetHovered, "重置", SOUND_RESET_X, SOUND_BTN_W, soundReady);

            // MSP 接入（实验性）：把灵动岛暴露成本机 MSP 节点，外部程序可弹通知、读控媒体。
            // 默认关闭 —— 它会监听一个本地端口，属于对外接口面，得用户自己点头。
            DrawToggleCard(canvas, MSP_CARD_Y, MspCardTitle,
                "让本机 MSP 程序弹通知、读控媒体",
                MspNotchBridge.IsEnabled, _mspToggleHovered, badge: MspBadgeText, leftColumn: true);
            DrawExternalLinkIcon(canvas, MspWikiIconRect(), _mspWikiHovered);

            var fontCard = new SKRect(CONTENT_L, TITLE_BAR_HEIGHT + FONT_CARD_Y, LeftColumnRight, TITLE_BAR_HEIGHT + FONT_CARD_Y + TOGGLE_CARD_H);
            DrawSettingsCard(canvas, fontCard);
            canvas.DrawText("切换灵动岛字体", CONTENT_TEXT_X, TITLE_BAR_HEIGHT + FONT_CARD_Y + ROW_TEXT_BASELINE, _uiTextPaint);

            bool fontError = _fontHint.Length > 0;
            string fontSub = fontError ? _fontHint : $"当前：{FontConfig.DisplayName}";
            _subTextPaint.Color = fontError ? DangerText : SecondaryText;
            canvas.DrawText(TruncateText(fontSub, _subTextPaint, FONT_PICK_X - CONTENT_TEXT_X - 8), CONTENT_TEXT_X,
                TITLE_BAR_HEIGHT + FONT_CARD_Y + ROW_TEXT_BASELINE + ROW_SUB_OFFSET, _subTextPaint);
            _subTextPaint.Color = SecondaryText;

            void DrawFontButton(bool hovered, string label, float bx, float bw)
            {
                var btn = new SKRect(bx, TITLE_BAR_HEIGHT + FONT_BTN_Y, bx + bw, TITLE_BAR_HEIGHT + FONT_BTN_Y + FONT_BTN_H);
                DrawSettingsButton(canvas, btn, label, hovered);
            }

            DrawFontButton(_fontPickHovered, "选择字体", FONT_PICK_X, FONT_PICK_W);
            DrawFontButton(_fontResetHovered, "重置", FONT_RESET_X, FONT_RESET_W);
        }

        private void DrawDropdownBox(SKCanvas canvas, float x, float y, float w, float h,
                                     string text, bool hovered, bool enabled,
                                     bool open = false, bool builtinTag = false, string? warn = null)
        {
            var rect = new SKRect(x, y, x + w, y + h);
            DrawControlSurface(canvas, rect, hovered, enabled, focused: open);

            float tagW = builtinTag ? _subTextPaint.MeasureText("内置") + 14f : 0f;
            // 右侧状态字（如「加载失败」）占「内置」胶囊那一格：正文按剩余宽度截断，不会压到它。
            float warnW = warn != null ? _subTextPaint.MeasureText(warn) + 6f : 0f;
            float textLimit = w - DROPDOWN_TEXT_PAD_L - DROPDOWN_CHEVRON_AREA
                - (tagW > 0f ? tagW + 8f : 0f) - warnW;
            _uiTextPaint.Color = enabled ? _fgColor : DisabledText;
            canvas.DrawText(TruncateText(text, _uiTextPaint, textLimit),
                rect.Left + DROPDOWN_TEXT_PAD_L, rect.Top + h / 2f + 5f, _uiTextPaint);
            _uiTextPaint.Color = _fgColor;

            if (builtinTag)
                DrawBuiltinTag(canvas, rect.Right - DROPDOWN_CHEVRON_AREA - tagW - 2f, rect.Top + (h - 16f) / 2f);

            if (warn != null)
            {
                var prevColor = _subTextPaint.Color;
                _subTextPaint.Color = DangerText;
                float warnRight = rect.Right - DROPDOWN_CHEVRON_AREA - 2f;
                // 基线跟正文对齐（12px 压在 13px 的同一基线上，底部齐平不显飘）
                canvas.DrawText(warn, warnRight - _subTextPaint.MeasureText(warn),
                    rect.Top + h / 2f + 5f, _subTextPaint);
                _subTextPaint.Color = prevColor;
            }

            float cx = rect.Right - 19f;
            float cy = rect.MidY;
            float dir = open ? -1f : 1f;   // 展开时箭头朝上
            float tipY = cy + dir * 2.5f;
            _chevronPaint.Color = enabled ? SecondaryText : DisabledText;
            canvas.DrawLine(cx, tipY - dir * 5f, cx + 5f, tipY, _chevronPaint);
            canvas.DrawLine(cx + 5f, tipY, cx + 10f, tipY - dir * 5f, _chevronPaint);
            _chevronPaint.Color = Neutral(150);
        }

        // 下拉框和列表共用的内置项标签。
        private float DrawBuiltinTag(SKCanvas canvas, float x, float top, bool enabled = true)
        {
            const string tag = "内置";
            float tagW = _subTextPaint.MeasureText(tag) + 14f;
            _dynamicFillPaint.Color = Overlay(enabled ? (byte)16 : (byte)7);
            canvas.DrawRoundRect(new SKRect(x, top, x + tagW, top + 16f), 4, 4, _dynamicFillPaint);
            _subTextPaint.Color = enabled ? SecondaryText : DisabledText;
            canvas.DrawText(tag, x + 7f, top + 12.5f, _subTextPaint);
            _subTextPaint.Color = SecondaryText;
            return tagW;
        }

        // 页签：显示设置
        private void RenderTabDisplay(SKCanvas canvas)
        {
            float page = -_displayPageScroll;

            var styleCardRect = new SKRect(CONTENT_L, TITLE_BAR_HEIGHT + STYLE_CARD_Y + page,
                LeftColumnRight, TITLE_BAR_HEIGHT + STYLE_CARD_Y + STYLE_CARD_H + page);
            DrawSettingsCard(canvas, styleCardRect);

            void DrawStyleOption(int index, string name, float x, float y)
            {
                bool isSelected = Renderer.NotchStyle == index;
                bool isHovered = _hoveredStyleIndex == index;

                // 选项外框与背景反馈
                var optRect = new SKRect(x, y, x + STYLE_OPT_W, y + STYLE_OPT_H);
                DrawControlSurface(canvas, optRect, isHovered, selected: isSelected);

                // 绘制纯血 Skia 伪 PNG 视觉特效图
                float cx = x + 75; float cy = y + 35;

                // 两种形态先收进同一条路径：填色 / 玻璃预览共用，免得两处各画一遍。
                using var shape = new SKPath();
                if (index == 0) // 调整经典刘海的矢量绘图比例，使其视觉高度和灵动岛保持一致
                {
                    shape.MoveTo(cx - 35, cy - 10);
                    shape.QuadTo(cx - 25, cy - 10, cx - 25, cy - 5);
                    shape.LineTo(cx - 25, cy + 5);
                    shape.QuadTo(cx - 25, cy + 10, cx - 15, cy + 10);
                    shape.LineTo(cx + 15, cy + 10);
                    shape.QuadTo(cx + 25, cy + 10, cx + 25, cy + 5);
                    shape.LineTo(cx + 25, cy - 5);
                    shape.QuadTo(cx + 25, cy - 10, cx + 35, cy - 10);
                }
                else // 模拟灵动岛：统一高度 20px，圆角 10px 形成胶囊
                {
                    shape.AddRoundRect(new SKRect(cx - 25, cy - 10, cx + 25, cy + 10), 10, 10);
                }

                // 预览里的岛体跟着当前背景材质走：选了液态玻璃就画成玻璃（真机上岛就是一块玻璃），
                // 实体 / 亚克力仍画纯色剪影 —— 这张卡是形态示意，材质信息不能在预览里丢。
                if (Renderer.IslandLiquidGlass)
                {
                    DrawLiquidGlassPreview(canvas, shape);
                }
                else
                {
                    _dynamicFillPaint.Color = IsLightPreviewCapsule() ? SKColors.White : SKColors.Black;
                    canvas.DrawPath(shape, _dynamicFillPaint);
                }

                // 单选 Radio 按钮与文本：作为整体在框内居中（文字宽度随名字变，钉死偏移会歪）。
                float radioY = y + 72;
                const float radioDiameter = 12f, radioTextGap = 9f;
                float radioGroupW = radioDiameter + radioTextGap + _dynamicTextPaint.MeasureText(name);
                float radioGroupLeft = x + (STYLE_OPT_W - radioGroupW) / 2f;
                float radioCx = radioGroupLeft + radioDiameter / 2f;
                _dynamicStrokePaint.Color = isSelected ? AccentText : SecondaryText;
                canvas.DrawCircle(radioCx, radioY - 4, 6, _dynamicStrokePaint);
                if (isSelected)
                {
                    _dynamicFillPaint.Color = AccentText;
                    canvas.DrawCircle(radioCx, radioY - 4, 3, _dynamicFillPaint);
                }
                _dynamicTextPaint.Color = isSelected ? AccentText : _fgColor;
                canvas.DrawText(name, radioGroupLeft + radioDiameter + radioTextGap, radioY + 1, _dynamicTextPaint);
            }

            DrawStyleOption(0, "经典刘海", STYLE_OPT_X, TITLE_BAR_HEIGHT + STYLE_OPT_Y + page);
            DrawStyleOption(1, "悬浮灵动岛", STYLE_OPT_X + STYLE_OPT_W + CARD_GAP, TITLE_BAR_HEIGHT + STYLE_OPT_Y + page);

            var displayOptions = new SKRect(CONTENT_L, TITLE_BAR_HEIGHT + DISPLAY_OPTIONS_CARD_Y + page,
                LeftColumnRight, TITLE_BAR_HEIGHT + TOGGLE_ROW_Y + page + ROW_H + 8f + DISPLAY_OPTIONS_EXTRA_PAD);
            DrawSettingsCard(canvas, displayOptions);
            float monitorRowY = TITLE_BAR_HEIGHT + MONITOR_ROW_Y + page;
            canvas.DrawText("目标显示器", CONTENT_TEXT_X, monitorRowY + ROW_LABEL_DY, _uiTextPaint);
            float mdX = DISPLAY_CTRL_RIGHT - MONITOR_DD_W;
            float mdY = monitorRowY + (ROW_H - MONITOR_DD_H) / 2f;
            string mName = Renderer.TargetMonitorIndex < _monitorOptions.Length ? _monitorOptions[Renderer.TargetMonitorIndex] : "未知";
            DrawDropdownBox(canvas, mdX, mdY, MONITOR_DD_W, MONITOR_DD_H, mName,
                _monitorDropdownHovered, enabled: true, open: _monitorDropdownOpen);

            float modeRowY = TITLE_BAR_HEIGHT + MODE_ROW_Y + page;
            canvas.DrawText("显示模式", CONTENT_TEXT_X, modeRowY + ROW_LABEL_DY, _uiTextPaint);
            var modeBox = DisplayModeDropdownBox;
            DrawDropdownBox(canvas, modeBox.Left, modeBox.Top, modeBox.Width, modeBox.Height,
                DisplayModeLabels[(int)Renderer.DisplayMode], _displayModeDropdownHovered, enabled: true,
                open: _displayModeDropdownOpen);

            float sceneRowY = TITLE_BAR_HEIGHT + SCENE_ROW_Y + page;
            canvas.DrawText("待机显示内容", CONTENT_TEXT_X, sceneRowY + ROW_LABEL_DY, _uiTextPaint);
            // 这一行永远是「待机显示内容」：只有待机模式可点，普通 / 静默模式一律置灰（形态不变）。
            // 选中的第三方插件没加载起来时：左边照常显示插件名（缓存名兜底，不再是裸 DLL 文件名），
            // 右边红字写明「加载失败」；同一时刻待机岛是空白（见 Renderer.ResolveStandbyPluginId）。
            bool sceneFailed = TryGetStandbySceneFailure(out string sceneName);
            string sceneText = sceneFailed && sceneName.Length > 0 ? sceneName : CurrentStandbySceneName();
            DrawDropdownBox(canvas, SCENE_SEG_X, sceneRowY + (ROW_H - SCENE_DD_H) / 2f, SCENE_SEG_W, SCENE_DD_H,
                sceneText, _sceneDropdownHovered, enabled: Renderer.StandbyModeOnly,
                open: _sceneDropdownOpen, builtinTag: BuiltinWidgets.IsBuiltin(Renderer.StandbySceneKey),
                warn: sceneFailed ? StandbySceneFailText : null);

            //（见 Core/NotchWindow 的双击分支）。
            // 静默模式下双击切换毫无意义（岛本来就退场了），整行跟着置灰。
            DrawToggleRow(canvas, TOGGLE_ROW_Y + page,
                "双击空白切换待机模式", "",
                Renderer.StandbyToggleByDoubleClick, _standbyToggleHovered,
                disabled: Renderer.SilentActive,
                rightHint: Renderer.SilentActive ? "静默模式下不支持" : "主岛空白处双击，实况窗双击无效",
                rowHovered: _standbyRowHovered, cardRight: LeftColumnRight);

            // ── 显示内容列表 ──
            // 靠「悬停才亮的那层底」表达可点，视觉上跟整页连成一体。
            // 待机模式禁用它（待机只看单一场景）；普通模式用常驻清单；静默模式用独立的静默清单，
            // 里面内置项没有「重要信息」概念 → 行内勾选框置灰。
            float contentCardY = TITLE_BAR_HEIGHT + DISPLAY_CARD_Y + page;
            var displayList = new SKRect(RightColumnLeft, contentCardY, WIDTH - CONTENT_RM, contentCardY + DISPLAY_CARD_H);
            DrawSettingsCard(canvas, displayList);
            canvas.DrawText("显示内容", RightColumnText, contentCardY + 24, _uiTextPaint);
            // 静默模式那份清单已经独立成「静默模式设置」页（tab 9），这一页只管普通模式的常驻内容。
            bool listEnabled = !Renderer.StandbyModeOnly && !Renderer.SilentActive;
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
                bool rowEnabled = listEnabled;

                DrawDisplayItem(canvas, rowY, item.Name, item.IsBuiltin, item.IsShown,
                    rowEnabled ? GetDisplayHoverProgress(slot) : 0f, rowEnabled,
                    _hoveredDisplayMoveUp == i, _hoveredDisplayMoveDown == i,
                    _hoveredDisplayDrag == i,
                    (_displayDragActive && _displayDragRow == i) || _displayDragPendingRow == i,
                    rowEnabled && PluginManager.Instance.CanMoveDisplay(item.Key, -1),
                    rowEnabled && PluginManager.Instance.CanMoveDisplay(item.Key, 1));
            }

            if (maxFirstRow > 0 && visibleRows > 0)
            {
                GetListScrollbarLayout(out float listTrackTop, out float listTrackH);
                DrawScrollBar(canvas, DisplayListScrollbarX, listTrackTop, listTrackH,
                    visibleRows / (float)displayItems.Count, _displayScroll / (float)maxFirstRow);
            }

            float pageMaxScroll = GetDisplayPageMaxScroll();
            if (pageMaxScroll > 0f)
            {
                GetPageScrollbarLayout(out float pageTrackTop, out float pageTrackH);
                float contentH = TITLE_BAR_HEIGHT + DISPLAY_CARD_Y + DISPLAY_CARD_H + 6f;
                DrawScrollBar(canvas, DisplayPageScrollbarX, pageTrackTop, pageTrackH,
                    HEIGHT / contentH, _displayPageScroll / pageMaxScroll, minThumbH: 24f);
            }
        }

        // 页签：静默模式设置（左侧栏「显示设置」下面一项，从 2.3.0 起独立成页）
        private const float SILENT_INTRO_CARD_Y = CARD_TOP_Y;

        private const float SILENT_INTRO_CARD_H = 92f;

        // 消息通知卡（一行入口，样式同「实况窗管理」里的媒体卡）：状态跟随通用设置，不放进列表。
        private const float SILENT_TOAST_CARD_Y = SILENT_INTRO_CARD_Y + SILENT_INTRO_CARD_H + CARD_GAP;

        private const float SILENT_TOAST_CARD_H = 62f;

        private static float SilentToastTop => TITLE_BAR_HEIGHT + SILENT_TOAST_CARD_Y;

        // 媒体卡（媒体是否作为重要信息）：整卡点击切换，默认开启。样式与消息通知卡同款。
        private const float SILENT_MEDIA_CARD_Y = SILENT_TOAST_CARD_Y + SILENT_TOAST_CARD_H + CARD_GAP;

        private const float SILENT_MEDIA_CARD_H = 62f;

        private static float SilentMediaTop => TITLE_BAR_HEIGHT + SILENT_MEDIA_CARD_Y;

        private const float SilentListCardY = CARD_TOP_Y;

        // 重要信息列表：与「显示内容」列表同一条规则 —— 卡片只装得下真实条数，
        // 条数多到装不下才一直排到内容区底边并改成内部滚动。
        private float SilentListCardH
        {
            get
            {
                int rows = Math.Max(1, PluginManager.Instance.SilentDisplayItems.Count);
                float contentH = SILENT_LIST_FIRST_ROW_Y + rows * SILENT_LIST_ROW_H + CARD_BOTTOM_PAD;
                return Math.Clamp(contentH, 0f, ContentBottom - (TITLE_BAR_HEIGHT + SilentListCardY));
            }
        }

        // 与显示设置页的列表同高，行内控件位置也完全复用 DrawDisplayItem。
        private const float SILENT_LIST_ROW_H = 33f;

        private const float SILENT_LIST_FIRST_ROW_Y = 36f;

        // 说明卡右上角那颗「去切换」按钮（只在非静默模式出现）：几何与命中同源。
        private const float SILENT_SWITCH_W = 78f;

        private const float SILENT_SWITCH_H = 26f;

        private float SilentSwitchLeft => DISPLAY_CTRL_RIGHT - SILENT_SWITCH_W;

        private static float SilentSwitchTop => TITLE_BAR_HEIGHT + SILENT_INTRO_CARD_Y + 8f;

        private int _silentListScroll;

        private int SilentListVisibleRows()
        {
            int fit = Math.Max(1, (int)((SilentListCardH - SILENT_LIST_FIRST_ROW_Y) / SILENT_LIST_ROW_H));
            return fit;
        }

        private void RenderTabSilentMode(SKCanvas canvas)
        {
            float introTop = TITLE_BAR_HEIGHT + SILENT_INTRO_CARD_Y;
            var introRect = new SKRect(CONTENT_L, introTop, LeftColumnRight, introTop + SILENT_INTRO_CARD_H);
            DrawSettingsCard(canvas, introRect);

            canvas.DrawText("静默模式", CONTENT_TEXT_X, introTop + 26, _uiTextPaint);

            // 右上角状态 + 「去切换」按钮：按钮不受模式影响，始终显示（静默时点了去显示设置切回来）。
            var prevColor = _subTextPaint.Color;
            float prevSize = _subTextPaint.TextSize;
            _subTextPaint.TextSize = 12f;
            _subTextPaint.Color = Renderer.SilentActive ? AccentText : Neutral(150);
            string stateText = Renderer.SilentActive ? "当前：静默模式" : "当前不是静默模式";
            float stateRight = SilentSwitchLeft - 10f;
            canvas.DrawText(stateText, stateRight - _subTextPaint.MeasureText(stateText),
                introTop + 26, _subTextPaint);
            _subTextPaint.TextSize = prevSize;
            _subTextPaint.Color = prevColor;

            {
                var btnRect = new SKRect(SilentSwitchLeft, SilentSwitchTop,
                    DISPLAY_CTRL_RIGHT, SilentSwitchTop + SILENT_SWITCH_H);
                DrawSettingsButton(canvas, btnRect, "去切换", _silentSwitchHovered);
            }

            _subTextPaint.Color = SecondaryText;
            float introTextMax = (LeftColumnRight - 16) - CONTENT_TEXT_X;
            canvas.DrawText(TruncateText("平时整块隐藏，插件上报「重要信息」（如下载中）时才弹出来。",
                    _subTextPaint, introTextMax), CONTENT_TEXT_X, introTop + 50, _subTextPaint);
            canvas.DrawText(TruncateText("右侧只列声明了重要信息的插件；勾选后仍需真有重要信息才显示。",
                    _subTextPaint, introTextMax), CONTENT_TEXT_X, introTop + 72, _subTextPaint);

            // ── 消息通知：系统内容里唯一能在静默模式出现的项，状态与通用设置双向跟随 ──
            float toastTop = TITLE_BAR_HEIGHT + SILENT_TOAST_CARD_Y;
            var toastRect = new SKRect(CONTENT_L, toastTop, LeftColumnRight, toastTop + SILENT_TOAST_CARD_H);
            DrawSettingsCard(canvas, toastRect);
            if (_silentToastHovered)
            {
                _dynamicFillPaint.Color = Overlay(12);
                canvas.DrawRoundRect(toastRect, 6, 6, _dynamicFillPaint);
            }

            canvas.DrawText("消息通知", CONTENT_TEXT_X, toastTop + 26, _uiTextPaint);
            _subTextPaint.Color = SecondaryText;
            canvas.DrawText(NotchWindow.IsToastEnabled ? "已启用（跟随「通用设置」）" : "已关闭（跟随「通用设置」）",
                CONTENT_TEXT_X, toastTop + 46, _subTextPaint);

            const string toastGo = "去「通用设置」修改";
            _subTextPaint.Color = _silentToastHovered ? AccentText : AccentText;
            canvas.DrawText(toastGo, DISPLAY_CTRL_RIGHT - _subTextPaint.MeasureText(toastGo),
                toastTop + 34, _subTextPaint);
            _subTextPaint.Color = SecondaryText;

            // ── 媒体：是否作为重要信息（勾选框开关，默认开启；与实况窗无关）──
            float mediaCardTop = TITLE_BAR_HEIGHT + SILENT_MEDIA_CARD_Y;
            var mediaCardRect = new SKRect(CONTENT_L, mediaCardTop, LeftColumnRight, mediaCardTop + SILENT_MEDIA_CARD_H);
            DrawSettingsCard(canvas, mediaCardRect);
            if (_silentMediaHovered)
            {
                _dynamicFillPaint.Color = Overlay(12);
                canvas.DrawRoundRect(mediaCardRect, 6, 6, _dynamicFillPaint);
            }

            bool mediaOn = SilentMode.MediaImportantEnabled;
            canvas.DrawText("媒体", CONTENT_TEXT_X, mediaCardTop + 26, _uiTextPaint);
            _subTextPaint.Color = SecondaryText;
            canvas.DrawText(TruncateText("开启后静默模式展示媒体，关闭后不展示（与实况窗无关）", _subTextPaint, LeftColumnRight - CONTENT_TEXT_X - 72),
                CONTENT_TEXT_X, mediaCardTop + 46, _subTextPaint);

            const float tW = 44f, tH = 22f;
            var tRect = new SKRect(
                DISPLAY_CTRL_RIGHT - 6f - tW, mediaCardTop + (SILENT_MEDIA_CARD_H - tH) / 2f,
                DISPLAY_CTRL_RIGHT - 6f, mediaCardTop + (SILENT_MEDIA_CARD_H + tH) / 2f);
            DrawSettingsSwitch(canvas, tRect, mediaOn, _silentMediaHovered);

            // ── 静默显示列表 ──
            float cardY = TITLE_BAR_HEIGHT + SilentListCardY;
            var listRect = new SKRect(RightColumnLeft, cardY, WIDTH - CONTENT_RM, cardY + SilentListCardH);
            DrawSettingsCard(canvas, listRect);

            canvas.DrawText("重要信息", RightColumnText, cardY + 24, _uiTextPaint);
            var items = PluginManager.Instance.SilentDisplayItems;
            int visibleRows = SilentListVisibleRows();
            int maxFirst = Math.Max(0, items.Count - visibleRows);
            _silentListScroll = Math.Clamp(_silentListScroll, 0, maxFirst);

            if (items.Count == 0)
                canvas.DrawText("还没有插件声明「重要信息」", DISPLAY_ITEM_L + 10f, cardY + SILENT_LIST_FIRST_ROW_Y + 20, _subTextPaint);

            for (int slot = 0; slot < Math.Min(visibleRows, items.Count); slot++)
            {
                int i = _silentListScroll + slot;
                var item = items[i];
                float rowY = cardY + SILENT_LIST_FIRST_ROW_Y + slot * SILENT_LIST_ROW_H;

                // 列表里只有「声明过重要信息」的插件，全部可勾选 / 排序。
                bool rowEnabled = true;

                DrawDisplayItem(canvas, rowY, item.Name, item.IsBuiltin, item.IsShown,
                    GetDisplayHoverProgress(slot), rowEnabled,
                    _hoveredSilentMoveUp == i, _hoveredSilentMoveDown == i,
                    false, false,
                    PluginManager.Instance.CanMoveDisplay(item.Key, -1),
                    PluginManager.Instance.CanMoveDisplay(item.Key, 1));
            }
        }

        // 页签：媒体设置
        private void RenderTabMedia(SKCanvas canvas)
        {
            var mediaCard = new SKRect(CONTENT_L, MediaCardTop, LeftColumnRight, MediaCardTop + MediaTopLeftCardH);
            DrawSettingsCard(canvas, mediaCard);
            DrawToggleRow(canvas, MediaRowStart, "媒体控制", "允许在刘海中显示和控制系统媒体播放",
                MediaController.IsMediaControlEnabled, _mediaToggleHovered, cardRight: LeftColumnRight);
            DrawToggleRow(canvas, MediaTopLeftRow2Y, "使用实况窗展示媒体",
                "检测到媒体先显示 3 秒，再收进左侧实况窗（封面旋转）",
                MediaLiveWindowController.IsEnabled, _mediaLiveToggleHovered, cardRight: LeftColumnRight);

            var platformCard = new SKRect(RightColumnLeft, MediaCardTop, WIDTH - CONTENT_RM, MediaCardTop + MediaTopRightCardH);
            DrawSettingsCard(canvas, platformCard);
            canvas.DrawText("目标媒体平台", RightColumnText, PlatformBoxY + 10, _uiTextPaint);
            string platformHint = MediaController.TargetPlatform == "browser" ? "仅接管浏览器内的播放会话" : "多平台共存时，优先截获并接管的平台";
            canvas.DrawText(TruncateText(platformHint, _subTextPaint, PlatformBoxX - RightColumnText - 8),
                RightColumnText, PlatformBoxY + 29, _subTextPaint);
            DrawDropdownBox(canvas, PlatformBoxX, PlatformBoxY, 110, 32, _platforms[_selectedPlatformIndex].Name,
                _dropdownHovered, enabled: true, open: _dropdownOpen);

            bool matchEnabled = MediaController.TargetPlatform == "other";
            bool appBoxEnabled = matchEnabled && MediaController.IsManualSessionMatch;
            _uiTextPaint.Color = matchEnabled ? _fgColor : Neutral(100);
            canvas.DrawText("匹配方式", RightColumnText, PLATFORM_ROW2_Y + 24, _uiTextPaint);
            _uiTextPaint.Color = _fgColor;
            _subTextPaint.Color = matchEnabled ? SecondaryText : Neutral(80);
            canvas.DrawText(TruncateText("自动匹配或手动指定", _subTextPaint, MATCH_MODE_X - RightColumnText - 8),
                RightColumnText, PLATFORM_ROW2_Y + 43, _subTextPaint);
            _subTextPaint.Color = SecondaryText;

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

            float lyricY = LyricCardContentTop;
            var lyricRect = new SKRect(CONTENT_L, LYRIC_CARD_Y, LeftColumnRight, LYRIC_CARD_Y + LyricCardHeight);
            DrawSettingsCard(canvas, lyricRect);

            // 歌词开关（四行统一 34px 行距：原 40px 在 692 高窗口里显得太空）
            canvas.DrawText("在刘海中显示歌词", CONTENT_TEXT_X, lyricY + 48, _subTextPaint);
            float tW = 42, tH = 20;
            float tX = LeftColumnRight - 16 - tW, tY = lyricY + LYRIC_TOGGLE1_DY;
            var tRect = new SKRect(tX, tY, tX + tW, tY + tH);
            DrawSettingsSwitch(canvas, tRect, MediaController.IsLyricsEnabled, _lyricToggleHovered);

            canvas.DrawText("显示翻译歌词（上下两行）", CONTENT_TEXT_X, lyricY + 82, _subTextPaint);
            float trY = lyricY + LYRIC_TOGGLE2_DY;
            var trRect = new SKRect(tX, trY, tX + tW, trY + tH);
            DrawSettingsSwitch(canvas, trRect, MediaController.IsTranslationEnabled, _transToggleHovered);

            canvas.DrawText("逐字歌词（不可用时自动回退卡拉OK）", CONTENT_TEXT_X, lyricY + 116, _subTextPaint);
            float kY = lyricY + LYRIC_TOGGLE3_DY;
            var kRect = new SKRect(tX, kY, tX + tW, kY + tH);
            DrawSettingsSwitch(canvas, kRect, MediaController.IsLyricScanEnabled, _scanToggleHovered);

            // 延迟调整
            canvas.DrawText("歌词延迟补偿", CONTENT_TEXT_X, lyricY + 150, _subTextPaint);
            float cardRightX = LeftColumnRight - 16;
            float btnY = lyricY + LYRIC_DELAY_DY;

            DrawSettingsButton(canvas, new SKRect(cardRightX - 175, btnY, cardRightX - 145, btnY + LYRIC_BTN_H), "-", _lyricMinusHovered);

            string valStr = $"{MediaController.LyricDelayOffset:F1} s";
            if (MediaController.LyricDelayOffset > 0) valStr = "+" + valStr;
            float textW = _uiTextPaint.MeasureText(valStr);
            canvas.DrawText(valStr, cardRightX - 90 - textW, btnY + 17, _uiTextPaint);

            DrawSettingsButton(canvas, new SKRect(cardRightX - 80, btnY, cardRightX - 50, btnY + LYRIC_BTN_H), "+", _lyricPlusHovered);

            DrawSettingsButton(canvas, new SKRect(cardRightX - 40, btnY, cardRightX, btnY + LYRIC_BTN_H), "重置", _lyricResetHovered);

            DrawHotkeyCard(canvas);
        }

        private void DrawHotkeyCard(SKCanvas canvas)
        {
            float cardY = HOTKEY_CARD_Y;
            var cardRect = new SKRect(RightColumnLeft, cardY, WIDTH - CONTENT_RM, cardY + MediaHotkeyCardHeight);
            DrawSettingsCard(canvas, cardRect);


            bool hasHint = _hotkeyHint.Length > 0;
            bool recording = _hotkeyRecordingIndex >= 0;
            // 优先级：错误提示 > 录制操作说明 > 平时说明。
            //    （框里只能放得下「按下按键…」四个字）。
            string sub = hasHint ? _hotkeyHint
                : (recording ? "按下按键录制 · Esc 取消 · Backspace 清空"
                             : "在其他窗口也能控制播放 · 点按键框可重录");
            _subTextPaint.Color = hasHint ? DangerText : SecondaryText;
            float subMax = (WIDTH - CONTENT_RM - 16f - 42f) - 12f - RightColumnText;
            DrawTextWithEmoji(canvas, sub, _subTextPaint, subMax, RightColumnText, cardY + 28);
            _subTextPaint.Color = SecondaryText;

            float tW = 42f, tH = 20f;
            float tX = WIDTH - CONTENT_RM - 16f - tW;
            float tY = cardY + 12f;
            var tRect = new SKRect(tX, tY, tX + tW, tY + tH);
            DrawSettingsSwitch(canvas, tRect, MediaHotkeys.IsEnabled, _hotkeyToggleHovered);

            // ── 五行键位：左 = 动作名，右 = 键位框 ──
            for (int i = 0; i < MediaHotkeys.Count; i++)
            {
                float rowY = cardY + MediaHotkeyHeadHeight + i * HOTKEY_ROW_H;
                canvas.DrawText(MediaHotkeys.Label(i), RightColumnText, rowY + 20, _subTextPaint);

                bool rowRecording = _hotkeyRecordingIndex == i;
                var boxRect = new SKRect(HOTKEY_BOX_X, rowY + 4, HOTKEY_BOX_RIGHT, rowY + 4 + HOTKEY_BOX_H);

                DrawControlSurface(canvas, boxRect, _hoveredHotkeyRow == i, true, focused: rowRecording);

                string text = rowRecording ? RecordingBoxText() : MediaHotkeys.FormatKey(i);
                _dynamicTextPaint.Color = rowRecording ? AccentText
                    : (MediaHotkeys.IsBound(i) ? _fgColor : Neutral(120));
                DrawTextWithEmoji(canvas, text, _dynamicTextPaint,
                    HOTKEY_BOX_W - 24f, HOTKEY_BOX_RIGHT - 12f, rowY + 20.5f, rightAlign: true);
                _dynamicTextPaint.Color = _fgColor;
            }
        }

        // 页签：交互设置
        private void RenderTabInteraction(SKCanvas canvas)
        {
            // 静默模式：岛的显隐完全由「有没有重要信息」决定，自动隐藏那一套开关在这里没有意义
            //（用户明确要求置灰）。唤出灵动岛同理 —— 静默模式不支持手动唤出。
            bool silent = Renderer.SilentActive;
            bool isAutoHideDisabled = silent;
            bool isModeDisabled = !NotchWindow.IsAutoHideEnabled || silent;
            const string silentSub = "静默模式下由重要信息驱动";
            var autoHideCardRect = new SKRect(CONTENT_L, TITLE_BAR_HEIGHT + AUTOHIDE_CARD_Y, LeftColumnRight, TITLE_BAR_HEIGHT + AUTOHIDE_CARD_Y + AUTOHIDE_CARD_H);
            DrawSettingsCard(canvas, autoHideCardRect);

            // 行 1：总开关，关掉时下面三行一起置灰、不可点。
            DrawToggleRow(canvas, 12, "自动隐藏", silent ? silentSub : "允许灵动岛自动隐藏",
                NotchWindow.IsAutoHideEnabled, _autoHideToggleHovered, isAutoHideDisabled, cardRight: LeftColumnRight);

            canvas.DrawLine(CONTENT_TEXT_X, TITLE_BAR_HEIGHT + 70, LeftColumnRight - 16f, TITLE_BAR_HEIGHT + 70, _separatorPaint);

            // 三个模式行：总开关未开启时置灰、不可点，并显示为关闭。
            // 行 2：「焦点离开时自动隐藏岛」。
            DrawToggleRow(canvas, 74, "当焦点离开时自动隐藏岛",
                silent ? silentSub : (!NotchWindow.IsAutoHideEnabled ? "需先开启上方总开关" : "没有媒体会话时，焦点离开就收起"),
                NotchWindow.IsFocusAutoHideEnabled,
                !isModeDisabled && _focusHideToggleHovered,
                isModeDisabled, cardRight: LeftColumnRight);

            canvas.DrawLine(CONTENT_TEXT_X, TITLE_BAR_HEIGHT + 132, LeftColumnRight - 16f, TITLE_BAR_HEIGHT + 132, _separatorPaint);

            DrawToggleRow(canvas, 136, "暂停播放后自动隐藏",
                silent ? silentSub : (!NotchWindow.IsAutoHideEnabled ? "需先开启上方总开关" : "媒体暂停播放时，也把刘海藏起来"),
                NotchWindow.IsPauseAutoHideEnabled,
                !isModeDisabled && _pauseHideToggleHovered,
                isModeDisabled, cardRight: LeftColumnRight);

            canvas.DrawLine(CONTENT_TEXT_X, TITLE_BAR_HEIGHT + 194, LeftColumnRight - 16f, TITLE_BAR_HEIGHT + 194, _separatorPaint);

            DrawToggleRow(canvas, 198, "全屏自动隐藏",
                silent ? silentSub : (!NotchWindow.IsAutoHideEnabled ? "需先开启上方总开关" : "检测到全屏视频 / 游戏时隐藏"),
                NotchWindow.IsFullscreenAutoHideEnabled,
                !isModeDisabled && _fsHideToggleHovered,
                isModeDisabled, cardRight: LeftColumnRight);

            DrawToggleCard(canvas, MEDIA_EXP_CARD_Y, "媒体交互方式",
                Renderer.MediaInteractionMode == 1
                    ? "展开功能已开启：入口见下方「双击封面跳转应用」"
                    : "展开功能已关闭：折叠态右键直达媒体设置",
                Renderer.MediaInteractionMode == 1, _mediaExpToggleHovered, rightColumn: true);

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
                MediaController.IsAppLaunchEnabled, _appLaunchToggleHovered, rightColumn: true);

            DrawToggleCard(canvas, PASS_CARD_Y, "穿透模式", "悬停时透明并允许鼠标穿透本体与底层窗口交互", Renderer.PassthroughModeEnabled, _passToggleHovered, leftColumn: true);

            DrawIslandHotkeyCard(canvas, Renderer.SilentActive);
            DrawSilentHotkeyCard(canvas);
        }

        /// <summary>
        /// 交互页最下面那张「静默模式」快捷键卡：结构与「唤出灵动岛」卡同源，只是没有禁用态
        ///（静默模式下正是靠它进出，所以永远可点）。
        /// </summary>
        private void DrawSilentHotkeyCard(SKCanvas canvas)
        {
            float cardY = TITLE_BAR_HEIGHT + SILENT_HOTKEY_CARD_Y;
            var cardRect = new SKRect(RightColumnLeft, cardY, WIDTH - CONTENT_RM, cardY + SILENT_HOTKEY_CARD_H);
            DrawSettingsCard(canvas, cardRect);

            canvas.DrawText("静默模式", RightColumnText, cardY + 23, _uiTextPaint);

            // 总开关（出厂关）
            float tW = 42f, tH = 20f;
            float tX = WIDTH - CONTENT_RM - 16f - tW;
            float tY = cardY + 16f;
            var tRect = new SKRect(tX, tY, tX + tW, tY + tH);
            DrawSettingsSwitch(canvas, tRect, SilentHotkey.IsEnabled, _silentHotkeyToggleHovered);

            // 优先级：错误提示 > 录制操作说明 > 未设置键位 > 未开启 > 平时说明。
            string hint = _silentHotkeyHint.Length > 0 ? _silentHotkeyHint : SilentHotkey.LastError;
            bool hasHint = hint.Length > 0;
            string sub = hasHint ? hint
                : _silentHotkeyRecording ? "按下按键录制 · Esc 取消 · Backspace 清空"
                    : (!SilentHotkey.IsBound ? "先点下面的按键框录一个组合键（默认空）"
                        : (!SilentHotkey.IsEnabled ? "已保存，打开右上角开关后生效"
                                                   : "全局生效：按一下在静默与之前模式之间切换"));
            _subTextPaint.Color = hasHint ? DangerText : SecondaryText;
            float subMax = (WIDTH - CONTENT_RM - 16f - tW) - 12f - RightColumnText;
            DrawTextWithEmoji(canvas, sub, _subTextPaint, subMax, RightColumnText, cardY + 40);
            _subTextPaint.Color = SecondaryText;

            float rowY = cardY + HOTKEY_HEAD_H;
            canvas.DrawText("快捷键", RightColumnText, rowY + 20, _subTextPaint);

            var boxRect = new SKRect(HOTKEY_BOX_X, rowY + 4, HOTKEY_BOX_RIGHT, rowY + 4 + HOTKEY_BOX_H);

            DrawControlSurface(canvas, boxRect, _silentHotkeyHovered, true, focused: _silentHotkeyRecording);

            string text = _silentHotkeyRecording ? RecordingBoxText() : SilentHotkey.FormatKey();
            _dynamicTextPaint.Color = _silentHotkeyRecording ? AccentText
                : (SilentHotkey.IsBound ? _fgColor : Neutral(120));
            DrawTextWithEmoji(canvas, text, _dynamicTextPaint,
                HOTKEY_BOX_W - 24f, HOTKEY_BOX_RIGHT - 12f, rowY + 20.5f, rightAlign: true);
            _dynamicTextPaint.Color = _fgColor;
        }

        // 交互页「唤出灵动岛」卡片：只有一个键位，结构与媒体页「全局快捷键」卡片同源
        //（标题 + 副标题 + 一行「名字 … [键位框]」）。
        private void DrawIslandHotkeyCard(SKCanvas canvas, bool disabled = false)
        {
            // ⚠️ 这里必须跟 DrawToggleCard 一样补上 TITLE_BAR_HEIGHT：
            // ISLAND_HOTKEY_CARD_Y 等常量都是「相对内容区顶部」的偏移，而画布用的是全窗口坐标。
            // 少加这一段的后果：卡片被画到上方「穿透模式」卡身上（看着像两张卡叠在一起），
            // 而命中区（ConsoleWindow.WndProc.cs）用的是 TITLE_BAR_HEIGHT + ISLAND_HOTKEY_CARD_Y + …，
            // 于是「看得见的卡片点不动、点得动的地方反而是空的」。
            float cardY = TITLE_BAR_HEIGHT + ISLAND_HOTKEY_CARD_Y;
            var cardRect = new SKRect(RightColumnLeft, cardY, WIDTH - CONTENT_RM, cardY + ISLAND_HOTKEY_CARD_H);
            DrawSettingsCard(canvas, cardRect);

            _uiTextPaint.Color = disabled ? Neutral(100) : _fgColor;
            canvas.DrawText("唤出灵动岛", RightColumnText, cardY + 23, _uiTextPaint);
            _uiTextPaint.Color = _fgColor;

            // 总开关（出厂关，与媒体页那张卡的开关同一套画法）
            float tW = 42f, tH = 20f;
            float tX = WIDTH - CONTENT_RM - 16f - tW;
            float tY = cardY + 16f;
            var tRect = new SKRect(tX, tY, tX + tW, tY + tH);
            DrawSettingsSwitch(canvas, tRect, IslandHotkey.IsEnabled, _islandHotkeyToggleHovered, !disabled);

            // 优先级：错误提示 > 录制操作说明 > 未开启 > 平时说明。
            // 注册失败（键被别的程序占着）在没录制时也要一直挂着，否则用户只会觉得「按了没反应」。
            string hint = _islandHotkeyHint.Length > 0 ? _islandHotkeyHint : IslandHotkey.LastError;
            bool hasHint = hint.Length > 0;
            string sub = disabled ? "静默模式下不支持手动唤出"
                : hasHint ? hint
                : (_islandHotkeyRecording ? "按下按键录制 · Esc 取消 · Backspace 清空"
                    : (!IslandHotkey.IsEnabled ? "开启后按下可隐藏 / 唤出灵动岛 · 点按键框可重录"
                                               : "全局生效 · 点按键框可重录"));
            _subTextPaint.Color = disabled ? Neutral(80) : hasHint ? DangerText : SecondaryText;
            float subMax = (WIDTH - CONTENT_RM - 16f - tW) - 12f - RightColumnText;
            DrawTextWithEmoji(canvas, sub, _subTextPaint, subMax, RightColumnText, cardY + 40);
            _subTextPaint.Color = SecondaryText;

            float rowY = cardY + HOTKEY_HEAD_H;
            _subTextPaint.Color = disabled ? Neutral(80) : SecondaryText;
            canvas.DrawText("快捷键", RightColumnText, rowY + 20, _subTextPaint);
            _subTextPaint.Color = SecondaryText;

            var boxRect = new SKRect(HOTKEY_BOX_X, rowY + 4, HOTKEY_BOX_RIGHT, rowY + 4 + HOTKEY_BOX_H);

            DrawControlSurface(canvas, boxRect, _islandHotkeyHovered, !disabled, focused: _islandHotkeyRecording);

            string text = _islandHotkeyRecording ? RecordingBoxText() : IslandHotkey.FormatKey();
            _dynamicTextPaint.Color = disabled ? Neutral(90)
                : _islandHotkeyRecording ? AccentText
                : (IslandHotkey.IsBound ? _fgColor : Neutral(120));
            DrawTextWithEmoji(canvas, text, _dynamicTextPaint,
                HOTKEY_BOX_W - 24f, HOTKEY_BOX_RIGHT - 12f, rowY + 20.5f, rightAlign: true);
            _dynamicTextPaint.Color = _fgColor;
        }

        // 录制框里的实时文案：已按住的修饰键先显示出来（"Ctrl + Shift + …"）。
        // 只按修饰键时界面必须立刻有反应 —— 否则用户会判断成「它根本不识别 Ctrl / Shift / Alt / Win」。
        // 两个录制入口（媒体键位 / 唤出灵动岛）共用同一份状态，同时只可能有一个在录。
        private string RecordingBoxText()
        {
            string mods = HotkeyText.FormatMods(_captureLiveMods);
            return mods.Length > 0 ? mods + "…" : "按下按键…";
        }

        private const float AboutIconTop = 22f;
        private const float AboutIconSize = 64f;
        private const float AboutTitleBaseline = 112f;
        private const float AboutSubtitleBaseline = 137f;
        private const float AboutButtonTop = 154f;
        private const float AboutButtonHeight = 30f;
        private const float AboutButtonWidth = 84f;
        private const float AboutButtonGap = 8f;
        private const float AboutCardHeight = AboutButtonTop + AboutButtonHeight + 16f;

        private const int AboutProjectLinkCount = 3;

        private static readonly (string Label, string? Url)[] AboutLinks =
        [
            ("检测更新", null),
            ("项目主页", "https://nps.georgewu.top/"),
            ("项目仓库", "https://github.com/GEORGEWWWU/NotchPeninsula"),
            ("个人主页", "https://georgewu.top/"),
            ("个人博客", "https://blog.georgewu.top/")
        ];

        private static readonly Lazy<SKBitmap?> AboutAuthorAvatar = new(() =>
        {
            using var stream = DataResources.OpenRead("data/image/ryenryen-avatar.png");
            return stream == null ? null : SKBitmap.Decode(stream);
        });

        private SKRect AboutLinkRect(int index)
        {
            float y = TITLE_BAR_HEIGHT + CARD_TOP_Y + AboutButtonTop;
            bool authorLink = index >= AboutProjectLinkCount;
            int first = authorLink ? AboutProjectLinkCount : 0;
            int count = authorLink ? AboutLinks.Length - first : AboutProjectLinkCount;
            float cardLeft = authorLink ? RightColumnLeft : CONTENT_L;
            float cardRight = authorLink ? WIDTH - CONTENT_RM : LeftColumnRight;
            float groupWidth = count * AboutButtonWidth + (count - 1) * AboutButtonGap;
            float left = (cardLeft + cardRight - groupWidth) / 2f
                + (index - first) * (AboutButtonWidth + AboutButtonGap);
            return SKRect.Create(left, y, AboutButtonWidth, AboutButtonHeight);
        }

        private int HitAboutLink(int x, int y)
        {
            if (_selectedTab == 4)
                for (int i = 0; i < AboutLinks.Length; i++)
                    if (AboutLinkRect(i).Contains(x, y)) return i;
            return -1;
        }

        private void RenderTabAbout(SKCanvas canvas)
        {
            float top = TITLE_BAR_HEIGHT + CARD_TOP_Y;
            var project = new SKRect(CONTENT_L, top, LeftColumnRight, top + AboutCardHeight);
            var author = new SKRect(RightColumnLeft, top, WIDTH - CONTENT_RM, top + AboutCardHeight);
            DrawSettingsCard(canvas, project);
            DrawSettingsCard(canvas, author);

            SKRect IconRect(float centerX) =>
                SKRect.Create(centerX - AboutIconSize / 2f, top + AboutIconTop, AboutIconSize, AboutIconSize);

            if (_appIconBitmap != null)
                canvas.DrawBitmap(_appIconBitmap, IconRect(project.MidX), _hqSamplingOpts);

            if (AboutAuthorAvatar.Value is { } avatar)
            {
                var avatarRect = IconRect(author.MidX);
                using var mask = new SKPath();
                mask.AddOval(avatarRect);
                canvas.Save();
                canvas.ClipPath(mask, antialias: true);
                canvas.DrawBitmap(avatar, avatarRect, _hqSamplingOpts);
                canvas.Restore();
            }

            _dynamicTextPaint.TextAlign = SKTextAlign.Center;
            _dynamicTextPaint.Color = _fgColor;
            _dynamicTextPaint.TextSize = 20f;
            canvas.DrawText("NotchPeninsula", project.MidX, top + AboutTitleBaseline, _dynamicTextPaint);
            canvas.DrawText("Ryenryen", author.MidX, top + AboutTitleBaseline, _dynamicTextPaint);
            _dynamicTextPaint.Color = SecondaryText;
            _dynamicTextPaint.TextSize = 13f;
            canvas.DrawText(_appTitleWithVersion.Replace("NotchPeninsula ", "NPS v"), project.MidX,
                top + AboutSubtitleBaseline, _dynamicTextPaint);
            canvas.DrawText("NotchPeninsula 开发者", author.MidX, top + AboutSubtitleBaseline, _dynamicTextPaint);
            _dynamicTextPaint.TextAlign = SKTextAlign.Left;

            for (int i = 0; i < AboutLinks.Length; i++)
                DrawSettingsButton(canvas, AboutLinkRect(i), AboutLinks[i].Label, _hoveredLinkIndex == i);
        }

        // 页签：个性化中心
        private void RenderTabPersonalize(SKCanvas canvas)
        {
            // 这三项都是自动调整的，留着只会让人以为要手动设。
            void DrawMultiCard(float yOffset, string[] subLabels, int[] indices)
            {
                float cardHeight = 12 + subLabels.Length * 34 + 2;   // 上下各 12（末行按钮底 138，卡高 150）
                var cardRect = new SKRect(RightColumnLeft, TITLE_BAR_HEIGHT + yOffset, WIDTH - CONTENT_RM, TITLE_BAR_HEIGHT + yOffset + cardHeight);
                DrawSettingsCard(canvas, cardRect);

                for (int i = 0; i < subLabels.Length; i++)
                {
                    int index = indices[i];
                    float cardBtnY = GetBtnY(index);

                    // 尺寸卡与其它卡一个样式：行标题用正文白，不再是一行灰字
                    canvas.DrawText(subLabels[i], RightColumnText, cardBtnY + 17, _uiTextPaint);

                    // 标签已是 13px 正文白（见上），这里的量宽必须用同一支画笔，否则「已生效」会压到字上
                    float afterLabelX = RightColumnText + _uiTextPaint.MeasureText(subLabels[i]) + 10;
                    if (Math.Abs(_customValues[index] - _defaultCustomValues[index]) > 0.001f)
                    {
                        var tagRect = new SKRect(afterLabelX, cardBtnY + 3, afterLabelX + 38, cardBtnY + 21);
                        _dynamicFillPaint.Color = AccentText.WithAlpha(22);
                        canvas.DrawRoundRect(tagRect, 3f, 3f, _dynamicFillPaint);
                        _dynamicTextPaint.TextSize = 10f; // 小文本样式
                        _dynamicTextPaint.Color = AccentText;
                        canvas.DrawText("已生效", afterLabelX + 4, cardBtnY + 16, _dynamicTextPaint);
                        _dynamicTextPaint.TextSize = 13f; // 还原字号，防止污染后续文字渲染
                    }

                    float cardRightX = WIDTH - CONTENT_TEXT_RM;

                    DrawSettingsButton(canvas, new SKRect(cardRightX - 175, cardBtnY, cardRightX - 145, cardBtnY + 24), "-", _hoveredMinusIndex == index);

                    // 使用静态缓存字符串，零 GC 开销
                    string valStr = _valStrCache[index];
                    float textW = _uiTextPaint.MeasureText(valStr);
                    canvas.DrawText(valStr, cardRightX - 90 - textW, cardBtnY + 17, _uiTextPaint);

                    DrawSettingsButton(canvas, new SKRect(cardRightX - 80, cardBtnY, cardRightX - 50, cardBtnY + 24), "+", _hoveredPlusIndex == index);

                    DrawSettingsButton(canvas, new SKRect(cardRightX - 40, cardBtnY, cardRightX, cardBtnY + 24), "重置", _hoveredResetIndex == index);
                }
            }

            // 绘制新增的主题卡片
            float themeY = TITLE_BAR_HEIGHT + THEME_CARD_Y;
            DrawSettingsCard(canvas, new SKRect(CONTENT_L, themeY, LeftColumnRight, themeY + THEME_CARD_H));

            canvas.DrawText("刘海 / 灵动岛主题", CONTENT_TEXT_X, themeY + 26, _uiTextPaint);
            // 「副标题」专指主标题下面这一行，统一灰色；它不属于状态说明。
            // 液态玻璃下主题恒为自动：控件置灰，副标题把「为什么不能选」说清楚，
            // 否则用户只会看到一组点不动的按钮。
            canvas.DrawText(Renderer.IslandLiquidGlass
                    ? "自动跟随玻璃背景亮度 · 不可手动修改"
                    : "可固定黑白或跟随系统主题",
                CONTENT_TEXT_X, themeY + 46, _subTextPaint);

            DrawPersonalizeDropdown(canvas, DropdownKind.Theme, enabled: !Renderer.IslandLiquidGlass);

            // 第二行：背景材质（独立选项，与主题配色互不影响）
            canvas.DrawText("背景材质", CONTENT_TEXT_X, themeY + MAT_TITLE_DY, _uiTextPaint);
            // 亚克力仍是实验特性：复用通用设置里 MSP 那枚「实验性」标签
            DrawInlineBadge(canvas, CONTENT_TEXT_X + _uiTextPaint.MeasureText("背景材质") + BADGE_GAP_X,
                themeY + MAT_TITLE_DY - TEXT_INK_MID_OFFSET, "实验性");
            canvas.DrawText("胶囊内背景材质", CONTENT_TEXT_X, themeY + MAT_SUB_DY, _subTextPaint);
            DrawPersonalizeDropdown(canvas, DropdownKind.Material);

            if (ShowOpacityCard)
            {
                float opaY = TITLE_BAR_HEIGHT + OPACITY_CARD_Y;
                DrawSettingsCard(canvas, new SKRect(CONTENT_L, opaY, LeftColumnRight, opaY + OPACITY_CARD_H));
                canvas.DrawText("背景透明度", CONTENT_TEXT_X, opaY + 29f, _uiTextPaint);
                DrawPersonalizeDropdown(canvas, DropdownKind.Opacity);
            }
            DrawMultiCard(SIZE_CARD_Y, ["全局折叠态高度", "底部圆角", "消息通知弹出高度", "视觉比例"], [3, 7, 5, 6]);

            if (Renderer.IslandLiquidGlass)
            {
                float gY = TITLE_BAR_HEIGHT + GLASS_CARD_Y;
                var gRect = new SKRect(RightColumnLeft, gY, WIDTH - CONTENT_RM, gY + GlassCardH);
                DrawSettingsCard(canvas, gRect);

                string glassHint = "桌面抓屏背板 · 可调模糊与折射";
                canvas.DrawText(glassHint, RightColumnText, gY + 26f, _subTextPaint);

                float rY = gY + GlassResetTopY;
                DrawSettingsButton(canvas, new SKRect(GLASS_RESET_X, rY, GLASS_RESET_X + GLASS_RESET_W, rY + GLASS_RESET_H),
                    "恢复默认参数", _hoveredGlassReset);

                for (int index = 0; index < GlassMaterial.Keys.Length; index++)
                    DrawGlassSlider(canvas, index);
            }
        }

        private void DrawPersonalizeDropdown(SKCanvas canvas, DropdownKind kind, bool enabled = true)
        {
            var box = PersonalizeDropdownBox(kind);
            var (options, selected) = PersonalizeDropdownOptions(kind);
            DrawDropdownBox(canvas, box.Left, box.Top, box.Width, box.Height,
                enabled ? options[selected] : "自动", _personalizeDropdownHovered == kind, enabled,
                open: _personalizeDropdownOpen == kind);
        }

        private void DrawGlassSlider(SKCanvas canvas, int index)
        {
            var track = GlassTrackBounds(index);
            int level = GlassMaterial.GetLevel(index);
            float step = track.Width / GlassMaterial.MaxLevel(index);
            bool hovered = _hoveredGlassParamIndex == index || _draggedGlassParam == index;
            float railY = track.MidY;
            float textY = railY + 4.5f;

            canvas.DrawText(GlassMaterial.Labels[index], track.Left - GLASS_LABEL_W, textY, _uiTextPaint);
            DrawSliderTrack(canvas, track.Left, track.Right, railY, track.Left + step * level, hovered);
            if (hovered && _hoveredGlassParamLevel >= 0 && _hoveredGlassParamLevel != level)
            {
                _dynamicFillPaint.Color = Overlay(80);
                canvas.DrawCircle(track.Left + step * _hoveredGlassParamLevel, railY, 4.5f, _dynamicFillPaint);
            }

            string[] values = index switch
            {
                GlassMaterial.Blur or GlassMaterial.Refraction => GlassMaterial.BlurLabels,
                GlassMaterial.CaptureRate => GlassRefreshLabels,
                GlassMaterial.FrameRate => GlassMaterial.FpsLabels,
                _ => GlassMaterial.Levels
            };
            string value = values[level];
            float valueX = track.Right + GLASS_VALUE_W - _uiTextPaint.MeasureText(value);
            canvas.DrawText(value, valueX, textY, _uiTextPaint);
        }

        private void RenderTabPlugins(SKCanvas canvas)
        {
            RefreshPluginView();

            // 插件设置子页：由行内「设置」按钮进入，仍然属于 tab 6
            if (_settingsPluginId != null)
            {
                RenderPluginSettingsPage(canvas);
                return;
            }

            // ── 顶部操作卡片 ──
            float topY = TITLE_BAR_HEIGHT + CARD_TOP_Y;
            var topRect = new SKRect(CONTENT_L, topY, WIDTH - CONTENT_RM, topY + PluginTopCardH);
            DrawSettingsCard(canvas, topRect);
            canvas.DrawText("我的插件", CONTENT_TEXT_X, topY + 26, _uiTextPaint);
            canvas.DrawText("管理已安装插件；点插件行的「设置」可配置插件参数", CONTENT_TEXT_X, topY + 46, _subTextPaint);

            void DrawPluginButton(int index, string label, float bx, float by, float bw)
            {
                bool hovered = _hoveredPluginAction == index;
                var btn = new SKRect(bx, by, bx + bw, by + 24);
                DrawSettingsButton(canvas, btn, label, hovered,
                    kind: index == 0 ? SettingsButtonKind.Primary : SettingsButtonKind.Standard);
            }

            DrawPluginButton(0, "导入插件", CONTENT_TEXT_X, topY + 60, 96);
            DrawPluginButton(1, "打开目录", CONTENT_TEXT_X + 104, topY + 60, 96);
            DrawPluginButton(2, "插件市场", CONTENT_TEXT_X + 208, topY + 60, 96);

            // ── 已安装插件列表卡片（可滚动，整卡高度）──
            GetPluginListCardTop(out float listY);
            var listRect = new SKRect(CONTENT_L, listY, WIDTH - CONTENT_RM, ContentBottom);
            DrawSettingsCard(canvas, listRect);
            canvas.DrawText($"已安装插件 ({_pluginView.Count})", CONTENT_TEXT_X, listY + 26, _uiTextPaint);

            if (!string.IsNullOrEmpty(_pluginHint))
            {
                string header = $"已安装插件 ({_pluginView.Count})";
                float hintMax = (WIDTH - CONTENT_TEXT_RM) - CONTENT_TEXT_X - _uiTextPaint.MeasureText(header) - 16;
                _subTextPaint.Color = _pluginHintIsError ? DangerText : SecondaryText;
                DrawTextWithEmoji(canvas, _pluginHint, _subTextPaint, hintMax, WIDTH - CONTENT_TEXT_RM, listY + 26, rightAlign: true);
                _subTextPaint.Color = SecondaryText;
            }

            // 条目变少时把滚动位置钳回可滚范围，避免停在一片空白上
            GetPluginListLayout(out int visibleRows, out int maxFirstRow);
            _pluginScroll = Math.Clamp(_pluginScroll, 0, maxFirstRow);

            if (_pluginView.Count == 0)
                canvas.DrawText("暂无插件，点击「导入插件」或到「插件市场」下载", CONTENT_TEXT_X, listY + 62, _subTextPaint);

            for (int slot = 0; slot < visibleRows; slot++)
            {
                int i = _pluginScroll + slot;
                var entry = _pluginView[i];
                var tile = PluginTile(listY + PluginRowsTopPad, slot);
                DrawPluginTile(canvas, tile);
                canvas.Save();
                canvas.ClipRect(tile);
                float rowY = tile.Top;
                float textX = tile.Left + PluginTextInset;
                float settingsX = tile.Right - 224f;
                float nameTextMax = settingsX - textX - 8f;
                float infoTextMax = nameTextMax;
                // ═══ 上行：插件名称（独占整行，无按钮遮挡） ═══
                _pluginTitlePaint.Color = _fgColor;
                string shownTitle = TruncateText(entry.FriendlyName, _pluginTitlePaint, nameTextMax);
                var (titleBaseline, infoBaseline) = PluginTextBaselines(tile, shownTitle);
                canvas.DrawText(shownTitle, textX, titleBaseline, _pluginTitlePaint);

                string sub = i < _pluginSubTexts.Count ? _pluginSubTexts[i] : "";
                bool subDisabled = i < _pluginSubDisabled.Count && _pluginSubDisabled[i];
                string shownSub = TruncateText(sub, _subTextPaint, infoTextMax);
                if (entry.State == PluginState.Failed && shownSub.StartsWith("加载失败", StringComparison.Ordinal))
                {
                    // 红字：这一行是弹窗关掉之后唯一还能说明「它没跑起来」的地方
                    const string tag = "加载失败";
                    _subTextPaint.Color = DangerText;
                    canvas.DrawText(tag, textX, infoBaseline, _subTextPaint);
                    _subTextPaint.Color = SecondaryText;
                    canvas.DrawText(shownSub[tag.Length..], textX + _subTextPaint.MeasureText(tag), infoBaseline, _subTextPaint);
                }
                else if (subDisabled && shownSub.StartsWith("已禁用", StringComparison.Ordinal))
                {
                    const string tag = "已禁用";
                    _subTextPaint.Color = AccentText;
                    canvas.DrawText(tag, textX, infoBaseline, _subTextPaint);
                    _subTextPaint.Color = SecondaryText;
                    canvas.DrawText(shownSub[tag.Length..], textX + _subTextPaint.MeasureText(tag), infoBaseline, _subTextPaint);
                }
                else
                {
                    _subTextPaint.Color = SecondaryText;
                    canvas.DrawText(shownSub, textX, infoBaseline, _subTextPaint);
                }

                float btnTop = PluginButtonsTop;
                const float btnH = 20f;       // 操作按钮矩形

                // 操作按钮（重载 / 卸载）+ 开关
                void DrawRowButton(float bx, bool hovered, string label, bool danger = false)
                {
                    var r = new SKRect(bx, rowY + btnTop, bx + 50, rowY + btnTop + btnH);
                    DrawSettingsButton(canvas, r, label, hovered, kind: danger
                        ? SettingsButtonKind.Danger : SettingsButtonKind.Standard);
                }
                DrawRowButton(settingsX, _hoveredPluginSettings == i, "设置");
                DrawRowButton((settingsX + 56f), _hoveredPluginReload == i, "重载", false);
                DrawRowButton((settingsX + 112f), _hoveredPluginRemove == i, "卸载", true);

                float tW = 42, tH = 20;
                float tX = (settingsX + 168f), tY = rowY + btnTop;
                var tRect = new SKRect(tX, tY, tX + tW, tY + tH);
                DrawSettingsSwitch(canvas, tRect, entry.IsEnabled, _hoveredPluginToggle == i);
                canvas.Restore();
            }

            if (visibleRows > 0 && maxFirstRow > 0)
            {
                float trackTop = listY + PluginRowsTopPad - 2f;
                float trackH = GetPluginRowsHeight(visibleRows);
                DrawScrollBar(canvas, WIDTH - 26, trackTop, trackH,
                    visibleRows / (float)_pluginView.Count, _pluginScroll / (float)maxFirstRow);
            }

            // 拖走之后不存在任何后台重绘，不会留下空转的定时器。
            if (_pluginDropHovering)
            {
                var zone = GetPluginDropZone();

                _dynamicFillPaint.Color = AccentText.WithAlpha(36);
                _dynamicStrokePaint.Color = AccentText;
                _dynamicStrokePaint.StrokeWidth = 2f;
                canvas.DrawRoundRect(zone, 8, 8, _dynamicFillPaint);
                canvas.DrawRoundRect(zone, 8, 8, _dynamicStrokePaint);
                _dynamicStrokePaint.StrokeWidth = 1.5f;   // 复位：该画笔被多处共用

                const string dropHint = "松开鼠标以导入插件（DLL / ZIP）";
                float hintW = _uiTextPaint.MeasureText(dropHint);
                float hintX = zone.MidX - hintW / 2f;
                float hintY = zone.MidY + 5f;
                _dynamicFillPaint.Color = AccentFill(false);
                canvas.DrawRoundRect(new SKRect(hintX - 14, hintY - 22, hintX + hintW + 14, hintY + 10), 6, 6, _dynamicFillPaint);
                _uiTextPaint.Color = OnAccent;
                canvas.DrawText(dropHint, hintX, hintY, _uiTextPaint);
                _uiTextPaint.Color = _fgColor;
            }
        }

        private void RenderTabMarket(SKCanvas canvas)
        {
            EnsureMarketData();
            if (_marketDataDirty) { _marketDataDirty = false; RefreshMarketFilter(); }   // UI 线程重建过滤视图

            float cardTop = TITLE_BAR_HEIGHT + CARD_TOP_Y;
            var cardRect = new SKRect(CONTENT_L, cardTop, WIDTH - CONTENT_RM, ContentBottom);
            DrawSettingsCard(canvas, cardRect);

            // 工具栏下拉框（分类 / 排序）与设置页共用同一套 DrawDropdownBox。
            var catRect = new SKRect(CONTENT_TEXT_X, MarketControlsY, CONTENT_TEXT_X + MarketCatBtnW, MarketControlsY + MarketControlH);
            DrawDropdownBox(canvas, catRect.Left, catRect.Top, MarketCatBtnW, MarketControlH,
                MarketCategoryName(_marketCategoryKey), _hoveredMarketCategoryIndex == -2,
                enabled: true, open: _marketCategoryOpen);

            var searchRect = new SKRect(MarketSearchX, MarketControlsY, MarketSearchX + MarketSearchW, MarketControlsY + MarketControlH);
            DrawControlSurface(canvas, searchRect, _marketSearchHovered, focused: _marketSearchFocused);
            float mgx = MarketSearchX + 14, mgy = MarketControlsY + 12;
            _dynamicStrokePaint.Color = Neutral(150);
            _dynamicStrokePaint.StrokeWidth = 1.5f;
            canvas.DrawCircle(mgx, mgy, 5f, _dynamicStrokePaint);
            canvas.DrawLine(mgx + 3.6f, mgy + 3.6f, mgx + 7f, mgy + 7f, _dynamicStrokePaint);
            _marketTextPaint.Color = _marketSearch.Length == 0 && _marketImeComposing.Length == 0 ? SecondaryText : _fgColor;
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
                        _dynamicFillPaint.Color = AccentText.WithAlpha(64);
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
                _marketTextPaint.Color = SecondaryText;
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
                _dynamicFillPaint.Color = AccentText;
                canvas.DrawRect(new SKRect(caretX + 1, MarketControlsY + 6, caretX + 2.4f, MarketControlsY + 20), _dynamicFillPaint);
            }


            var chkBox = new SKRect(MarketChkX, MarketChkY, MarketChkX + MarketChkBoxSize, MarketChkY + MarketChkBoxSize);
            DrawSettingsCheckBox(canvas, chkBox, _marketOnlyInstalled, _hoveredMarketChk);
            _marketTextPaint.Color = _hoveredMarketChk ? _fgColor : Neutral(190);
            canvas.DrawText(MarketChkLabel, MarketChkLabelX, MarketStatusBaseline, _marketTextPaint);

            // 只说总数，不追加「（已筛选）」：分类 / 搜索 / 「只看已安装」三个控件本身就在旁边，
            // 再挂一个后缀只会把这句话挤成省略号。
            string status = _marketFetching ? "正在加载插件市场…"
                : _marketError.Length > 0 ? _marketError
                : $"共 {_marketView.Count} 个插件";
            float statusX = MarketChkLabelX + _marketTextPaint.MeasureText(MarketChkLabel) + 18f;
            float statusMax = MarketSortX - 10f - statusX;   // 止于排序下拉左侧
            _marketTextPaint.Color = _marketError.Length > 0 && !_marketFetching ? DangerText : SecondaryText;
            canvas.DrawText(TruncateText(status, _marketTextPaint, statusMax), statusX, MarketStatusBaseline, _marketTextPaint);
            _marketTextPaint.Color = SecondaryText;
            if (!string.IsNullOrEmpty(_marketHint) && !_marketFetching)
            {
                float hintRight = MarketSortX - 10f;   // 提示右对齐到排序下拉左侧，别钻到控件底下
                float hintMax = hintRight - statusX - _marketTextPaint.MeasureText(status) - 16;
                _marketTextPaint.Color = _marketHintIsError ? DangerText : SecondaryText;
                DrawTextWithEmoji(canvas, _marketHint, _marketTextPaint, hintMax, hintRight, MarketStatusBaseline, rightAlign: true);
                _marketTextPaint.Color = SecondaryText;
            }

            var refreshRect = new SKRect(MarketRefreshX, MarketStatusRowY, MarketRefreshX + MarketRefreshW, MarketStatusRowY + MarketControlH);
            bool refreshActive = !_marketFetching;
            DrawSettingsButton(canvas, refreshRect, "刷新", _hoveredMarketRefresh, refreshActive);
            _marketTextPaint.Color = SecondaryText;

            // ── 排序下拉（最热 / 评分 / 最新 / 名称，官方插件始终置顶）──
            var sortRect = new SKRect(MarketSortX, MarketStatusRowY, MarketSortX + MarketSortW, MarketStatusRowY + MarketControlH);
            DrawDropdownBox(canvas, sortRect.Left, sortRect.Top, MarketSortW, MarketControlH,
                MarketSortOptions[_marketSortIndex], _hoveredMarketSortIndex == -2,
                enabled: true, open: _marketSortOpen);
            _subTextPaint.Color = SecondaryText;   // 下面列表区继续用这支，保持默认灰

            float rowsTop = MarketTileTop;

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
                var tile = PluginTile(rowsTop, slot);
                DrawPluginTile(canvas, tile);
                canvas.Save();
                canvas.ClipRect(tile);
                float rowY = tile.Top;
                float textX = tile.Left + PluginTextInset;
                float rightEdge = tile.Right - 10f;
                float detailX = rightEdge - MarketBtnW;
                float uninstallX = detailX - 6f - MarketBtnW;
                float installX = uninstallX - 6f - MarketBtnW;
                float titleBaseline = rowY + MarketTitleBaseline;
                float infoBaseline = rowY + MarketMetadataBaseline;
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

                string dlText = $"{mp.Downloads} 次下载";
                float dlw = _subTextPaint.MeasureText(dlText);
                float dlX = rightEdge - dlw;

                bool hasRating = mp.RatingCount > 0;
                string scoreText = hasRating ? FormatScore(mp.Rating) : "暂无评分";
                float scoreW = _subTextPaint.MeasureText(scoreText);
                float scoreX = dlX - dlGap - scoreW;

                _subTextPaint.Color = SecondaryText;
                canvas.DrawText(dlText, dlX, titleBaseline, _subTextPaint);

                if (hasRating)
                {
                    float starCx = scoreX - scoreGap - starR;
                    using var star = BuildStarPath(starCx, titleBaseline - 5f, starR, starR * 0.42f);
                    _dynamicFillPaint.Color = AccentText;
                    canvas.DrawPath(star, _dynamicFillPaint);
                    _dynamicFillPaint.Color = Overlay(15);   // 复位：该画笔多处共用
                }
                _subTextPaint.Color = SecondaryText;
                canvas.DrawText(scoreText, scoreX, titleBaseline, _subTextPaint);
                _subTextPaint.Color = SecondaryText;

                string tagOfficial = mp.Official ? "官方" : "";
                //    绝不拼成半截的「已装 v」。
                string tagInstalled = versionDiff ? $"已装 v{localVer}" : "";
                float textSpace = Math.Max(0f, installX - textX - 8f);
                float officialWidth = tagOfficial.Length > 0 ? 6f + _subTextPaint.MeasureText(tagOfficial) : 0f;
                if (textSpace < officialWidth + 24f)
                {
                    tagOfficial = "";
                    officialWidth = 0f;
                }
                float installedWidth = tagInstalled.Length > 0 ? 8f + _subTextPaint.MeasureText(tagInstalled) : 0f;
                if (textSpace < officialWidth + installedWidth + 36f)
                {
                    tagInstalled = "";
                    installedWidth = 0f;
                }
                float tagW = officialWidth + installedWidth;
                float nameMaxNow = Math.Max(0f, textSpace - tagW);

                string shownName = TruncateText(mp.Name, _pluginTitlePaint, nameMaxNow);
                _pluginTitlePaint.Color = dim ? DisabledText : _fgColor;
                canvas.DrawText(shownName, textX, titleBaseline, _pluginTitlePaint);
                float afterNameX = textX + _pluginTitlePaint.MeasureText(shownName);

                if (tagOfficial.Length > 0)
                {
                    _subTextPaint.Color = dim ? Neutral(95) : AccentText;
                    canvas.DrawText(tagOfficial, afterNameX + 6, titleBaseline, _subTextPaint);
                    afterNameX += 6 + _subTextPaint.MeasureText(tagOfficial);
                }
                if (tagInstalled.Length > 0)
                {
                    _subTextPaint.Color = dim ? Neutral(95) : AccentText;
                    canvas.DrawText(tagInstalled, afterNameX + 8, titleBaseline, _subTextPaint);
                    afterNameX += 8 + _subTextPaint.MeasureText(tagInstalled);
                }
                _subTextPaint.Color = SecondaryText;

                float infoTextMax = textSpace;
                if (busy)
                {
                    _subTextPaint.Color = AccentText;
                    canvas.DrawText(TruncateText("正在下载安装…", _subTextPaint, infoTextMax), textX, infoBaseline, _subTextPaint);
                    _subTextPaint.Color = SecondaryText;
                }
                else if (dim)
                {
                    //    同时说明这一行为什么是灰的
                    _subTextPaint.Color = Neutral(95);
                    canvas.DrawText(TruncateText("已禁用", _subTextPaint, infoTextMax), textX, infoBaseline, _subTextPaint);
                    _subTextPaint.Color = SecondaryText;
                }
                else
                {
                    _subTextPaint.Color = SecondaryText;
                    canvas.DrawText(TruncateText($"v{mp.Version} · {mp.Author}", _subTextPaint, infoTextMax),
                        textX, infoBaseline, _subTextPaint);
                }

                float btnTop = MarketButtonsTop;
                const float btnH = 20f;

                // 主操作（下载 / 更新 / 重装 / 启用）
                {
                    bool enabled = !busy;
                    bool hovered = _hoveredMarketInstall == i;
                    var r = new SKRect(installX, rowY + btnTop, installX + MarketBtnW, rowY + btnTop + btnH);
                    DrawSettingsButton(canvas, r, MarketInstallLabel(mp, local, dim), hovered, enabled,
                        local != null && dim ? SettingsButtonKind.Standard : SettingsButtonKind.Primary);
                }

                // 未安装时禁用卸载
                {
                    bool enabled = local != null && !busy;
                    bool hovered = _hoveredMarketUninstall == i;
                    var r = new SKRect(uninstallX, rowY + btnTop, uninstallX + MarketBtnW, rowY + btnTop + btnH);
                    DrawSettingsButton(canvas, r, "卸载", hovered, enabled, SettingsButtonKind.Danger);
                }

                // 详情
                {
                    bool hovered = _hoveredMarketDetail == i;
                    var r = new SKRect(detailX, rowY + btnTop, detailX + MarketBtnW, rowY + btnTop + btnH);
                    DrawSettingsButton(canvas, r, "详情", hovered);
                }
                canvas.Restore();
            }

            if (mVisibleRows > 0 && mMaxFirst > 0)
            {
                float trackTop = rowsTop - 2f;
                float trackH = GetPluginRowsHeight(mVisibleRows);
                DrawScrollBar(canvas, WIDTH - 26, trackTop, trackH,
                    mVisibleRows / (float)_marketView.Count, _marketScroll / (float)mMaxFirst);
            }

            // ── 一键更新全部：有可更新插件才出现（列表高度已在 GetMarketListLayout 让位）──
            if (MarketUpdateCount > 0)
            {
                var bar = MarketUpdateBarRect;
                bool running = _updateAllRunning;
                bool enabled = !running && _marketBusyId.Length == 0;
                DrawSettingsButton(canvas, bar, running ? UpdateAllProgressText : $"一键更新全部插件（{MarketUpdateCount}）",
                    _hoveredMarketUpdateAll, enabled, SettingsButtonKind.Primary);
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
            else if (_marketDialog == MarketDialog.MinVersion)
            {
                DrawMinVersionDialog(canvas);
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
                DrawControlSurface(canvas, close, hovered: true);
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
                case MarketDialog.Rate: DrawDialogRate(canvas, rect, bodyY); break;
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
            _subTextPaint.Color = SecondaryText;
            DrawTextWithEmoji(canvas, TruncateText(meta, _subTextPaint, innerW), _subTextPaint, innerW,
                rect.Left + DialogPad, y + 12);
            _subTextPaint.Color = SecondaryText;
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
                var local = MatchLocalPlugin(mp);
            bool disabled = local != null && PluginManager.Instance.IsDisabled(local);
            bool busy = string.Equals(_marketBusyId, mp.Id, StringComparison.Ordinal);
            bool actionable = local == null || disabled;
            string label = local == null ? (busy ? "安装中…" : "安装")
                : disabled ? "启用" : "已安装";

            var button = GetDialogButtonRect(rect, 0, 1);
            bool hovered = actionable && !busy && _hoveredDialogButton == 0;
            DrawSettingsButton(canvas, button, label, hovered, actionable && !busy, SettingsButtonKind.Primary);
        }

        // 评分正文：平均分行 → 五颗星（可点半星）→ 状态行
        private void DrawDialogRate(SKCanvas canvas, SKRect rect, float y)
        {
            string summary = _rateCount > 0 ? $"{FormatScore(_rateAverage)} 分 · {_rateCount} 人评分" : "还没有人评分";
            if (_rateLoading) summary = "正在读取…";
            _subTextPaint.Color = SecondaryText;
            canvas.DrawText(summary, rect.Left + DialogPad, y + 12, _subTextPaint);
            _subTextPaint.Color = SecondaryText;

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
                    ? (_rateStatusIsError ? DangerText : SecondaryText)
                    : Neutral(160);
                DrawTextWithEmoji(canvas, TruncateText(status, _subTextPaint, DialogInnerW), _subTextPaint,
                    DialogInnerW, rect.Left + DialogPad, sy + 12);
                _subTextPaint.Color = SecondaryText;
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
                    _dynamicFillPaint.Color = AccentText;
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
        private void DrawDialogButton(SKCanvas canvas, SKRect rect, string label, int index,
            SettingsButtonKind kind = SettingsButtonKind.Standard)
            => DrawSettingsButton(canvas, rect, label, _hoveredDialogButton == index, kind: kind);

        /// <summary>提示弹窗的富文本正文：逐行逐段画，群号单独上色。</summary>
        private void DrawNoticeBody(SKCanvas canvas, SKRect rect, float bodyY)
        {
            float left = rect.Left + DialogPad;

            // 折行位置是打开弹窗时算好的，这里只按结果摆，别再自己折一遍（否则链接的命中区会跟画面错开）。
            for (int i = 0; i < _loadFailedLines.Count; i++)
            {
                float lineY = bodyY + i * DialogLineH;
                float x = left;
                foreach (var (text, isLink) in _loadFailedLines[i])
                {
                    _subTextPaint.Color = isLink
                        ? (_loadFailedLinkHovered ? AccentText : AccentFill(false))
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
                _dynamicFillPaint.Color = AccentText;
                canvas.DrawRect(new SKRect(x0, y, x0 + _loadFailedLinkWidth, y + 1f), _dynamicFillPaint);
            }

            _subTextPaint.Color = SecondaryText;
        }

        private void DrawLoadFailedDialog(SKCanvas canvas)
        {
            var rect = GetNoticeDialogRect();
            canvas.DrawRoundRect(rect, 8, 8, _menuBg);
            canvas.DrawRoundRect(rect, 8, 8, _menuBorder);

            float bodyY = DrawDialogHeader(canvas, rect, "插件加载失败") + LoadFailedBodyDY;
            DrawNoticeBody(canvas, rect, bodyY);
            DrawDialogButton(canvas, GetDialogButtonRect(rect, 0, 1), "好的", 0, SettingsButtonKind.Primary);
        }

        private void DrawMinVersionDialog(SKCanvas canvas)
        {
            var rect = GetNoticeDialogRect();
            canvas.DrawRoundRect(rect, 8, 8, _menuBg);
            canvas.DrawRoundRect(rect, 8, 8, _menuBorder);

            float bodyY = DrawDialogHeader(canvas, rect, "NPS 版本过低") + LoadFailedBodyDY;
            DrawNoticeBody(canvas, rect, bodyY);

            // 用户指定：取消在左（绿）、继续在右（黄）。继续是主操作，放最右。
            DrawDialogButton(canvas, GetDialogButtonRect(rect, 0, 2), "取消", 0);
            DrawDialogButton(canvas, GetDialogButtonRect(rect, 1, 2), "继续", 1, SettingsButtonKind.Primary);
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
                _subTextPaint.Color = SecondaryText;
                canvas.DrawText(TruncateText("插件文件会移入回收站，之后可以重新导入。", _subTextPaint, DialogInnerW),
                    rect.Left + DialogPad, bodyY + 12 + DialogLineH, _subTextPaint);
                _subTextPaint.Color = SecondaryText;

                DrawDialogButton(canvas, GetDialogButtonRect(rect, 0, 1), "卸载", 0, SettingsButtonKind.Danger);
                return;
            }

            _subTextPaint.Color = Neutral(200);
            canvas.DrawText(TruncateText("这个插件登记了它创建的数据目录：", _subTextPaint, DialogInnerW),
                rect.Left + DialogPad, bodyY + 12, _subTextPaint);
            _subTextPaint.Color = SecondaryText;
            canvas.DrawText(TruncateText(_uninstallDir, _subTextPaint, DialogInnerW),
                rect.Left + DialogPad, bodyY + 12 + DialogLineH, _subTextPaint);
            canvas.DrawText(TruncateText("删除后无法恢复；点「保留」只卸载插件。", _subTextPaint, DialogInnerW),
                rect.Left + DialogPad, bodyY + 12 + DialogLineH * 2, _subTextPaint);
            _subTextPaint.Color = SecondaryText;

            DrawDialogButton(canvas, GetDialogButtonRect(rect, 0, 3), "打开目录", 0);
            DrawDialogButton(canvas, GetDialogButtonRect(rect, 1, 3), "保留", 1);
            DrawDialogButton(canvas, GetDialogButtonRect(rect, 2, 3), "删除", 2, SettingsButtonKind.Danger);
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
                ? Math.Max(1, (int)((availFrom - TITLE_BAR_HEIGHT - 12 - DROPDOWN_PAD_Y * 2f) / DROPDOWN_ROW_H))
                : Math.Max(1, (int)((ContentBottom - availFrom - DROPDOWN_PAD_Y * 2f) / DROPDOWN_ROW_H));

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

            // 上下各留一点内边距：第一行和最后一行不再贴着面板边缘。
            float listH = DropdownHeight(visible);
            float dY = upward ? availFrom - listH : availFrom;
            var dRect = new SKRect(x, dY, x + w, dY + listH);
            canvas.DrawRoundRect(dRect, 8, 8, _menuBg);
            canvas.DrawRoundRect(dRect, 8, 8, _menuBorder);

            if (total == 0)
            {
                _dynamicTextPaint.Color = SecondaryText;
                canvas.DrawText(emptyText!, x + 12, dY + DROPDOWN_PAD_Y + 18, _dynamicTextPaint);
                _dynamicTextPaint.Color = _fgColor;
                return;
            }

            int maxFirst = Math.Max(0, total - visible);
            int first = Math.Clamp(scrollFirst, 0, maxFirst);

            for (int row = 0; row < visible; row++)
            {
                int i = first + row;
                float rowTop = dY + DROPDOWN_PAD_Y + row * DROPDOWN_ROW_H;
                bool selected = i == selectedIndex;

                var rowRect = new SKRect(x + 4, rowTop + 1, x + w - 4, rowTop + DROPDOWN_ROW_H - 1);
                bool hovered = hoveredIndex == i && i != dimmedIndex;
                if (hovered || selected)
                {
                    _dynamicFillPaint.Color = selected
                        ? AccentText.WithAlpha(hovered ? (byte)36 : (byte)23)
                        : Overlay(SettingsControlPressed(hovered) ? (byte)31 : (byte)19);
                    canvas.DrawRoundRect(rowRect, SettingsControlRadius, SettingsControlRadius, _dynamicFillPaint);
                }

                // 文字统一留出左侧蓝条的位置，选中与否都不左右跳
                float textX = x + 18f;
                if (selected)
                {
                    _dynamicFillPaint.Color = AccentText;
                    canvas.DrawRoundRect(new SKRect(x + 6f, rowTop + 7f, x + 8.5f, rowTop + DROPDOWN_ROW_H - 7f),
                        1.5f, 1.5f, _dynamicFillPaint);
                }

                bool builtin = builtinFlags != null && i < builtinFlags.Length && builtinFlags[i];
                float tagW = builtin ? _subTextPaint.MeasureText("内置") + 14f : 0f;
                float nameMaxW = x + w - 12f - textX - (tagW > 0f ? tagW + 8f : 0f);
                string shown = TruncateText(options[i], _dynamicTextPaint, nameMaxW);
                float nameW = _dynamicTextPaint.MeasureText(shown);
                _dynamicTextPaint.Color = i == dimmedIndex ? DisabledText : selected ? AccentText : _fgColor;
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
            if (_selectedTab == 5 && _personalizeDropdownOpen != DropdownKind.None)
            {
                var box = PersonalizeDropdownBox(_personalizeDropdownOpen);
                var (options, selected) = PersonalizeDropdownOptions(_personalizeDropdownOpen);
                RenderDropdownList(canvas, box.Left, box.Bottom + 2f, box.Width,
                    options, selected, _hoveredPersonalizeOption, dimmedIndex: -1);
            }

            if (_selectedTab == 2 && _dropdownOpen)
            {
                RenderDropdownList(canvas, PlatformBoxX, PlatformMenuY, 110,
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
                GetVolumeMenuLayout(out float menuTop, out int volVisible, out bool upward);
                RenderDropdownList(canvas, SOUND_VOL_X, upward ? TITLE_BAR_HEIGHT + SOUND_BOX_Y : menuTop,
                    SOUND_VOL_W, VolumeOptionLabels, ToastSoundConfig.VolumeIndex, _hoveredSoundVolumeIndex,
                    dimmedIndex: -1, upward: upward, visibleRowsOverride: volVisible);
            }

            if (_selectedTab == 1 && _monitorDropdownOpen)
            {
                float dX = DISPLAY_CTRL_RIGHT - MONITOR_DD_W;
                float dY = TITLE_BAR_HEIGHT + MONITOR_ROW_Y + (ROW_H - MONITOR_DD_H) / 2f + MONITOR_DD_H + 2f - _displayPageScroll;
                RenderDropdownList(canvas, dX, dY, MONITOR_DD_W, _monitorOptions,
                    Renderer.TargetMonitorIndex, _hoveredMonitorDropdownIndex, dimmedIndex: -1);
            }

            if (_selectedTab == 1 && _displayModeDropdownOpen)
            {
                var box = DisplayModeDropdownBox;
                RenderDropdownList(canvas, box.Left, box.Bottom + 2f, box.Width,
                    DisplayModeLabels, (int)Renderer.DisplayMode, _hoveredDisplayModeIndex, dimmedIndex: -1);
            }

            // 待机显示内容下拉（显示设置）：放最后画，要盖住下面的切换行和显示内容列表
            if (_selectedTab == 1 && _sceneDropdownOpen) RenderSceneDropdownList(canvas);
        }
    }
}
