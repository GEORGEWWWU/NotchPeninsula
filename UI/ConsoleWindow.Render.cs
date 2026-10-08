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
        //  设置窗口 —— 绘制部分（由 Render() 拆出）
        //  局部函数已提升为实例方法，统一把 canvas 作为第一个参数。
        //  每个页签一个 RenderTabXxx，只依赖 canvas 与实例字段。
        // ----  ----

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

        // 画整张卡片底 + 一行开关内容。
        //
        // 版式统一：一张卡片就是「标题 + 一行副标题 + 右侧开关」，不允许再往下叠第三行小字。
        //    需要补充说明时把话压进副标题，或者写进 README —— 卡片里多出一层子标题会跟其他开关不一致。
        private void DrawToggleCard(SKCanvas canvas, float yOffset, string title, string sub, bool state, bool hovered, bool disabled = false)
        {
            var cardRect = new SKRect(CONTENT_L, TITLE_BAR_HEIGHT + yOffset, WIDTH - CONTENT_RM, TITLE_BAR_HEIGHT + yOffset + 62);
            canvas.DrawRoundRect(cardRect, 6, 6, _cardBg);
            canvas.DrawRoundRect(cardRect, 6, 6, _cardBorder);
            DrawToggleRow(canvas, yOffset, title, sub, state, hovered, disabled);
        }

        // 只画「一行开关」的内容（标题 / 副标题 / 右侧开关），不画卡片底。
        // 拆出来是为了让「自动隐藏」那张卡片能在同一个卡片底里放多行（总开关 + 三个模式开关）。
        //
        // 纵向偏移必须走全页统一的常量，按本行有几行文字分别取基线：
        //    有副标题（两行）→ 标题基线 = yOffset + ROW_TEXT_BASELINE（= 行首 + 25.5），
        //                      副标题 = 标题 + ROW_SUB_OFFSET（= 行首 + 45.5）；
        //                      两行墨迹的整体中心 = 行首 + 30 = 行内锚点。
        //    无副标题（单行）→ 标题基线 = yOffset + ROW_TEXT_BASELINE_SINGLE（= 行首 + 35.5），
        //                      墨迹中线 = 行首 + 30 = 行内锚点。
        //    开关轨道 = yOffset + ROW_ANCHOR_Y ± TOGGLE_TRACK_H/2（中心 = 行首 + 30）。
        //    以前这里对两类行用同一个偏移，才会出现「两行文字整块往下掉 10px」。
        private void DrawToggleRow(SKCanvas canvas, float yOffset, string title, string sub, bool state, bool hovered, bool disabled = false)
        {
            // 有副标题 → 对齐「两行文字块的中线」；没有 → 对齐「这一行文字的中线」。
            // 两者都等于把文字块与右侧控件做成同心，卡片/行高变化时自动跟着走。
            float titleBaseline = TITLE_BAR_HEIGHT + yOffset
                + (sub.Length > 0 ? ROW_TEXT_BASELINE : ROW_TEXT_BASELINE_SINGLE);

            _uiTextPaint.Color = disabled ? Neutral(100) : _fgColor;
            canvas.DrawText(title, CONTENT_TEXT_X, titleBaseline, _uiTextPaint);
            _uiTextPaint.Color = _fgColor;

            // sub 为空时整行只有标题 + 开关（如「消息提示音」平时不写副标题）
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

        /// <summary>
        /// 分段选择器（一排小胶囊按钮）：显示模式的「待机 / 普通」与待机显示内容的「时间 / 空白 / 媒体控制」都用它。
        /// x / w 由调用方给（本页右对齐），段宽 = w / 段数 —— 命中侧（WndProc 的 tab 1 段）按同一算式取段号。
        /// </summary>
        private void DrawSegmented(SKCanvas canvas, float x, float y, float w, float h,
            string[] labels, int selected, int hovered)
        {
            int n = labels.Length;
            float segW = w / n;

            _dynamicFillPaint.Color = Overlay(8);
            canvas.DrawRoundRect(new SKRect(x, y, x + w, y + h), 6, 6, _dynamicFillPaint);

            for (int i = 0; i < n; i++)
            {
                // 段之间留 2px 缝（两端不留）：不靠缝也能看出分界，但有缝更像一组按钮而不是一块色板。
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

        /// <summary>
        /// 画一条滚动条（3px 轨道 + 滑块）。窗口里所有列表 / 整页滚动条都走这里，
        /// 「默认隐藏、滚动才显形」的透明度只在这一处生效（见 ScrollBarAlpha）。
        /// thumbRatio = 滑块占轨道高的比例，posRatio = 滑块位置比例（0 = 顶）。
        /// </summary>
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
        // 悬停是离散状态（指针在这一行 / 不在），底色硬切会闪；这里给每行一个 0→1 的进度，
        // 由窗口定时器逐拍逼近目标值，渲染时按进度算底色透明度 —— 进出都是淡入淡出。
        // 与托盘菜单同一套做法：定时器只在动画进行时存在，跑完就 KillTimer，不空转。

        /// <summary>开表。已在跑、或窗口还没建好时什么都不做。</summary>
        private void StartDisplayHoverAnim()
        {
            if (_displayHoverTimerOn || _hwnd == IntPtr.Zero) return;
            if (Win32.SetTimer(_hwnd, DISPLAY_HOVER_TIMER_ID, DISPLAY_HOVER_TICK_MS, IntPtr.Zero) == IntPtr.Zero) return;
            _displayHoverTimerOn = true;
        }

        /// <summary>停表。</summary>
        private void StopDisplayHoverAnim(IntPtr hwnd)
        {
            if (!_displayHoverTimerOn) return;
            Win32.KillTimer(hwnd, DISPLAY_HOVER_TIMER_ID);
            _displayHoverTimerOn = false;
        }

        /// <summary>推进一拍动画并重绘；返回是否还有行没到位（true = 继续跑表）。</summary>
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

            // 个性化中心的蓝色提示：同一张表、同一套缓动（约 0.2s 淡入 / 淡出）
            for (int i = 0; i < _hintAnim.Length; i++)
            {
                float target = i == _hintRow ? 1f : 0f;
                float cur = _hintAnim[i];
                if (Math.Abs(target - cur) <= 0.01f) { _hintAnim[i] = target; continue; }

                _hintAnim[i] = cur + (target - cur) * DISPLAY_HOVER_EASE;
                animating = true;
            }

            // 无条件重绘：最后那一拍会把进度吸附到目标值，这一帧必须画出来，
            // 否则会停在 0.99 那种「差一点点」的状态上。
            Render();
            return animating;
        }

        /// <summary>某一行的悬停进度（0 ~ 1）；越界返回 0。</summary>
        private float GetDisplayHoverProgress(int row)
            => row >= 0 && row < _displayHoverAnim.Length ? _displayHoverAnim[row] : 0f;

        /// <summary>
        /// 条目右端「把这一项往上 / 往下挪」的箭头：一根 11px 短竖杆 + 顶端两笔斜头（圆头描边），
        /// 就是常见的 SVG ↑ / ↓ 图标那种样子 —— 不用实心三角（太重），也不用 ∧ ∨ 字符
        ///（字符在不同字体下的墨迹高度与基线都不一样，纵向根本对不齐）。
        /// slotX 是 18px 点击槽左边界（渲染与命中同源，见 DISPLAY_MOVE_UP_X / DOWN_X），
        /// cy 是条目垂直中心；up=false 时画朝下的。三支画笔是进程级复用的静态对象，零分配。
        /// </summary>
        private void DrawMoveArrow(SKCanvas canvas, float slotX, float cy, bool hovered, bool enabled, bool up)
        {
            var stroke = !enabled ? _sortArrowDisabledStroke
                : hovered ? _sortArrowHoverStroke
                : _sortArrowStroke;

            float cx = slotX + SORT_TRI_W / 2f;
            if (hovered && enabled)
            {
                // 悬停给一块小圆角底：让它读起来是「一颗按钮」，而不是飘在行尾的两个符号
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

        /// <summary>
        /// 「显示内容」列表里的单个条目：左侧复选框 + 名称（内置模块跟一枚「内置」胶囊）+ 右端一对上下箭头。
        /// **常态不画背景**（只有悬停时按 hoverP 淡入一层底）—— 列表外面已经没有容器，
        /// 条目不 hover 时完全融进页面，靠 4px 行距分隔，不画分割线。
        /// enabled=false（待机模式）时整条退成灰色、箭头走禁用画笔。
        /// 所有 x 都从 DISPLAY_ITEM_L / DISPLAY_ITEM_R / DISPLAY_MOVE_* 推导，命中侧（WndProc）同源。
        /// </summary>
        private void DrawDisplayItem(SKCanvas canvas, float y, string name, bool isBuiltin, bool isShown, float hoverP,
            bool enabled, bool hoverUp, bool hoverDown, bool canUp, bool canDown)
        {
            // 底色只在悬停时才有（由动画进度淡入淡出）：常态让 item 与整页融为一体，不 hover 就没有背景。
            // 整栏置灰（待机模式）时 hoverP 恒为 0，也就永远不画底。
            if (enabled && hoverP > 0.01f)
            {
                _dynamicFillPaint.Color = Overlay((byte)(28 * hoverP));
                canvas.DrawRoundRect(new SKRect(DISPLAY_ITEM_L, y, DISPLAY_ITEM_R, y + DISPLAY_ITEM_H), 6, 6, _dynamicFillPaint);
            }

            // 复选框 16×16：勾选 = 蓝底白勾，未勾 = 空心描边（置灰时整套退成灰）
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

            // 名称：没勾上的条目整行压暗（它当前不上岛），但保持可读；整栏置灰时一律用禁用色
            const string tag = "内置";
            float tagW = isBuiltin ? _subTextPaint.MeasureText(tag) + 14f : 0f;
            float textX = box.Right + 10f;
            string shownName = TruncateText(name, _uiTextPaint, DISPLAY_MOVE_UP_X - 12f - textX - tagW);
            _uiTextPaint.Color = !enabled ? Neutral(100) : isShown ? _fgColor : Neutral(150);
            canvas.DrawText(shownName, textX, y + 20.5f, _uiTextPaint);
            _uiTextPaint.Color = _fgColor;

            // 「内置」胶囊：内置模块与第三方插件一眼分得开，比裸文字更整齐
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

        // 条目复选框里的白勾：永远白色（压在蓝底上），不参与明暗重绑 —— 与 _iconPaint 的用法同理。
        private static readonly SKPaint _displayTickPaint = new()
        {
            Color = SKColors.White,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 1.7f,
            StrokeCap = SKStrokeCap.Round,
            StrokeJoin = SKStrokeJoin.Round,
            IsAntialias = true
        };

        // 三支箭头描边（静止 / 悬停 / 置灰）。颜色由调用方按状态挑，画完不用还原 ——
        // 它们只在这里被画，且每支的颜色是固定的，不存在「临时改色忘了复位」的风险
        //（这一点与本项目其它共享画笔不同：那些是「同一支画笔被切色复用」，必须复位）。
        //
        // 这里的初值只是深色外观下的兜底色 —— 真正的颜色在 ApplyAppearance() 里
        // 用 Neutral() / _fgColor 按明暗重绑（那是全窗口唯一的画笔重绑点）。
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
            // 个性化中心最上，两条分割线
            DrawTab(canvas, 5, "个性化中心", 10);
            canvas.DrawLine(20, TITLE_BAR_HEIGHT + 52, 160, TITLE_BAR_HEIGHT + 52, _separatorPaint);
            DrawTab(canvas, 0, "通用设置", 60);
            DrawTab(canvas, 1, "显示设置", 100);
            DrawTab(canvas, 2, "媒体设置", 140);
            DrawTab(canvas, 3, "交互设置", 180);
            canvas.DrawLine(20, TITLE_BAR_HEIGHT + 222, 160, TITLE_BAR_HEIGHT + 222, _separatorPaint);
            // 「我的插件 / 插件市场」是同一组（插件相关），中间不插分割线 ——
            //    分割线只在大组之间出现：设置组 | 插件组 | 关于。
            //    间距必须与上面那组的节奏完全一致：行高 36 + 6 = 分割线，分隔线后 8 起下一行。
            DrawTab(canvas, 6, "我的插件", 230);
            DrawTab(canvas, 7, "插件市场", 270);
            canvas.DrawLine(20, TITLE_BAR_HEIGHT + 312, 160, TITLE_BAR_HEIGHT + 312, _separatorPaint);
            DrawTab(canvas, 4, "关于软件", 320);
        }

        // 页签：通用设置
        private void RenderTabGeneral(SKCanvas canvas)
        {
            DrawToggleCard(canvas, 12, "开机自启", "跟随系统启动自动运行该程序", _isAutoStartEnabled, _toggleHovered);
            // 窗口置顶（与下方消息通知整组互换位置）
            DrawToggleCard(canvas, 84, "窗口置顶", "开启后刘海将始终保持在其他窗口最上层", NotchWindow.IsTopmostEnabled, _topmostToggleHovered);
            // 「系统消息通知」—— 一张四行卡（总开关 + 三个附属设置行），yOffset 156..TOAST_CARD_BOTTOM = 404
            //    行距恒为 62（与全页所有单行卡同节奏），每行共用同一个行内锚点 ROW_ANCHOR_Y = 30：
            //      · 左侧文字墨迹中线 = 行首 + 30   （= 基线 行首 + 26，13px 字号墨迹中线在基线之上 5.5）
            //      · 右侧控件中心     = 行首 + 30   （开关轨道 +20..+40；下拉框高 32 则框顶 = 行首 + 14）
            //    行1 +156「系统消息通知」开关（标题基线 +26 / 副标题 +46 / 开关轨 +176..+196）
            //    分隔线 +TOAST_SEP_Y (222)
            //    行2 +218「消息通知内容」（标题基线 +26 / 描述 +46 / 下拉框 +232..+264）
            //    分隔线 +SOUND_SEP_Y (284)
            //    行3 +280「消息提示音」开关（标题基线 +26 / 副标题 +46 / 开关轨 +300..+320）
            //    行4 +342「提示音」下拉 + 音量下拉 + [试听][重置]（标签基线 +26 / 控件 +356..+388）
            //
            //    提示音是通知的附属设置，所以和通知同卡不同行 —— 拆成两张独立卡会让层级关系丢失。
            //       「消息通知内容」与「消息提示音」都是「系统消息通知」的子项，只是前者管内容、后者管声音。
            //    纵向对齐的唯一口径是行内锚点，绝不要拿「标签 vs 框内文字」当口径 ——
            //       框内文字本身在框里偏下，跟着它走会连带把标签拖偏（返工三轮的根因）。
            //    卡片下沿必须贴合内容：行 4 控件底 388，卡片底 404，留 16px。
            //    改这里的数值时必须同步改 WM_MOUSEMOVE 的 tab 0 段与 RenderDropdowns 的浮层锚点。
            var notifyCardRect = new SKRect(CONTENT_L, TITLE_BAR_HEIGHT + TOAST_ROW1_Y, WIDTH - CONTENT_RM, TITLE_BAR_HEIGHT + TOAST_CARD_BOTTOM);
            canvas.DrawRoundRect(notifyCardRect, 6, 6, _cardBg);
            canvas.DrawRoundRect(notifyCardRect, 6, 6, _cardBorder);

            // ── 行 1：总开关 ──
            DrawToggleRow(canvas, TOAST_ROW1_Y, "系统消息通知", "允许在刘海中显示Windows系统的Toast消息", NotchWindow.IsToastEnabled, _toastToggleHovered);

            canvas.DrawLine(CONTENT_TEXT_X, TITLE_BAR_HEIGHT + TOAST_SEP_Y, WIDTH - CONTENT_TEXT_RM, TITLE_BAR_HEIGHT + TOAST_SEP_Y, _separatorPaint);

            // ── 行 2：消息通知内容下拉（完整时展示应用名并拉大通知尺寸）──
            // 下拉框占 +230..+262，与行 1 的开关轨道（+176..+196 相对 +156 = +20..+40）同一位；
            // 标题 / 描述就是单行卡那套 +26 / +46，与整页其它行完全对齐。
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
            // 副标题保留，但不写括号里的实现细节（「（默认关闭，不吃任何内存）」不该出现在界面上）；
            // 超限的音频会在选中时通过红色副标题给出具体原因（见 _soundHint）。
            DrawToggleRow(canvas, SOUND_ROW3_Y, "消息提示音",
                _soundHint.Length > 0 ? _soundHint : "新消息到达时播放提示音",
                ToastSoundConfig.IsEnabled, _soundToggleHovered);

            // ── 行 4：提示音设置（行 3 的附属，无开关）──
            // 左起标签「提示音」，右起「提示音」下拉 + 音量下拉 + [试听][重置]。
            // 这一行是全页唯一 4 控件并排的行（下拉+音量+两按钮 = 340px），内容区只有 348px，
            //    所以左侧只放一个短标签、不放描述文字 —— 放不下会叠到下拉框上。
            //    「音量」下拉的可用性由 ToastSoundConfig.IsSourceReady 给出（父开关 + 有具体音源），
            //    与命中侧 / 点击侧同源，不要再在这里手写 `SelectedIndex > 0`。
            //    第 4 行整行是「消息提示音」开关的附属：父开关关掉时整行置灰、不吃指针
            //       （就是「父开关 → 附属行」的那套通用约定，面板上另一处例子是 tab 3 的自动隐藏卡片）。
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
                // 蓝底白字：浅色外观下 _fgColor 是纯黑，压在蓝底上几乎看不清（深色外观下本来就是白，
                // 所以这里写死白色对两套外观都正确）。置灰态走灰色文字 + 极淡底，不参与这条规则。
                _uiTextPaint.Color = enabled ? SKColors.White : Neutral(110);
                canvas.DrawText(label, bx + (bw - tw) / 2f, TITLE_BAR_HEIGHT + SOUND_BTN_Y + 17, _uiTextPaint);
                // 恢复色必须是 _uiTextPaint 的基准色 _fgColor，不能写 (240,240,240)：
                //    这一行之后还要画「剪贴板链接检测」「切换灵动岛字体」两张卡的标题，
                //    残留的 240 会把它们一起压暗（置灰态下必现）。
                _uiTextPaint.Color = _fgColor;
            }

            DrawSoundButton(_soundPreviewHovered, "试听", SOUND_PREVIEW_X, SOUND_BTN_W, soundReady);
            DrawSoundButton(_soundResetHovered, "重置", SOUND_RESET_X, SOUND_BTN_W, soundReady);

            // 说明文字已移除：这一行本来写「仅接受 ≤N 秒、≤N MB 的音频」，
            // 但它是实现细节，不该出现在设置界面上打扰用户 —— 超限的音频在选中时
            // 会通过开关行的红色副标题给出具体原因（见 _soundHint）。

            // 剪贴板链接检测（从「交互设置」搬来 —— 它是个功能开关，不属于交互行为）
            // 行首由 CLIPBOARD_CARD_Y 派生 = 通知卡底 + 10，通知卡长高时自动跟着走
            DrawToggleCard(canvas, CLIPBOARD_CARD_Y, "剪贴板链接检测", "复制链接时在刘海中显示，可一键在默认浏览器打开", NotchWindow.IsClipboardEnabled, _clipboardToggleHovered);

            // 切换灵动岛字体：选中字体文件后立即热替换岛内全部文本字体（默认系统字体，不做任何改动）
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

        /// <summary>
        /// 通用设置页的窄版下拉框。位置与宽度全部由调用方传入，热区直接复用同一组常数，
        /// 不会再出现「渲染在一处、命中在另一处」的错位。
        /// 禁用态（ = false）会整体降低不透明度并画成灰色，
        /// 与命中侧的置灰判据必须同源。
        /// </summary>
        private void DrawDropdownBox(SKCanvas canvas, float x, float yOffset, float w, float h,
                                     string text, bool hovered, bool enabled)
        {
            var rect = new SKRect(x, TITLE_BAR_HEIGHT + yOffset, x + w, TITLE_BAR_HEIGHT + yOffset + h);
            float bgAlpha = enabled ? (hovered ? 15 : 8) : 4;
            _dynamicFillPaint.Color = Overlay((byte)bgAlpha);
            canvas.DrawRoundRect(rect, 4, 4, _dynamicFillPaint);

            if (!enabled) _uiTextPaint.Color = Neutral(110);
            canvas.DrawText(TruncateText(text, _uiTextPaint, w - 26), rect.Left + 10, rect.Top + h / 2f + 5, _uiTextPaint);
            // 恢复基准色 _fgColor（不是 240）—— 本方法后面还要画同帧的其它卡片标题，
            //    残留色会把它们一起压暗。当前唯一会传 enabled:false 的调用方是提示音行。
            if (!enabled) _uiTextPaint.Color = _fgColor;

            float cx = rect.Right - 18;
            float cy = rect.MidY;
            if (!enabled) _chevronPaint.Color = Neutral(90);
            canvas.DrawLine(cx, cy - 2.5f, cx + 5, cy + 2.5f, _chevronPaint);
            canvas.DrawLine(cx + 5, cy + 2.5f, cx + 10, cy - 2.5f, _chevronPaint);
            if (!enabled) _chevronPaint.Color = Neutral(160);
        }

        // 页签：显示设置
        // 版式：一屏放下（整页不再滚动）。只保留「显示形态」一张大卡片，其余四块是不带容器的单行，
        // 控件类型刻意错开（下拉框 / 分段器 / 分段器 / 开关），不是一个样式抄四遍。
        private void RenderTabDisplay(SKCanvas canvas)
        {
            // 整页滚动偏移：内容已压进一屏（GetDisplayPageMaxScroll = 0），这里恒为 0；
            // 保留偏移项是为了以后内容变高时不必再改一遍坐标（命中侧同源，见 WndProc 的 tab 1 段）
            float page = -_displayPageScroll;

            // 显示形态（唯一保留的大卡片：两个形态选项就是它的内容，不再另写标题行）
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

                // 颜色直接同步真实的明暗逻辑，并完美兼容“跟随系统”模式。
                // 「跟随系统」走进程级缓存（见 Renderer.SystemIsLightTheme）——
                // 本方法每次渲染显示设置页都会执行，且页面上每个胶囊示意图各调一次
                //（显示形态 2 + 显示模式 2 + 待机场景 4 = 每帧 8 次），绝不能在这里现读注册表。
                _dynamicFillPaint.Color = IsLightPreviewCapsule() ? SKColors.White : SKColors.Black;

                if (index == 0) // 调整经典刘海的矢量绘图比例，使其视觉高度和灵动岛保持一致
                {
                    // using：SKPath 持有 Skia 原生对象，本方法每次渲染显示设置页都会调用，
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

            // ── 目标显示器（单行：标签 + 右侧下拉框，不套容器）──
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
            // 高亮的是当前真实状态：点「待机模式」岛上立刻收拢、点「普通模式」立刻展开
            //（Renderer.StandbyActive，写注册表 StandbyActive，重启后保持）。
            float modeRowY = TITLE_BAR_HEIGHT + MODE_ROW_Y + page;
            canvas.DrawText("显示模式", CONTENT_TEXT_X, modeRowY + ROW_LABEL_DY, _uiTextPaint);
            DrawSegmented(canvas, MODE_SEG_X, modeRowY + (ROW_H - SEG_H) / 2f, MODE_SEG_W, SEG_H,
                ["待机模式", "普通模式"], Renderer.StandbyActive ? 0 : 1, _hoveredDisplayModeIndex);

            // ── 待机显示内容（单行分段器）：进入待机后岛上显示什么 ──
            // 复用现成的时钟模块与折叠媒体模块（见 Renderer.StandbyScene）。
            float sceneRowY = TITLE_BAR_HEIGHT + SCENE_ROW_Y + page;
            canvas.DrawText("待机显示内容", CONTENT_TEXT_X, sceneRowY + ROW_LABEL_DY, _uiTextPaint);
            DrawSegmented(canvas, SCENE_SEG_X, sceneRowY + (ROW_H - SEG_H) / 2f, SCENE_SEG_W, SEG_H,
                ["时间", "空白", "媒体控制"], Renderer.StandbyScene - 1, _hoveredStandbySceneIndex - 1);

            // ── 双击空白切换待机模式（单行开关，不套容器、不写副标题）──
            // 场景选「媒体控制」时岛内没有空白可双击，退出改走双击整块媒体区
            //（见 Core/NotchWindow 的双击分支）。
            DrawToggleRow(canvas, TOGGLE_ROW_Y + page,
                "双击空白切换待机模式", "",
                Renderer.StandbyToggleByDoubleClick, _standbyToggleHovered);

            // ── 显示内容列表 ──
            // 灵动岛显示什么、按什么次序，全在这一栏里：每项 = 复选框（勾选 = 显示在岛上）
            // + 名称（内置模块跟一枚「内置」胶囊）+ 右端的 ↑ ↓（调整在岛上的先后次序）。
            // 列表内容与顺序都取自 PluginManager 那张统一顺序表（内置模块与插件混排）。
            // 这一栏不再套外层容器（没有卡片底 / 边框），item 直接铺满内容区：
            // 靠「悬停才亮的那层底」表达可点，视觉上跟整页连成一体。
            // 条目数可能超过一屏能放的行数，超出部分靠滚轮滚动查看（_displayScroll = 滚动首行），
            //    可滚范围与命中 / 滚轮共用 GetDisplayListLayout；滚动时才画右侧那条滚动条。
            // 待机模式下整栏置灰不可交互（见 enabled）—— 那一栏管的是「普通模式显示什么」。
            float contentCardY = TITLE_BAR_HEIGHT + DISPLAY_CARD_Y + page;
            bool listEnabled = !Renderer.StandbyActive;

            var displayItems = PluginManager.Instance.DisplayItems;
            GetDisplayListLayout(out int visibleRows, out int maxFirstRow);
            // 条目变少（插件被移除）时把滚动位置钳回可滚范围，避免停在一片空白上
            _displayScroll = Math.Clamp(_displayScroll, 0, maxFirstRow);
            if (displayItems.Count == 0)
                canvas.DrawText("暂无可显示的内容", DISPLAY_ITEM_L + 10f, contentCardY + DISPLAY_FIRST_ROW_Y + 20, _subTextPaint);

            // slot = 可视槽位（0 = 当前首行），i = 绝对条目下标
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

            // 超出可视区时在列表右侧画一条滚动条指示（与下拉浮层同款），避免用户以为「列表就这么长」。
            // 滑块行程只能是「轨道高 - 滑块高」，写成 trackH * first / maxFirst 会让滑块滑出轨道。
            // 自动隐藏：没在滚动时整条不画（见 DrawScrollBar）。
            if (maxFirstRow > 0 && visibleRows > 0)
            {
                GetListScrollbarLayout(out float listTrackTop, out float listTrackH);
                DrawScrollBar(canvas, WIDTH - 14, listTrackTop, listTrackH,
                    visibleRows / (float)displayItems.Count, _displayScroll / (float)maxFirstRow);
            }

            // 整页滚动条：页面高于窗口时画在窗口最右侧（比上面那条更靠外）。
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

            // 合并卡片：「目标媒体平台」+「匹配方式」共用一张卡（两行 × 62 = 124 高，84..208）
            //    第 1 行行首 84 / 分隔线 142（行首 +58）/ 第 2 行行首 146（行距 62）
            //    本段所有 y 值必须与 WM_MOUSEMOVE 的 tab 2 命中段保持同步（见字段区的坐标常量注释）
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

            // ── 第 2 行：匹配方式（仅「通用媒体」下可选，其余平台整行置灰）──
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

            // 右框：手动模式的目标软件（直接显示 AppID）；自动匹配或非通用媒体时置灰
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

            // 歌词设置卡片（合并卡片 208 底 + 14 间距；与 WM_MOUSEMOVE 的 lyricY 同源）
            // 四行：开关行偏移 37 / 77 / 117（行距 40），末行是延迟补偿按钮（147..171）
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

            // 翻译歌词开关：译文作为第二行画在原文下方（仅当这句有译文时出现）
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

            // 逐字歌词开关（逐字与整行推进合并后的唯一开关）：歌词随演唱进度扫光 ——
            // 有逐字数据就按每个字自己的时值推进；这首歌拿不到逐字数据时自动回退卡拉 OK 的整行扫光
            //（见 MediaController.ComputeScanProgress）。标题里的括号就是对用户的回退说明。
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

        // 页签：媒体设置 —— 「全局快捷键」卡片（媒体页最后一张，坐标常量见 HOTKEY_CARD_Y 处）。
        //
        // 版式沿用本页其它卡片：标题 + 副标题 + 右侧开关；开关下面是五行「动作 → 键位框」。
        //    键位框可点：点一下进入录制态（框内提示「按下按键…」），下一次按键组合就成新键位。
        //    副标题平时写使用说明，出错时被 _hotkeyHint 顶掉（卡片里没有第二行可以挂提示）。
        private void DrawHotkeyCard(SKCanvas canvas)
        {
            float cardY = HOTKEY_CARD_Y;
            var cardRect = new SKRect(CONTENT_L, cardY, WIDTH - CONTENT_RM, cardY + HOTKEY_CARD_H);
            canvas.DrawRoundRect(cardRect, 6, 6, _cardBg);
            canvas.DrawRoundRect(cardRect, 6, 6, _cardBorder);

            // ── 标题区：标题 + 副标题（左），总开关（右，纵向居中于标题区）──
            canvas.DrawText("全局快捷键", CONTENT_TEXT_X, cardY + 19, _uiTextPaint);

            bool hasHint = _hotkeyHint.Length > 0;
            bool recording = _hotkeyRecordingIndex >= 0;
            // 优先级：错误提示 > 录制操作说明 > 平时说明。
            //    录制中把「Esc 取消 / Backspace 清空」摆出来，否则这两个操作用户根本发现不了
            //    （框里只能放得下「按下按键…」四个字）。
            string sub = hasHint ? _hotkeyHint
                : (recording ? "按下按键录制 · Esc 取消 · Backspace 清空"
                             : "在其他窗口也能控制播放 · 点按键框可重录");
            _subTextPaint.Color = hasHint ? new SKColor(230, 122, 92) : Neutral(170);
            // 宽度上限到开关左缘为止（开关轨道左缘 = 窗口右边 -12 -16 -42），别压在开关上
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
                    // 录制态加一圈蓝边：只有这一处会闪，用户一眼能认出「在等我按键」。
                    // 笔画宽度画完要还原 —— 这支画笔是共用的，别把下一帧的卡片描边也带粗。
                    float oldStroke = _dynamicStrokePaint.StrokeWidth;
                    _dynamicStrokePaint.Color = new SKColor(0, 140, 240);
                    _dynamicStrokePaint.StrokeWidth = 1f;
                    canvas.DrawRoundRect(boxRect, 4, 4, _dynamicStrokePaint);
                    _dynamicStrokePaint.StrokeWidth = oldStroke;
                }

                string text = rowRecording ? "按下按键…" : MediaHotkeys.FormatKey(i);
                // 三种态用三种颜色：录制中（蓝）> 有键位（正文色）> 未设置（弱化），
                //    弱化那档让「这条被清空了」一眼可辨，不用去数键位名。
                _dynamicTextPaint.Color = rowRecording ? new SKColor(0, 150, 255)
                    : (MediaHotkeys.IsBound(i) ? _fgColor : Neutral(120));
                // 右对齐到框内右侧 12px：键名长短不一，右对齐比居中更整齐（与框外其它控件的右基准线呼应）
                DrawTextWithEmoji(canvas, text, _dynamicTextPaint,
                    HOTKEY_BOX_W - 24f, HOTKEY_BOX_RIGHT - 12f, rowY + 20.5f, rightAlign: true);
                _dynamicTextPaint.Color = _fgColor;
            }
        }

        // 页签：交互设置
        private void RenderTabInteraction(SKCanvas canvas)
        {
            // 四行：yOffset 12 / 74 / 136 / 198，行距 62（改这里要同步改上面 tab 3 的悬停热区）。
            // 穿透模式不参与本卡片的置灰：开了穿透照样能开关这三个模式。
            // 总开关关掉时下面三行一起置灰（要启用总开关才能选择模式）。
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

            // 行 3：「暂停播放后自动隐藏」：媒体暂停 / 停止时也把岛藏起来。
            DrawToggleRow(canvas, 136, "暂停播放后自动隐藏",
                !NotchWindow.IsAutoHideEnabled ? "需先开启上方总开关" : "媒体暂停播放时，也把刘海藏起来",
                NotchWindow.IsPauseAutoHideEnabled,
                !isModeDisabled && _pauseHideToggleHovered,
                isModeDisabled);

            canvas.DrawLine(CONTENT_TEXT_X, TITLE_BAR_HEIGHT + 194, WIDTH - CONTENT_TEXT_RM, TITLE_BAR_HEIGHT + 194, _separatorPaint);

            // 行 4：「全屏自动隐藏」：检测到全屏视频 / 全屏游戏（含独占 D3D）时无条件让位，播放中也不显示。
            DrawToggleRow(canvas, 198, "全屏自动隐藏",
                !NotchWindow.IsAutoHideEnabled ? "需先开启上方总开关" : "检测到全屏视频 / 游戏时隐藏",
                NotchWindow.IsFullscreenAutoHideEnabled,
                !isModeDisabled && _fsHideToggleHovered,
                isModeDisabled);

            // 媒体交互方式 = 展开功能总闸：组合模式同样可展开媒体面板，
            //    因此不再置灰。总闸开着时，折叠态的展开入口由下一张卡片
            //    「双击封面跳转应用」决定 —— 跳转开着走右键（左键留给双击跳转），跳转关掉走左键单击。
            //    副标题必须与命中侧同口径（见 Renderer.MediaExpandByRightClick / MediaExpandByLeftClick
            //       与 NotchWindow 的 WM_LBUTTONDOWN / WM_RBUTTONDOWN）：写成「点击展开」会让用户去左键点，
            //       点完发现没反应 —— 后来改口径、细化时都明确要求同步文案。
            DrawToggleCard(canvas, 270, "媒体交互方式",
                Renderer.MediaInteractionMode == 1
                    ? "展开功能已开启：入口见下方「双击封面跳转应用」"
                    : "展开功能已关闭：折叠态右键直达媒体设置",
                Renderer.MediaInteractionMode == 1, _mediaExpToggleHovered);

            // 双击封面跳转应用（「双击哪里」已统一到封面）：
            //    双击封面把正在放媒体的那个应用切回前台 —— 折叠态双击媒体模块左半边（整条高度都算，
            //    不是只有缩略图那一小块）、展开态双击封面，两种形态同一块热区。
            //    它同时也是折叠态展开入口的开关：开着时左键被双击跳转占用，展开走右键；
            //    关掉后左键空闲，恢复左键单击展开。
            //    副标题必须与命中侧（Renderer.MediaExpandByRightClick / MediaExpandByLeftClick、
            //      Renderer.HitMediaLaunchZone）同口径：折叠态是「左半边」而不是「左上角」——
            //      命中判定完全不看 y（折叠态整条都在封面这一行里），写成左上角会让用户
            //      只敢往缩略图上点，恰好复现那次「按十次有两三次落在边上、感觉要点两下」。
            //      四档文案分别对应两个开关的四种组合，把「现在到底怎么展开」写清楚，不多写。
            //      副标题（x=CONTENT_TEXT_X 起、12px、开关左缘 522）宽度上限约 298px ≈ 24 个汉字，再长会压到开关上。
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
            // 关于页是独立的竖向居中布局（不在卡片里），中心点必须锁死在这个值：
            //    下面那排链接的起点由 centerX 推导，而 WndProc 里三个链接的命中区
            //    （x 305..370 / 375..440 / 445..500）是按 center=400 手写的，两边必须同源。
            //    想挪这个中心，就必须同时改 WndProc 的 tab 4 段，否则链接会「画在左边、点在右边」。
            //    这里不用 CONTENT_L 推导，是因为内容区在 2026-10-05 加宽过（左边界 200→186），
            //    跟随推导会把链接整体左移 7px 而命中区不动，反而点不中。
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
            void DrawMultiCard(float yOffset, string title, string[] subLabels, int[] indices, string unit)
            {
                // 该卡片的尺寸设置是否已被改动（index 0 / 2 / 4 已不可调，不参与判定）
                bool isModified = false;
                foreach (int index in indices)
                {
                    if (index == 0 || index == 2 || index == 4) continue;
                    if (Math.Abs(_customValues[index] - _defaultCustomValues[index]) > 0.001f)
                    {
                        isModified = true;
                        break;
                    }
                }

                float cardHeight = 36 + subLabels.Length * 34;
                var cardRect = new SKRect(CONTENT_L, TITLE_BAR_HEIGHT + yOffset, WIDTH - CONTENT_RM, TITLE_BAR_HEIGHT + yOffset + cardHeight);
                canvas.DrawRoundRect(cardRect, 6, 6, _cardBg);
                canvas.DrawRoundRect(cardRect, 6, 6, _cardBorder);

                canvas.DrawText(title, CONTENT_TEXT_X, TITLE_BAR_HEIGHT + yOffset + 26, _uiTextPaint);

                // 如果改动了某个尺寸设置，在标题旁边显示已生效标签
                if (isModified)
                {
                    float titleWidth = _uiTextPaint.MeasureText(title);
                    float tagX = CONTENT_TEXT_X + titleWidth + 10;
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

                    canvas.DrawText(subLabels[i], CONTENT_TEXT_X, cardBtnY + 17, _subTextPaint);

                    // 底部圆角只在「经典刘海」样式下参与圆角插值：切到灵动岛样式后该项会被
                    //    islandRadius 完全覆盖（见 Renderer.Draw 的 rBottom 计算），调了也看不出来，
                    //    所以就地标明生效条件 —— 与下面那条「系统自动调整」一样，默认隐藏、悬停该行才淡入。
                    if (index == 7)
                    {
                        float hintA = GetHintAlpha(index);
                        if (hintA > 0.01f)
                        {
                            float labelW = _subTextPaint.MeasureText(subLabels[i]);
                            _subTextPaint.Color = new SKColor(0, 140, 240, (byte)(255 * hintA));
                            canvas.DrawText("刘海模式下生效", CONTENT_TEXT_X + labelW + 8, cardBtnY + 17, _subTextPaint);
                            _subTextPaint.Color = Neutral(170);
                        }
                    }

                    // index 0 / 2 / 4 不可调：右侧只显示提示，不画「减 / 值 / 加 / 重置」（WndProc 的命中循环同步跳过）
                    if (index == 0 || index == 2 || index == 4)
                    {
                        float hintA = GetHintAlpha(index);
                        if (hintA > 0.01f)
                        {
                            const string autoHint = "系统自动调整，无需设置";
                            float hintW = _subTextPaint.MeasureText(autoHint);
                            _subTextPaint.Color = new SKColor(0, 140, 240, (byte)(255 * hintA));
                            canvas.DrawText(autoHint, WIDTH - CONTENT_TEXT_RM - hintW, cardBtnY + 17, _subTextPaint);
                            _subTextPaint.Color = Neutral(170);
                        }
                        continue;
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
            float themeY = TITLE_BAR_HEIGHT + 12;
            var themeRect = new SKRect(CONTENT_L, themeY, WIDTH - CONTENT_RM, themeY + 125);
            canvas.DrawRoundRect(themeRect, 6, 6, _cardBg);
            canvas.DrawRoundRect(themeRect, 6, 6, _cardBorder);

            canvas.DrawText("刘海 / 灵动岛主题", CONTENT_TEXT_X, themeY + 26, _uiTextPaint);
            canvas.DrawText("背景与文本颜色自适应反转", CONTENT_TEXT_X, themeY + 46, _subTextPaint);

            float themeRightX = WIDTH - CONTENT_TEXT_RM; // 变量隔离
            float btnY = GetBtnY(-1);

            void DrawThemeBtn(int index, string label, float leftOffset, float rightOffset)
            {
                bool isActive = Renderer.ThemeMode == index;
                bool isHovered = _hoveredThemeIndex == index;
                float btnWidth = leftOffset - rightOffset;

                _dynamicFillPaint.Color = (isActive || isHovered) ? Overlay(30) : Overlay(15);
                canvas.DrawRoundRect(new SKRect(themeRightX - leftOffset, btnY, themeRightX - rightOffset, btnY + 24), 4, 4, _dynamicFillPaint);

                _dynamicTextPaint.Color = isActive ? new SKColor(0, 140, 240) : _fgColor;

                // 根据文本真实长度在胶囊内部完美居中
                float textWidth = _dynamicTextPaint.MeasureText(label);
                float textX = themeRightX - leftOffset + (btnWidth - textWidth) / 2f;
                canvas.DrawText(label, textX, btnY + 17, _dynamicTextPaint);
            }

            DrawThemeBtn(0, "黑", 140, 100);
            DrawThemeBtn(1, "白", 90, 50);
            DrawThemeBtn(2, "系统", 40, 0);
            canvas.DrawText("背景透明度", CONTENT_TEXT_X, themeY + 75, _subTextPaint);
            float sliderY = themeY + 95;
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
                // 只画当前选中的小蓝球，或者鼠标悬停时的半透明反馈，去掉丑陋的灰色固定点
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
            // 「待机高度」与「媒体激活时高度」已合并为一个「全局折叠态高度」（index 3）：
            //    它同时管待机态、媒体折叠态与剪贴板面板的高度，值沿用原媒体控制存储的
            //    MEDIA_HEIGHT（注册表 Custom_MediaH），老用户的高度不会丢。
            DrawMultiCard(147, "待机显示", ["水平宽度", "全局折叠态高度", "底部圆角"], [0, 3, 7], "px");
            DrawMultiCard(299, "媒体控制", ["激活时宽度"], [2], "px");
            DrawMultiCard(383, "消息通知", ["弹出的宽度", "弹出的高度"], [4, 5], "px");
            DrawMultiCard(501, "全局 DPI 缩放", ["视觉比例"], [6], "x");
        }

        // 页签：我的插件（已安装插件列表，一个标题 + 一个列表；市场在独立页签）
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
            // 「插件市场」= 一键切到市场页签（与点左栏那一项同一条路），省得用户自己去找
            DrawPluginButton(2, "插件市场", CONTENT_TEXT_X + 208, topY + 60, 96);

            // ── 已安装插件列表卡片（可滚动，整卡高度）──
            GetPluginListCardTop(out float listY);
            var listRect = new SKRect(CONTENT_L, listY, WIDTH - CONTENT_RM, HEIGHT - 20);
            canvas.DrawRoundRect(listRect, 6, 6, _cardBg);
            canvas.DrawRoundRect(listRect, 6, 6, _cardBorder);
            canvas.DrawText($"已安装插件 ({_pluginView.Count})", CONTENT_TEXT_X, listY + 26, _uiTextPaint);

            // 最近一次「拖入 DLL」的结果提示：贴在列表卡标题行右侧。用红色标失败（非插件的 DLL
            // 导入后不会进列表，没有这行提示用户就完全不知道刚才那一下发生了什么）。
            if (!string.IsNullOrEmpty(_pluginHint))
            {
                string header = $"已安装插件 ({_pluginView.Count})";
                float hintMax = (WIDTH - CONTENT_TEXT_RM) - CONTENT_TEXT_X - _uiTextPaint.MeasureText(header) - 16;
                _subTextPaint.Color = _pluginHintIsError ? new SKColor(232, 100, 100) : new SKColor(120, 200, 140);
                // 必须走 Emoji 回退版：文案里的「」（U+2611 + U+FE0F）不在 YaHei UI 里，
                //    直接 DrawText 画出来是个豆腐块。渲染器那套逐码点回退在 Renderer 内部，
                //    设置窗口这样单独绘制的文字得自己带一次（见 DrawTextWithEmoji 的说明）。
                DrawTextWithEmoji(canvas, _pluginHint, _subTextPaint, hintMax, WIDTH - CONTENT_TEXT_RM, listY + 26, rightAlign: true);
                _subTextPaint.Color = Neutral(170);
            }
            // 这里没有「顺序一览」——显示与排序已统一收敛到「显示设置 → 显示内容」，
            //    插件中心只负责启用 / 禁用，不再提供任何排序入口（已移除）。

            // 可视行数与可滚范围走布局真源（与命中 / 滚轮共用 GetPluginListLayout）；
            // 条目变少时把滚动位置钳回可滚范围，避免停在一片空白上
            GetPluginListLayout(out int visibleRows, out int maxFirstRow);
            _pluginScroll = Math.Clamp(_pluginScroll, 0, maxFirstRow);

            // 上行：名称独占整行，可延展至卡片右边界外侧
            // 下行：信息（左）+ 操作按钮（右，从左到右：重载 | 卸载 | 开关）
            float nameTextMax = (WIDTH - CONTENT_TEXT_RM) - CONTENT_TEXT_X;                // 名称几乎全宽
            float infoTextMax = PLUGIN_BTN_RELOAD_X - CONTENT_TEXT_X - 8;     // 信息止于按钮区之前

            if (_pluginView.Count == 0)
                canvas.DrawText("暂无插件，点击「导入 DLL」或到「插件市场」下载", CONTENT_TEXT_X, listY + 66, _subTextPaint);

            // slot = 可视槽位（0 = 当前首行），i = 绝对条目下标（悬停 / 点击侧用的也是绝对下标）
            for (int slot = 0; slot < visibleRows; slot++)
            {
                int i = _pluginScroll + slot;
                var entry = _pluginView[i];
                // 行起点必须与 OnMouseMove 的 tab 6 段严格一致
                float rowY = listY + 44 + slot * PluginListRowH;
                if (slot > 0) canvas.DrawLine(CONTENT_TEXT_X, rowY - 5, WIDTH - CONTENT_TEXT_RM, rowY - 5, _separatorPaint);

                // ═══ 上行：插件名称（独占整行，无按钮遮挡） ═══
                canvas.DrawText(TruncateText(entry.FriendlyName, _uiTextPaint, nameTextMax), CONTENT_TEXT_X, rowY + 17, _uiTextPaint);

                // ═══ 下行：信息 + 全部操作按钮（同一行从左到右排列） ═══
                // 副标题文本已按「插件变更序号」在 RefreshPluginView 里预算好（见 ConsoleWindow.Plugin.cs），
                // 渲染路径只取用。禁用 / 加载失败的插件也列在这里（列全才能原地重新启用）；
                // 禁用态把开头的「已禁用」画成强调蓝，其余（版本 / 作者）照旧常规灰。
                string sub = i < _pluginSubTexts.Count ? _pluginSubTexts[i] : "";
                bool subDisabled = i < _pluginSubDisabled.Count && _pluginSubDisabled[i];
                float infoBaseline = rowY + 37;
                // 截断一律按整串的宽度算，再分段上色 —— 否则两段各截一半会出现两处省略号
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

                // ── 下行按钮（全部在同一行，y 中心 ≈ rowY+38） ──
                const float btnTop = 21f, btnH = 20f;       // 操作按钮矩形

                // 操作按钮（重载 / 卸载）+ 开关
                // 排序小三角已移除：位置调整统一走「显示设置 → 显示内容」，
                //    这里不再有 CanMoveOrder / MoveOrder 的入口。
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

            // 超出可视区时在卡片右侧画一条滚动条指示（与「显示内容」列表同款，滚动时才显形），
            // 滑块行程只能是「轨道高 - 滑块高」，写成 trackH * first / maxFirst 会让滑块滑出轨道。
            if (maxFirstRow > 0 && visibleRows > 0)
            {
                float trackTop = listY + 44 - 2f;
                float trackH = visibleRows * PluginListRowH - 8f;
                DrawScrollBar(canvas, WIDTH - 26, trackTop, trackH,
                    visibleRows / (float)_pluginView.Count, _pluginScroll / (float)maxFirstRow);
            }

            // ── 拖入 DLL 的蓝色反馈（光标落在右侧内容区时整体亮起，见 ConsoleWindow.PluginDrop.cs）──
            // 静态高亮：亮 / 灭直接切换，没有淡入淡出与呼吸，也没有定时器 ——
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

            // ── 弹窗（加载失败提示，与市场那套同一模板，画在卡片与拖入高亮之上）──
            // 卸载 / 重载都不再有确认弹窗（见 Click.cs）：它们只挪文件、不做不可逆的事。
            if (_marketDialog == MarketDialog.LoadFailed)
            {
                DrawLoadFailedDialog(canvas);
            }
        }

        // 页签：插件市场（顶栏：分类下拉 + 搜索框；下面一个列表）
        private void RenderTabMarket(SKCanvas canvas)
        {
            EnsureMarketData();
            ApplyPendingMarketResult();   // 消费后台安装结果：刷新列表 + 提示 + 评分弹窗
            if (_marketDataDirty) { _marketDataDirty = false; RefreshMarketFilter(); }   // UI 线程重建过滤视图

            float cardTop = TITLE_BAR_HEIGHT + 12;
            var cardRect = new SKRect(CONTENT_L, cardTop, WIDTH - CONTENT_RM, HEIGHT - 20);
            canvas.DrawRoundRect(cardRect, 6, 6, _cardBg);
            canvas.DrawRoundRect(cardRect, 6, 6, _cardBorder);

            // ── 第一行：分类下拉 + 搜索框（同一行、同一基线、同一字号，见 Market.cs 的布局真源）──
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

            // ── 搜索框（点击聚焦后可打字）：紧接分类按钮，宽度固定 158（不为右侧空白伸缩）──
            var searchRect = new SKRect(MarketSearchX, MarketControlsY, MarketSearchX + MarketSearchW, MarketControlsY + MarketControlH);
            _dynamicFillPaint.Color = Overlay(15);
            canvas.DrawRoundRect(searchRect, 5, 5, _dynamicFillPaint);
            _dynamicStrokePaint.Color = _marketSearchFocused ? new SKColor(0, 140, 240) : Neutral(_marketSearchHovered ? (byte)130 : (byte)90);
            canvas.DrawRoundRect(searchRect, 5, 5, _dynamicStrokePaint);
            // 放大镜（复用描边画笔：Draw 每秒 60 帧，别在这里 new SKPaint）
            float mgx = MarketSearchX + 14, mgy = MarketControlsY + 12;
            _dynamicStrokePaint.Color = Neutral(150);
            _dynamicStrokePaint.StrokeWidth = 1.5f;
            canvas.DrawCircle(mgx, mgy, 5f, _dynamicStrokePaint);
            canvas.DrawLine(mgx + 3.6f, mgy + 3.6f, mgx + 7f, mgy + 7f, _dynamicStrokePaint);
            _marketTextPaint.Color = _marketSearch.Length == 0 && _marketImeComposing.Length == 0 ? Neutral(120) : _fgColor;
            // 搜索框内容 = 已确认的搜索串 + 正在组字的串（组字串用灰色，与已上屏的部分区分开）
            // 串太长时只显示「插入点贴着右边界」的那一段（窗口起点见 MarketSearchViewStart）
            float caretX = MarketSearchTextX;
            if (_marketSearch.Length > 0)
            {
                int viewStart = MarketSearchViewStart();
                // 框选高亮：先铺底色再画字。只在聚焦时画（失焦后不该留着一块高亮），
                //    起点会被 MarketSearchXAtIndex 自动钳到可视窗口里，窗口外的部分本来也看不见。
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
                // 组字预览：跟在已上屏串后面，灰色 + 下划线，表示「还没上屏」
                _marketTextPaint.Color = Neutral(150);
                string comp = TruncateText(_marketImeComposing, _marketTextPaint, MarketSearchX + MarketSearchW - 8 - caretX);
                canvas.DrawText(comp, caretX, MarketControlsY + 17, _marketTextPaint);
                float cw = _marketTextPaint.MeasureText(comp);
                canvas.DrawLine(caretX, MarketControlsY + 21, caretX + cw, MarketControlsY + 21, _separatorPaint);
                caretX += cw;
                // 光标在串中间组字时，把后面的内容往右顺移（原生编辑框也是把尾巴推开，不能叠在一起）
                if (_marketSearchCaret < _marketSearch.Length)
                {
                    _marketTextPaint.Color = _fgColor;
                    string tail = TruncateText(_marketSearch[_marketSearchCaret..], _marketTextPaint,
                        MarketSearchX + MarketSearchW - 8 - caretX);
                    canvas.DrawText(tail, caretX, MarketControlsY + 17, _marketTextPaint);
                }
            }
            // 光标：聚焦时画在插入点上（点哪儿、按方向键都跟着动，不再固定贴串尾）
            if (_marketSearchFocused)
            {
                _dynamicFillPaint.Color = new SKColor(0, 140, 240);
                canvas.DrawRect(new SKRect(caretX + 1, MarketControlsY + 6, caretX + 2.4f, MarketControlsY + 20), _dynamicFillPaint);
            }

            // ── 第二行：左 = 「只看已安装」复选框 + 计数；右 = 刷新按钮 ──
            // 刷新按钮从第一行搬到这里（用户要求），与复选框同处一行、垂直位置走同一套「行顶 + 17」规则。
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
            // 右端现在是刷新按钮（不再是窗口右边）—— 计数 / 加载状态必须在它左侧收住，
            //    _marketError 可能很长，不截断会直接压到按钮上。
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

            // ── 刷新按钮：第二行最右（右边界与列表行按钮组同基准线），点击重新拉取市场数据 ──
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
            // 名称行的可用宽度不在这里定 —— 右侧要放「评分 + 下载量」的右对齐簇，
            // 可用宽度得按每行实际的簇宽动态算（见下面循环里的 nameMaxNow）。
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

            // slot = 可视槽位，i = 绝对条目下标（与悬停 / 点击侧一致）
            for (int slot = 0; slot < mVisibleRows; slot++)
            {
                int i = _marketScroll + slot;
                var mp = _marketView[i];
                float rowY = rowsTop + slot * PluginListRowH;
                if (slot > 0) canvas.DrawLine(CONTENT_TEXT_X, rowY - 5, WIDTH - CONTENT_TEXT_RM, rowY - 5, _separatorPaint);

                var local = MatchLocalPlugin(mp);
                bool busy = string.Equals(_marketBusyId, mp.Id, StringComparison.Ordinal);
                // 本地已装但被禁用：整行文字置灰（名字 / 官方 / 已装标签 / 版本信息），
                //    与「本机根本没装」区分开 —— 文件还在 plugins 里，用户只是没让它跑。
                //    按钮与右侧评分不置灰：前者仍要能点（重装 / 卸载），后者是市场侧信息。
                bool dim = PluginManager.Instance.IsDisabled(local);
                // 只有「本地版本 ≠ 市场版本」才提示已装 —— 版本一致时那颗按钮本来就是「重装」，
                //    再多一行「已装 vX」纯属重复信息。
                // 本地版本可能为空：插件被禁用后重启，它不会被加载，PluginEntry.Version 就读不到了
                //    （只有 CachedName 有缓存）。这时不能拼「已装 v」——后面会空一截，
                //    也绝不显示（无从判断版本是否一致）。
                string localVer = local?.Version ?? "";
                bool hasLocalVer = localVer.Length > 0;
                bool versionDiff = local != null && hasLocalVer && CompareVersions(mp.Version, localVer) != 0;

                // ═══ 上行右侧：评分 + 下载量（右对齐成一个簇，评分在下载量左边）═══
                // 星形用 BuildStarPath 画完整的实心五角星，不依赖 ★ 字符 ——
                //    ★（U+2605）要经字体回退，回退到不同字体时笔形残缺、看着「显示不全」；
                //    路径绘制恒定饱满（与评分弹窗那排星同一套路径）。
                // 分数只显示数字（如「4.5」），不带「分」字，简洁。
                // 还没人评分时显示「暂无评分」，而不是「0.0」——后者会被误读成「很差」。
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

                // ═══ 上行左侧：插件名 +（官方）+（已装 vX，仅版本不一致时）═══
                // 名称可用的宽度必须按「右侧评分簇 + 后面要跟的标签」动态收窄，
                //    否则长名字会压到评分/下载量上（右侧那块是右对齐的，不会自己让位）。
                string tagOfficial = mp.Official ? "官方" : "";
                // 按用户定的口径：只有版本不一致才提示「已装 vX」（版本一致时按钮本就是「重装」，
                //    再挂一行已装属重复信息）。版本读不到（禁用后重启）时无从比较，同样不显示 ——
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

                // ═══ 下行：版本 · 作者（禁用时前面缀一行「已禁用」，与「我的插件」页口径一致）═══
                if (busy)
                {
                    _subTextPaint.Color = new SKColor(0, 140, 240);
                    canvas.DrawText("正在下载安装…", CONTENT_TEXT_X, rowY + 37, _subTextPaint);
                    _subTextPaint.Color = Neutral(170);
                }
                else if (dim)
                {
                    // 已禁用：取代版本信息（这时版本读不到，硬拼会留个空的「v」），
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

                // ── 三个独立按钮：各自圆角、间隔 6px、尺寸一致、文字各自居中 ──
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

            // 市场列表滚动条（与「显示内容」/ 我的插件同款，滚动时才显形）
            if (mMaxFirst > 0 && mVisibleRows > 0)
            {
                float trackTop = rowsTop - 2f;
                float trackH = mVisibleRows * PluginListRowH - 8f;
                DrawScrollBar(canvas, WIDTH - 26, trackTop, trackH,
                    mVisibleRows / (float)_marketView.Count, _marketScroll / (float)mMaxFirst);
            }

            // ── 分类下拉浮层（画在卡片之上；点外部或选中项后收起）──
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
                        // ✓ (U+2713) 不在 YaHei UI 里，必须走 Emoji 回退，否则是个豆腐块
                        DrawTextWithEmoji(canvas, "✓", _subTextPaint, 20, menu.Right - 12, mY + k * rowH + 18, rightAlign: true);
                    }
                }
                _subTextPaint.Color = Neutral(170);
            }

            // ── 市场弹窗（详情 / 评分 / 卸载确认 / 加载失败提示，同一时刻最多一个）──
            if (_marketDialog == MarketDialog.LoadFailed)
            {
                // 加载失败提示没有市场条目（下标是 -1），所以不能走下面的下标分支
                DrawLoadFailedDialog(canvas);
            }
            else if (_marketDialogIndex >= 0)
            {
                var mp = GetMarketAt(_marketDialogIndex);
                if (mp == null) CloseMarketDialog();
                else DrawMarketDialog(canvas, mp);
            }
        }

        /// <summary>
        /// 评分文本：一位小数，固定用不变文化 —— 避免在部分区域设置下变成「4,5」。
        /// 单独抽成方法的另一个原因：含引号的 ToString("0.0", …) 不能直接写进插值字符串的花括号里
        /// （老版本 C# 会把内层引号当成字符串结尾，直接编译不过）。
        /// </summary>
        private static string FormatScore(double score)
            => score.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture);

        /// <summary>
        /// 主操作按钮的配色：按状态分三种颜色（用户明确要求）。
        ///   下载 = 绿（获取新东西）／更新 = 橙（有新版本，需要留意）／重装 = 主题蓝（与全程序各处
        ///   强调蓝同色：0,120,212，悬停 0,140,240 —— 本地已最新，是常规可放心操作的动作）。
        /// </summary>
        private static (SKColor Normal, SKColor Hover) InstallButtonColors(PluginEntry? local, MarketPlugin mp)
        {
            if (local == null) return (new SKColor(44, 160, 92), new SKColor(58, 184, 108));       // 下载：绿
            // 版本读不到（禁用后重启）无从比较更新与否，按「重装」着色，别谎报有新版
            if (local.Version.Length > 0 && CompareVersions(mp.Version, local.Version) > 0)
                return (new SKColor(214, 126, 24), new SKColor(238, 146, 34));                    // 更新：橙
            return (new SKColor(0, 120, 212), new SKColor(0, 140, 240));                          // 重装：主题蓝
        }

        /// <summary>
        /// 在按钮矩形里居中画标签，并顺手把共用画笔的颜色还原回前景色。
        ///
        /// 为什么必须单独抽一个：测量与绘制一定要用同一个画笔。
        /// 设置窗口里绘制按钮文字用的是 _uiTextPaint（13px），若拿 _subTextPaint（12px）去量宽度，
        /// 量出来的值比真实字宽小，文字就会整体偏左 —— 每颗按钮都偏一点，看着就是「没居中」。
        /// 垂直也在这里统一（基线 = 按钮中线 + 字号的 0.35，即常规的视觉居中偏移）。
        /// </summary>
        private void DrawCenteredButtonLabel(SKCanvas canvas, string label, SKRect button, SKPaint paint)
        {
            float tw = paint.MeasureText(label);
            canvas.DrawText(label, button.MidX - tw / 2f, button.MidY + paint.TextSize * 0.35f, paint);
            paint.Color = _fgColor;   // 共用画笔，画完必须还原
        }

        /// <summary>
        /// 弹窗头部：左上标题（超宽截断，带 Emoji 回退）+ 右上关闭按钮（悬停加粗变亮）。
        /// 返回正文起始 y —— 三种弹窗（详情 / 评分 / 加载失败）共用这一段，
        /// 样式一致靠它保证，别在各自的分支里再抄一遍。
        /// </summary>
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

        /// <summary>
        /// 市场弹窗总入口：详情 / 评分共用头部模板（见 DrawDialogHeader），正文按类型分派。
        /// 圆角与描边沿用原来那套（_menuBg + _menuBorder）。
        /// 注：卸载不再有确认弹窗（只挪文件、不做不可逆的事，见 Click.cs）。
        /// </summary>
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

            // 元数据行：评分（不带人数）· 版本 · 作者 · 更新日期 · 下载量
            // 人数只在「评分弹窗」里显示 —— 详情弹窗给的是概览，不需要这个细节
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
            // 评分弹窗的摘要保留「人数」（用户明确要求这个窗口不要改）：
            //    列表行只显示星 + 数字，但评分窗口要能看到有多少人评过。
            string summary = _rateCount > 0 ? $"{FormatScore(_rateAverage)} 分 · {_rateCount} 人评分" : "还没有人评分";
            if (_rateLoading) summary = "正在读取…";
            _subTextPaint.Color = Neutral(150);
            canvas.DrawText(summary, rect.Left + DialogPad, y + 12, _subTextPaint);
            _subTextPaint.Color = Neutral(170);

            // 星级：鼠标预览优先，否则显示「我评过的分」——那是锁定值，不能再改
            double shown = _rateStars > 0 ? _rateStars : _rateMine;
            var stars = GetRateStarsRect(rect);
            DrawStarRow(canvas, stars, shown);

            // 状态行（弹窗底部那行常驻文案）
            float sy = stars.Bottom + 16f;
            string status = _rateStatus;
            if (status.Length == 0)
            {
                // 已评过：直接用用户定的那句话（与提交时 already_rated 的提示同一口径）
                status = _rateMine > 0
                    ? $"您已经给此插件打了 {FormatScore(_rateMine)} 分，感谢您的参与"
                    : _rateLoading ? "" : "拖到星星上选分，点一下提交（支持半星，每人只能评一次）";
            }
            if (status.Length > 0)
            {
                // 提交结果（成功绿 / 失败红）；只是说明性文案（如「已评过、不能改」）用常规灰
                _subTextPaint.Color = _rateStatus.Length > 0
                    ? (_rateStatusIsError ? new SKColor(232, 100, 100) : new SKColor(120, 200, 140))
                    : Neutral(160);
                DrawTextWithEmoji(canvas, TruncateText(status, _subTextPaint, DialogInnerW), _subTextPaint,
                    DialogInnerW, rect.Left + DialogPad, sy + 12);
                _subTextPaint.Color = Neutral(170);
            }
        }

        /// <summary>
        /// 五颗星。支持半星：每颗先用底色画空心描边星，再把实心星按「分值比例」裁剪后叠上去
        /// （与官网 market 的 rate__fill 同一套做法）。
        /// </summary>
        private void DrawStarRow(SKCanvas canvas, SKRect area, double value)
        {
            const int stars = 5;
            float slotW = area.Width / stars;
            float size = Math.Min(slotW - 4f, area.Height);
            float r = size / 2f;
            float cy = area.MidY;

            // 五颗星半径完全一致 ⇒ 共用一份路径，靠 canvas.Translate 摆位。
            //    原先每颗星各 new 一个 SKPath 且从不 Dispose —— 评分弹窗每次渲染（含纯悬停换帧）
            //    就漏 5 个原生轮廓，同文件 :1705 / :1921 的 BuildStarPath 调用都用了 using，这里是漏网的一处。
            //    路径建在原点、坐标系交给 Translate，所以下面的裁剪矩形也换成以中心为原点。
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

        /// <summary>标准五角星路径（外径 / 内径可调），中心为 (cx, cy)。</summary>
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
        /// 加载失败提示弹窗：标题 + 两行正文 + 一颗居中的「好的」。
        /// 纯告知，没有可取消的动作，关闭叉 / 点外部 / 点按钮都是关窗（见 Click.cs 两个页签的分支）。
        /// （这是插件相关唯一的弹窗 —— 卸载 / 重载都不再确认，见 Click.cs。）
        /// </summary>
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

        // 各页签展开的下拉浮层（媒体平台 / 匹配方式 / 目标软件 / 通知内容 / 目标显示器）
        /// <summary>
        /// 画一个展开的下拉列表浮层。行高固定 DROPDOWN_ROW_H（26），
        /// 与命中判定、滚轮的可滚范围必须一致。
        ///  那一项灰显（但仍可点，用于「自定义项失效」这种
        /// 「能点进去重选、但当前值不可用」的场景）。
        ///  是首行索引，由调用方给定：
        /// 只有提示音列表项数会超过可视区（传 _dropdownScroll），其余列表一律传 0。
        /// 本方法不做「自动把选中项滚进可视区」——那件事只在展开那一刻做一次
        ///    （见 ScrollToastSoundMenuToSelected），否则滚轮会被每帧拉回顶部。
        /// </summary>
        private void RenderDropdownList(SKCanvas canvas, float x, float yOffset, float w,
                                        string[] options, int selectedIndex, int hoveredIndex, int dimmedIndex,
                                        bool upward = false, int scrollFirst = 0, int visibleRowsOverride = 0)
        {
            // 浮层高度必须钳制在窗口内：
            //    提示音列表是动态加载的（data\sound 里丢多少 wav 就有多少项），
            //    若不做上限，16 项 = 416px 就会从提示音卡一直拖到窗口底部之外（40 项更夸张）。
            //    这里保证「浮层底边 ≤ 窗口高 - 12」，超出部分走滚动窗口（见 _dropdownScroll）。
            //    upward=true 时改为向上展开（锚在控件上方），适合同一行的「音量」下拉 ——
            //    它天生贴近窗口下半区，向下展开 11 档必然出界。
            float anchorY = TITLE_BAR_HEIGHT + yOffset;   // 向下：控件下沿；向上：控件上沿
            int total = options.Length;
            float availFrom = upward ? anchorY - 2 : anchorY;
            int maxRows = upward
                ? Math.Max(1, (int)((availFrom - TITLE_BAR_HEIGHT - 12) / DROPDOWN_ROW_H))
                : Math.Max(1, (int)((HEIGHT - 12 - availFrom) / DROPDOWN_ROW_H));
            // 调用方若已经用布局真源（GetToastSoundMenuLayout / GetVolumeMenuLayout）算过可视行数，
            //    就以它为准 —— 否则命中侧与绘制侧又变成两份独立算式（A7 的病根）。
            int visible = visibleRowsOverride > 0
                ? Math.Min(total, visibleRowsOverride)
                : Math.Min(total, maxRows);
            float listH = visible * DROPDOWN_ROW_H;
            float dY = upward ? anchorY - 2 - listH : anchorY;
            var dRect = new SKRect(x, dY, x + w, dY + listH);
            canvas.DrawRoundRect(dRect, 4, 4, _menuBg);
            canvas.DrawRoundRect(dRect, 4, 4, _menuBorder);

            // 滚动：首行完全由调用方给。以前这里会在选中项跑出可视区时把 first 强行拉回
            // 选中项附近 —— 于是滚轮刚滚下去、下一帧就被拉回顶部，用户看到的就是「滚不动」。
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

            // 超出可视区时在右侧画一条滚动条指示（与其他列表同款：滚动时才显形）
            if (total > visible)
            {
                float trackH = listH - 8;
                // 滑块只能走「轨道高 - 滑块高」这段行程（由 DrawScrollBar 保证）。
                //    曾经写成 `trackH * first / maxFirst`：当滑块本身很长（可视行数接近总行数）时，
                //    thumbY + thumbH 会一路超过轨道底边，滑块整条滑出下拉菜单往下掉 ——
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

            // 消息提示音下拉菜单（通用设置）：无 / data\sound 里的每个 wav / 浏览音频…
            if (_selectedTab == 0 && _toastSoundDropdownOpen)
            {
                // 浮层锚点与可滚范围都取自 GetToastSoundMenuLayout —— 绘制 / 悬停命中 / 滚轮三处同源，
                // 不要再在这里手写 `SOUND_BOX_Y + SOUND_ROW_H + 2`。
                GetToastSoundMenuLayout(out float soundMenuTop, out int soundVisible, out _);
                RenderDropdownList(canvas, SOUND_CTRL_X, soundMenuTop - TITLE_BAR_HEIGHT, SOUND_CTRL_W,
                    ToastSoundConfig.BuildOptionLabels(), ToastSoundConfig.SelectedIndex, _hoveredToastSoundIndex,
                    dimmedIndex: ToastSoundConfig.IsUsableFile(ToastSoundConfig.CustomPath, out _)
                        ? -1 : ToastSoundConfig.CustomIndex,
                    scrollFirst: _dropdownScroll, visibleRowsOverride: soundVisible);
            }

            // 音量下拉菜单：档位多（0%~100%，10% 一档共 11 项）且控件贴近窗口下半区，
            //    所以向上展开 —— 向下展开会一路拖出窗口。
            //    可视行数由 GetVolumeMenuLayout 给出（与命中侧同源），绘制不再自己算一份。
            if (_selectedTab == 0 && _soundVolumeDropdownOpen)
            {
                GetVolumeMenuLayout(out _, out _, out int volVisible);
                RenderDropdownList(canvas, SOUND_VOL_X, SOUND_BOX_Y, SOUND_VOL_W,
                    VolumeOptionLabels, ToastSoundConfig.VolumeIndex, _hoveredSoundVolumeIndex, dimmedIndex: -1,
                    upward: true, scrollFirst: 0, visibleRowsOverride: volVisible);
            }

            // 目标显示器（浮层锚点跟着整页偏移一起走，否则滚动后它会脱开下拉框）
            // 几何与命中侧完全同源：左端 = 内容区右缘 − MONITOR_DD_W，顶 = 下拉框底部 + 2
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
