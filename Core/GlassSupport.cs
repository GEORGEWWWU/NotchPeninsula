using System.Diagnostics;

namespace NotchPeninsula
{
    /// <summary>
    /// 液态玻璃 2 的可用性守卫。
    ///
    /// 分工：
    ///   · 切换材质那一刻由 <see cref="LiquidGlass2.SelfTest"/> 判定（纯内存小图，不碰抓屏）；
    ///   · 运行中由本类判定：连续拿不到玻璃图层就说明这台机器算不出这张图，
    ///     与其让岛体一直退回一块普通模糊背板（用户眼里就是「打不开」），
    ///     不如退回能用的材质并弹窗说明原因。
    ///
    /// 为什么需要「连续」：抓屏线程刚启动、显示器切换、锁屏恢复时都会短暂没有背板，
    /// 单帧失败完全是正常现象，一失败就降级会让材质随机跳掉。
    ///
    /// ⚠️ 最容易写错的一条：「还没有背板」不等于「合成失败」。
    ///    启动阶段桌面要十几秒才出来（见 NotchWindow.Acrylic 里启动刷新的那几段注释），
    ///    这期间抓屏线程可能一帧都没产出，渲染侧压根没调用 <see cref="LiquidGlass2.Compose"/>。
    ///    旧实现却把这段时间照常累进失败计时，于是启动刚满 4 秒就弹窗说「无法正常渲染」，
    ///    把一台实测完全跑得动 v2 的机器切走（同一台机器重选 v2 后 31ms 就正常出图）。
    ///    现在预热期只观察、不判定：见 <see cref="Tick(bool, bool)"/>。
    ///
    /// 另外两条纪律：
    ///   · 降级只对【本次会话】生效，不写注册表 —— 一次误判不该让下次启动直接放弃 v2，
    ///     否则用户再也没有机会用上本来能用的材质。
    ///   · 降级目标必须真的是弹窗里写的那个材质：旧实现用 toGlass2 反推，v1 分支里
    ///     顺手把 <see cref="Renderer.IslandLiquidGlass"/> 也置了 false，文案写「切回液态玻璃」，
    ///     实际落到「实体」（日志里就是 材质=实体 涂层alpha=127），两边对不上。
    /// </summary>
    internal static class GlassSupport
    {
        /// <summary>连续多久拿不到玻璃图层就判定为不支持（毫秒）。只从「真拿到背板」之后开始计。</summary>
        private const double FailLimitMs = 4000;

        /// <summary>连续多久背板没有内容就退回实体（毫秒）。同样只从「真拿到背板」之后开始计。</summary>
        private const double NoContentLimitMs = 8000;

        /// <summary>预热期持续这么久还没有背板，就往日志里补一条带抓屏状态的记录（只诊断，不降级）。</summary>
        private const double WarmupWarnMs = 15000;

        private static double _failMs;
        private static double _noContentMs;
        private static long _lastStamp;

        /// <summary>预热期已经持续多久（毫秒）：只用来决定要不要补诊断日志，不参与任何判定。</summary>
        private static double _warmMs;
        private static bool _warmLogged;

        /// <summary>
        /// 本次观察期内是否真的拿到过背板。
        /// 只有它为 true 时，「背板内容为空」才有意义 —— 否则只是「还没抓到」，不是「抓不到」。
        /// </summary>
        private static bool _hadBackdrop;

        /// <summary>本次会话的降级留痕（空 = 没降级过）。只给日志 / 诊断看，不进设置项。</summary>
        private static string _fallbackNote = "";

        /// <summary>用户在设置里重新选了材质：清零所有计时，别带着上一轮的失败立刻降级。</summary>
        public static void Reset()
        {
            _failMs = 0;
            _noContentMs = 0;
            _lastStamp = 0;
            _warmMs = 0;
            _warmLogged = false;
            _hadBackdrop = false;
            _fallbackNote = "";   // 重选材质 = 重新观察，上一次的降级记录也不再代表当前状态
        }

        /// <summary>
        /// 本次会话的守卫快照（写进诊断包）。纯只读，不碰渲染状态 —— 导出诊断不该改变任何东西。
        /// 误降级的排查全看这一行：背板到底有没有产出过、失败计时累计了多少、这次运行是否已降级。
        /// </summary>
        public static string StatusLine
        {
            get
            {
                string state = _fallbackNote.Length > 0
                    ? "本次会话已降级，" + _fallbackNote
                    : "未降级";
                return state
                    + $"；背板曾有={_hadBackdrop}；合成失败累计 {_failMs:F0}ms；"
                    + $"无内容累计 {_noContentMs:F0}ms；预热 {_warmMs / 1000.0:F1}s；"
                    + $"抓屏 {IslandBackdrop.DebugState}";
            }
        }

