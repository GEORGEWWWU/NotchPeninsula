using SkiaSharp;

namespace NotchPeninsula
{
    public partial class NotchWindow
    {
        // 渲染线程独占的一份背板引用：只有它能释放，避免和后台线程抢着析构
        private SKImage? _acrylicOwned;
        private int _acrFrames;
        private bool _acrLogged;

        // 亚克力背板：抓的是【整个画布框】，不是当前胶囊框。
        // 这样胶囊变大变小（悬停展开 / 待机↔媒体切换）时只挪源矩形，完全不用重抓 —— 尺寸变换期间的闪烁就没了。
        // 渲染线程这里只写「要抓哪块」+ 取成品，真正的抓屏在 NPS-Backdrop 线程上（屏幕读取要 20ms 量级）。
        private void SyncIslandBackdrop()
        {
            if (!Renderer.IslandAcrylic || Renderer.FullHideAlpha < 0.5f)
            {
                IslandBackdrop.SetTarget(0, 0, 0, 0, _hwnd);
                if (_acrylicOwned != null)
                {
                    Renderer.AcrylicBackdrop = null;
                    _acrylicOwned.Dispose();
                    _acrylicOwned = null;
                }
                return;
            }

            // 岛体画布比屏幕宽：中心对齐屏幕中心后，画布两侧各挂出屏幕外一截（给展开/滑动留余量）。
            // ⚠️ 抓取矩形必须与屏幕求交，否则会把屏幕外的黑一起抓下来 → 背板全黑 = 亚克力「消失」。
            //    （真凶实证：app.log 出现过 屏幕矩形=(-663,0,4399,483) 背板平均 RGB=(0,0,0)）
            int dstX = _cachedMonitorX + (_cachedMonitorWidth - _scaledWidth) / 2;
            int dstY = _cachedMonitorY + (int)(Renderer.IslandBaseY * _dpiScale) + (int)_currentY;
            int capX = Math.Max(_cachedMonitorX, dstX);
            int capY = Math.Max(_cachedMonitorY, dstY);
            int capW = Math.Min(_cachedMonitorX + _cachedMonitorWidth, dstX + _scaledWidth) - capX;
            int capH = Math.Min(_cachedMonitorY + _cachedMonitorHeight, dstY + _scaledHeight) - capY;
            // 交矩形与胶囊尺寸无关 → 胶囊变大变小只挪源矩形，不重抓，尺寸变换期间不闪
            if (capW < 16 || capH < 16)
            {
                IslandBackdrop.SetTarget(0, 0, 0, 0, _hwnd);
                return;
            }
            IslandBackdrop.SetTarget(capX, capY, capW, capH, _hwnd);

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

            // 开着一秒还没拿到背板 → 只报一次，说明后台线程没产出
            _acrFrames++;
            if (_acrFrames == 120 && Renderer.AcrylicBackdrop == null)
                Logger.Warn("[亚克力] 开启 120 帧仍未拿到背板（后台抓屏线程无产出）");

            // 胶囊在画布里的物理位置 → 换算成背板图像素（背板 = 交矩形 ÷ Downscale）。
            // 画布 x 原点 = dstX，胶囊左边缘 = dstX + left·dpi，所以相对交矩形要再减 capX。
            float left = (Renderer.WINDOW_WIDTH - _currentWidth) / 2f;
            float topY = 12f * _currentStyleProgress;
            float k = IslandBackdrop.Downscale;
            float srcL = (dstX + left * _dpiScale - capX) / k;
            float srcT = (dstY + topY * _dpiScale - capY) / k;
            float srcW = _currentWidth * _dpiScale / k;
            float srcH = _currentHeight * _dpiScale / k;
            Renderer.AcrylicBackdropSrc = new SKRect(srcL, srcT, srcL + srcW, srcT + srcH);
        }

        // 设置面板切材质 / 换主题后调用（UI 线程）：标记过期，后台线程下一轮重抓
        public static void RequestAcrylicRebuild() => IslandBackdrop.Expire();

        private static void ShutdownIslandBackdrop() => IslandBackdrop.Stop();
    }
}
