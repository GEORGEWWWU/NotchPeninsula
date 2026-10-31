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
    /// </summary>
    internal static class GlassSupport
    {
        /// <summary>连续多久拿不到玻璃图层就判定为不支持（毫秒）。</summary>
        private const double FailLimitMs = 4000;

        /// <summary>连续多久背板没有内容就退回实体（毫秒）。</summary>
        private const double NoContentLimitMs = 8000;

        private static double _failMs;
        private static double _noContentMs;
        private static long _lastStamp;

        /// <summary>用户在设置里重新选了材质：清零所有计时，别带着上一轮的失败立刻降级。</summary>
        public static void Reset()
        {
            _failMs = 0;
            _noContentMs = 0;
            _lastStamp = 0;
        }

        /// <summary>
        /// 每帧调一次（仅材质 = 液态玻璃 2 且岛体可见时）。
        /// </summary>
        /// <param name="drawn">本帧是否真的画出了玻璃图层</param>
        public static void Tick(bool drawn)
        {
            long now = Stopwatch.GetTimestamp();
            double dt = _lastStamp == 0 ? 0 : (now - _lastStamp) * 1000.0 / Stopwatch.Frequency;
            _lastStamp = now;
            if (dt < 0 || dt > 500) dt = 0;   // 长时间没渲染（隐藏 / 挂起）不算连续失败

            if (drawn)
            {
                _failMs = 0;
                _noContentMs = 0;
                return;
            }

            _failMs += dt;

            // 背板本身有没有内容，是另一条独立的判据：
            // 合成可能完全正常，只是这台机器上抓屏拿不到桌面（远程桌面、独占全屏、DRM 保护窗口）。
            if (!IslandBackdrop.IsContentAlive) _noContentMs += dt;
            else _noContentMs = 0;

            // ── ① 合成本身失败：退回液态玻璃（v1）──
            if (_failMs >= FailLimitMs)
            {
                string why = LiquidGlass2.HealthDetail.Length > 0
                    ? LiquidGlass2.HealthDetail
                    : "连续拿不到玻璃图层";
                Fallback("lg2-degrade-v1",
                    "「液态玻璃 2.0」在这台电脑上无法正常渲染，已临时切回「液态玻璃」。\n\n"
                    + "原因：" + why + "\n"
                    + "系统：" + UiAlert.WindowsVersion() + "\n"
                    + "抓屏：" + (IslandBackdrop.UsingMagnifier ? "放大镜（1:1）" : "兼容模式") + "\n\n"
                    + "可以继续使用其它材质；如果想再试一次，在「个性化 → 背景材质」里重新选它。",
                    toGlass2: false);
                return;
            }

            // ── ② 有像素、但桌面内容一直是空的：退回实体 ──
            if (_noContentMs >= NoContentLimitMs)
            {
                Fallback("lg2-degrade-solid",
                    "这台电脑上抓不到桌面画面（背板始终没有内容），「液态玻璃 2.0」无法显示出玻璃效果，已临时切回「实体」。\n\n"
                    + "系统：" + UiAlert.WindowsVersion() + "\n"
                    + "抓屏：" + (IslandBackdrop.UsingMagnifier ? "放大镜（1:1）" : "兼容模式") + "\n\n"
                    + "常见原因：远程桌面 / 虚拟机、独占全屏应用、受 DRM 保护的窗口。",
                    toGlass2: false);
            }
        }

        private static void Fallback(string key, string message, bool toGlass2)
        {
            Reset();

            // 只在渲染线程改标志位：位图缓存与抓屏重建都会在主循环里跟进，
            // 这里不直接 Dispose 任何东西，避免和渲染同帧互相踩。
            Renderer.IslandLiquidGlass2 = false;
            Renderer.IslandLiquidGlass = toGlass2;
            if (!toGlass2 && !Renderer.IslandLiquidGlass) Renderer.IslandAcrylic = false;
            Renderer.ApplyThemeColors();

            Program.SaveSetting("IslandLiquidGlass2", 0);
            Program.SaveSetting("IslandLiquidGlass", Renderer.IslandLiquidGlass ? 1 : 0);
            Program.SaveSetting("IslandAcrylic", Renderer.IslandAcrylic ? 1 : 0);
            NotchWindow.RequestAcrylicRebuild();

            Logger.Warn("[液态玻璃2.0] 已降级：" + message.Replace('\n', ' '));
            UiAlert.ShowOnce(key, message);
        }
    }
}