        /// <summary>
        /// 每帧调一次（仅材质 = 液态玻璃 2 且岛体可见时）。
        /// 兼容旧调用点：背板是否就位直接读 <see cref="Renderer.AcrylicBackdrop"/>，
        /// 它与渲染侧本帧实际用的那张图是同一个对象（同一渲染线程、同一次 Render 内不会被改）。
        /// </summary>
        /// <param name="drawn">本帧是否真的画出了玻璃图层</param>
        public static void Tick(bool drawn)
            => Tick(drawn, Renderer.AcrylicBackdrop != null);

        /// <summary>
        /// 每帧调一次（仅材质 = 液态玻璃 2 且岛体可见时）。
        /// </summary>
        /// <param name="drawn">本帧是否真的画出了玻璃图层</param>
        /// <param name="backdropReady">
        /// 本帧渲染侧是否真的拿到了背板（= Renderer.AcrylicBackdrop != null）。
        /// false 表示抓屏线程还没产出第一张图 / 背板刚被材质切换清空 —— 这是预热期，
        /// 渲染侧连 <see cref="LiquidGlass2.Compose"/> 都没调用，不能算合成失败。
        /// </param>
        public static void Tick(bool drawn, bool backdropReady)
        {
            long now = Stopwatch.GetTimestamp();
            double dt = _lastStamp == 0 ? 0 : (now - _lastStamp) * 1000.0 / Stopwatch.Frequency;
            _lastStamp = now;
            if (dt < 0 || dt > 500) dt = 0;   // 长时间没渲染（隐藏 / 挂起）不算连续失败

            if (drawn)
            {
                // 能画出玻璃 = 合成链路通了，同时说明背板早就到位
                _failMs = 0;
                _noContentMs = 0;
                _warmMs = 0;
                _hadBackdrop = true;
                return;
            }

            // ── 预热期：背板还没产出 ──
            // 渲染侧写的是 `if (backdrop != null && ...) Compose(...)`，没有背板就不可能调用合成，
            // 所以这一帧「没画出玻璃」只说明「桌面画面还没到」，跟「这台机器算不出」无关。
            // 计时清零而不是留着：留着等于把预热前后的失败帧连成一段「连续」，
            // 而那正是旧实现 4 秒误判的来源（启动时抓屏线程一帧都还没产出）。
            if (!backdropReady)
            {
                _failMs = 0;
                _noContentMs = 0;
                _warmMs += dt;
                if (!_warmLogged && _warmMs >= WarmupWarnMs)
                {
                    _warmLogged = true;
                    // 预热特别久时留一条可诊断的记录：抓屏线程没跑 / 没拿到目标矩形 / 屏幕 DC 打不开，
                    // 都能从 DebugState 里看出来。只记日志，绝不判定失败。
                    Logger.Warn($"[玻璃] 预热期已持续 {_warmMs / 1000.0:F1}s 仍没有背板，暂不判定失败"
                                + $"（抓屏状态：{IslandBackdrop.DebugState}）");
                }
                return;
            }

            _hadBackdrop = true;
            _warmMs = 0;

            // ── 真拿到背板、真调用了 Compose、却没出图：这才是「合成本身失败」 ──
            _failMs += dt;

            // 背板有没有内容，是另一条判据：合成可能完全正常，只是这台机器抓不到桌面
            // （远程桌面、独占全屏应用、受 DRM 保护的窗口都会这样）。
            // ⚠️ 只在「真有背板」时累计：_contentAlive 的默认值就是 false，
            //    旧实现把默认值当成「内容死了」，背板一帧都没有也会在 8 秒后判定，那是纯误判。
            if (!IslandBackdrop.IsContentAlive) _noContentMs += dt;
            else _noContentMs = 0;

            // ── ① 合成本身失败：退回液态玻璃（v1）──
            if (_failMs >= FailLimitMs)
            {
                string capture = IslandBackdrop.UsingMagnifier ? "放大镜（1:1）" : "兼容模式";

                // HealthDetail 只在「读到坏像素 / 合成抛异常」时才有值。Compose 因为几何太小、
                // 模糊核建不出来直接返回 null 时它是空的 —— 这时必须把渲染侧真正知道的状态补上，
                // 否则弹窗只剩一句「连续拿不到玻璃图层」，下次排查等于没有信息。
                string why = LiquidGlass2.HealthDetail;
                if (string.IsNullOrEmpty(why))
                    why = $"背板已就位（版本 {IslandBackdrop.Version}），"
                        + $"但玻璃图层连续 {_failMs / 1000.0:F1} 秒合成为空"
                        + $"（健康状态 {LiquidGlass2.Health}，抓屏 {capture}）";

                Fallback("lg2-degrade-v1",
                    "「液态玻璃 2.0」在这台电脑上无法正常渲染，本次会话已临时切回「液态玻璃」。\n\n"
                    + "原因：" + why + "\n"
                    + "系统：" + UiAlert.WindowsVersion() + "\n"
                    + "抓屏：" + capture + "\n"
                    + "诊断：" + IslandBackdrop.DebugState + "\n\n"
                    + "可以继续使用其它材质；如果想再试一次，在「个性化 → 背景材质」里重新选它"
                    + "（重启后也会重新尝试）。",
                    why,
                    landOnGlassV1: true);
                return;
            }

            // ── ② 有背板、但内容一直是空的：退回实体 ──
            // 这一步只有在「真拿到背板」之后才可能走到（上面已保证）。
            // 没背板时压根不会计数，更不会在这里误判成「抓不到桌面画面」。
            if (_noContentMs >= NoContentLimitMs)
            {
                string capture = IslandBackdrop.UsingMagnifier ? "放大镜（1:1）" : "兼容模式";
                string why = $"背板版本 {IslandBackdrop.Version}，"
                           + $"玻璃源区域连续 {_noContentMs / 1000.0:F1} 秒无内容";

                Fallback("lg2-degrade-solid",
                    "这台电脑上抓不到桌面画面（背板始终没有内容），「液态玻璃 2.0」无法显示出玻璃效果，"
                    + "本次会话已临时切回「实体」。\n\n"
                    + "系统：" + UiAlert.WindowsVersion() + "\n"
                    + "抓屏：" + capture + "\n"
                    + "诊断：" + IslandBackdrop.DebugState + "\n\n"
                    + "常见原因：远程桌面 / 虚拟机、独占全屏应用、受 DRM 保护的窗口。",
                    why,
                    landOnGlassV1: false);
            }
        }

