using SkiaSharp;

namespace NotchPeninsula
{
    public partial class NotchWindow
    {
        // 渲染线程独占的一份背板引用：只有它能释放，避免和后台线程抢着析构
        private SKImage? _acrylicOwned;
        private int _acrFrames;
        private bool _acrLogged;
        private bool _acrBootRefresh; // 启动后是否已做过一次「等价于点质量档」的刷新
        private long _bootAtMs;       // 启动后第一次进入这里的时间戳
        private long _acrBootRetryMs; // 背板还没内容时，下一次催重抓的时间戳
        private int _acrBootRetries;  // 催抓次数（封顶，别无限刷）
        private bool _lastGlassMode;

        // 亚克力背板：抓的是【整个画布框 ∩ 屏幕】，不是当前胶囊框。
        // 这样胶囊变大变小（悬停展开 / 待机↔媒体切换）时只挪源矩形，完全不用重抓 —— 尺寸变换期间的闪烁就没了。
        // 渲染线程这里只写「要抓哪块」+「岛体占哪块」+ 取成品，真正的抓屏在 NPS-Backdrop 线程上（屏幕读取要 20ms 量级）。
        private void SyncIslandBackdrop()
        {
            // 亚克力与液态玻璃共用同一条抓屏管线，只是降采样倍数不同
            bool glass = Renderer.IslandLiquidGlass;
            if ((!Renderer.IslandAcrylic && !glass) || Renderer.FullHideAlpha < 0.5f)
            {
                IslandBackdrop.SetTarget(0, 0, 0, 0);
                IslandBackdrop.ClearMask();
                if (_acrylicOwned != null)
                {
                    Renderer.AcrylicBackdrop = null;
                    _acrylicOwned.Dispose();
                    _acrylicOwned = null;
                }
                LiquidGlass.Invalidate();   // 材质切走就把玻璃缓存也放掉，别白占内存
                return;
            }
            IslandBackdrop.SetMode(glass);

            // 背板刚从「全黑」跳到「有内容」（启动时桌面要十几秒才出来）→ 玻璃缓存作废重算。
            // 等效于用户手动切一次质量档，这也是那个"必须手动刷新"的现象来源。
            if (IslandBackdrop.TakeGlassWake())
            {
                Logger.Info("[亚克力] 背板从全黑变亮，作废玻璃缓存重算");
                LiquidGlass.Invalidate();
            }

            // 换材质 = 换降采样倍数：旧背板（另一倍率下抓的）先丢掉，
            // 否则新算出来的源矩形会指到旧图外面，切换瞬间会闪一块错位的色块。
            if (_lastGlassMode != glass)
            {
                _lastGlassMode = glass;
                if (_acrylicOwned != null)
                {
                    Renderer.AcrylicBackdrop = null;
                    _acrylicOwned.Dispose();
                    _acrylicOwned = null;
                }
                LiquidGlass.Invalidate();
                IslandBackdrop.Expire();
            }

            // 岛体画布比屏幕宽：中心对齐屏幕中心后，画布两侧各挂出屏幕外一截（给展开/滑动留余量）。
            // ⚠️ 抓取矩形必须与屏幕求交，否则会把屏幕外的黑一起抓下来 → 背板全黑 = 亚克力「消失」。
            //    （真凶实证：app.log 出现过 屏幕矩形=(-663,0,4399,483) 背板平均 RGB=(0,0,0)）
            int dstX = _cachedMonitorX + (_cachedMonitorWidth - _scaledWidth) / 2;
            int dstY = _cachedMonitorY + (int)(Renderer.IslandBaseY * _dpiScale) + (int)_currentY;

            // 抓取矩形只认**静态基准位置**，不跟 _currentY（滑动/下沉动画的偏移）。
            // 实测启动时 _currentY 会卡在 500 左右，矩形跟着跑到 (0,510) 抓到屏幕中部，
            // 玻璃拿到的是一块错位画面 —— 看着就是"没渲染，手动点一下才正常"。
            int capBaseY = _cachedMonitorY + (int)(Renderer.IslandBaseY * _dpiScale);
            int capX = Math.Max(_cachedMonitorX, dstX);
            int capY = Math.Max(_cachedMonitorY, capBaseY);
            int capW = Math.Min(_cachedMonitorX + _cachedMonitorWidth, dstX + _scaledWidth) - capX;
            int capH = Math.Min(_cachedMonitorY + _cachedMonitorHeight, capBaseY + _scaledHeight) - capY;
            // 放大镜是 1:1 全分辨率，"整块画布"那么大一张图没必要 —— 玻璃只用到屏幕上边一条。
            // 按 32px 量化，免得胶囊高度一动就重抓。
            if (glass && MagnifierBackdrop.IsAvailable && capH > 120)
            {
                int need = (int)Math.Ceiling((_currentHeight + 60f) * _dpiScale);
                if (need < 120) need = 120;
                need = (need + 31) / 32 * 32;
                if (capH > need) capH = need;
            }

            // 交矩形与胶囊尺寸无关 → 胶囊变大变小只挪源矩形，不重抓，尺寸变换期间不闪
            if (capW < 16 || capH < 16)
            {
                IslandBackdrop.SetTarget(0, 0, 0, 0);
                IslandBackdrop.ClearMask();
                return;
            }

            // 胶囊在屏幕上的物理矩形：抓屏线程要靠它把岛体自己从背板里抹掉。
            // ⚠️ 遮罩一律记【屏幕物理像素】。换成图像坐标就必须借 LastScale/LastOrigin，
            //    而那两个值在首帧还没产出过图、是默认的 1/12，于是遮罩只有 1/12 大小、位置也不对，
            //    岛体自己原封不动留在背板里；玻璃的源矩形又正好是岛体那块 ⇒ 玻璃采到自己的输出，
            //    45/46 的灰度成了不动点（"无法透过玻璃看到后面，点一下质量档才好"就是这么来的）。
            float left = (Renderer.WINDOW_WIDTH - _currentWidth) / 2f;
            float topY = 12f * _currentStyleProgress;
            float islandX = dstX + left * _dpiScale;
            float islandY = dstY + topY * _dpiScale;
            float islandW = _currentWidth * _dpiScale;
            float islandH = _currentHeight * _dpiScale;

            // 刘海模式顶角向外张开的那两块角料也属于岛体：玻璃会在那里采样，
            // 遮罩必须一起盖住，否则 GDI 那条线会把岛体自己的像素留在背板里（玻璃采到自己上一帧的输出）。
            float flare = 0f;
            if (_currentStyleProgress < 1f)
            {
                float islandRadius = MathF.Min(_currentHeight / 2f, 20f);
                float rTopX = -Renderer.OUTER_R * (1f - _currentStyleProgress)
                              + islandRadius * _currentStyleProgress;
                if (rTopX < 0f) flare = -rTopX * _dpiScale;
            }

            // 先告诉抓屏线程「岛体占哪块」（抓完要把这块抹掉），再给抓取矩形 —— 顺序反了首帧可能抓到没抹的
            IslandBackdrop.SetMask(islandX - flare, islandY, islandX + islandW + flare, islandY + islandH);

            // 贴图用的源矩形仍按【实际产出图】的比例与原点换算：放大镜是 1:1，GDI 是 1/k，切换材质时也不会错位
            float inv = IslandBackdrop.LastScale;
            float orgX = IslandBackdrop.LastOriginX;
            float orgY = IslandBackdrop.LastOriginY;
            float srcL = (islandX - orgX) * inv;
            float srcT = (islandY - orgY) * inv;
            float srcW = islandW * inv;
            float srcH = islandH * inv;

            IslandBackdrop.SetTarget(capX, capY, capW, capH);

            var fresh = IslandBackdrop.TakePending();
            if (fresh != null)
            {
                _acrylicOwned?.Dispose();
                _acrylicOwned = fresh;
                Renderer.AcrylicBackdrop = fresh;
                if (!_acrLogged)
                {
                    _acrLogged = true;
                    Logger.Info($"[亚克力] 渲染侧拿到背板 {fresh.Width}x{fresh.Height}");
                }
            }

            _acrFrames++;

            // 启动刷新：等岛体**完全落位**（淡入完成、没有滑动/下沉偏移）再做，
            // 动作与设置页点一次「玻璃质量」逐句对应（见 ConsoleWindow.Click）：
            //   LiquidGlass.Quality = 原档 → 强制走 ApplyQuality（ReapplyQuality），
            //   再 RequestAcrylicRebuild() —— 就是点击分支最后那一句。
            // ⚠️ 用户点的是「原来那档」，setter 会早退，所以必须用 ReapplyQuality 才等效。
            //
            // ⚠️ 但这把刷新**必须等到背板真的有内容**再收工：启动时显示器/会话可能还没出画面，
            //    首帧背板就是全黑，0.8 秒一把梭刷新等于白做（实测刷完玻璃依旧是 45/46 的死灰）。
            //    抓屏线程的内容守卫（IslandBackdrop.TrackContent）会判定并重抓；这里只在
            //    "有内容"之后做那一次等价点击的刷新，之前按 600ms 节奏催一下。
            long nowMs = Environment.TickCount64;
            if (_bootAtMs == 0) _bootAtMs = nowMs;

            bool settled = nowMs - _bootAtMs >= 400
                           && Renderer.FullHideAlpha >= 0.9f
                           && Math.Abs(_currentY) < 0.5f;

            // 非玻璃材质没有内容守卫（TrackContent 只在液态玻璃下跑），按老规矩一把梭
            bool contentReady = !Renderer.IslandLiquidGlass || IslandBackdrop.IsContentAlive;

            if (settled && !_acrBootRefresh)
            {
                if (contentReady)
                {
                    _acrBootRefresh = true;
                    Logger.Info("[亚克力] 启动刷新玻璃（等价于设置里点一次玻璃质量）");
                    LiquidGlass.ReapplyQuality();
                    RequestAcrylicRebuild();
                }
                else if (nowMs - _acrBootRetryMs >= 600)
                {
                    _acrBootRetryMs = nowMs;
                    _acrBootRetries++;
                    if (_acrBootRetries <= 8)
                    {
                        // 背板还是黑的：先按点击的同款动作强推一次（新建 DC/DIB 能治好"GDI 抓住黑帧"），
                        // 但自愈不在这里收工 —— 等守卫判出"有内容"，再走上面那一支。
                        Logger.Info($"[亚克力] 启动时背板还没有内容，第 {_acrBootRetries} 次强制重抓");
                        IslandBackdrop.Expire();
                        LiquidGlass.Invalidate();
                    }
                    else
                    {
                        // 约 5 秒都没内容：交给内容守卫（死黑超时自己重建、恢复时自己 Invalidate），
                        // 这里收工，别每帧再惦记。
                        _acrBootRefresh = true;
                        Logger.Warn("[亚克力] 启动自愈放弃：背板长时间无内容，转交内容守卫");
                    }
                }
            }

            Renderer.AcrylicBackdropSrc = new SKRect(srcL, srcT, srcL + srcW, srcT + srcH);
        }

        // 设置面板切材质 / 换主题后调用（UI 线程）：标记过期，后台线程下一轮重抓
        public static void RequestAcrylicRebuild()
        {
            IslandBackdrop.Expire();
            // 启动时是「实体」材质的机器，切到玻璃时放大镜还没建过 —— 这里补一次，
            // 走消息是为了保证它一定发生在 UI 线程（本方法也可能被系统主题事件那条线调用）
            EnsureMagnifier(force: false);
        }

        /// <summary>
        /// 把「放大镜初始化」送到 UI 线程上执行。force = true 会拆掉旧的重建一遍（手动重试）。
        /// </summary>
        public static void EnsureMagnifier(bool force)
        {
            IntPtr h = InstanceHandle;
            if (h != IntPtr.Zero)
                Win32.PostMessage(h, MagNative.WM_APP_ENSURE_MAG, force ? (IntPtr)1 : IntPtr.Zero, IntPtr.Zero);
        }

        private static void ShutdownIslandBackdrop()
        {
            IslandBackdrop.Stop();
            MagnifierBackdrop.Stop();
        }
    }
}