        /// <summary>
        /// 本次会话内降级。⚠️ 不写注册表：降级是「这次先让你能用」，不是「用户以后都不要 v2」。
        /// 旧实现写 Program.SaveSetting，一次误判就永久生效 —— 下次启动直接按 v1/实体启动，
        /// 用户再也没有机会试 v2；而实测那台被误判的机器重选 v2 三十毫秒就正常出图。
        /// </summary>
        /// <param name="key">弹窗去重键（同一次运行里同一原因只弹一次）</param>
        /// <param name="message">给用户看的完整说明</param>
        /// <param name="why">给日志 / 诊断用的一句话原因</param>
        /// <param name="landOnGlassV1">true = 退到液态玻璃（v1）；false = 退到实体</param>
        private static void Fallback(string key, string message, string why, bool landOnGlassV1)
        {
            bool hadBackdrop = _hadBackdrop;   // 计时有它才有意义，写诊断时保留「已经发生过」的事实

            Reset();
            _hadBackdrop = hadBackdrop;

            // 只在渲染线程改标志位：位图缓存与抓屏重建都会在主循环里跟进，
            // 这里不直接 Dispose 任何东西，避免和渲染同帧互相踩。
            //
            // ⚠️ 三个标志必须显式写全。旧实现拿 toGlass2 反推，v1 分支里
            //    IslandLiquidGlass 也被写成 false ⇒ 弹窗说「切回液态玻璃」，
            //    实际落到「实体」；日志里 材质=实体 涂层alpha=127 就是这个问题。
            Renderer.IslandLiquidGlass2 = false;
            Renderer.IslandLiquidGlass = landOnGlassV1;
            Renderer.IslandAcrylic = false;
            Renderer.ApplyThemeColors();

            string target = landOnGlassV1 ? "液态玻璃" : "实体";
            _fallbackNote = $"已降级到「{target}」：{why}";

            // 抓屏那条线要按新材质重建（降采样倍数、放大镜矩形都会变）
            NotchWindow.RequestAcrylicRebuild();

            // 留痕只写日志 / 诊断：设置项保持用户原来选的材质，重启后还会重新试一次 v2。
            Logger.Warn($"[液态玻璃2.0] 本次会话已降级到「{target}」"
                        + $"（仅本会话生效，设置未改，重启 / 重选材质会重新尝试）"
                        + $"；原因：{OneLine(why)}"
                        + $"；抓屏：{(IslandBackdrop.UsingMagnifier ? "放大镜（1:1）" : "兼容模式")}"
                        + $"；{IslandBackdrop.DebugState}");
            UiAlert.ShowOnce(key, message);
        }

        /// <summary>把多行原因压成一行写日志（日志按行读，换行会把一条记录劈成好几条）。</summary>
        private static string OneLine(string s) => s.Replace('\r', ' ').Replace('\n', ' ');
    }
}
