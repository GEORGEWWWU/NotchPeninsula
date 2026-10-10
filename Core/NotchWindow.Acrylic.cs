using SkiaSharp;

namespace NotchPeninsula
{
    public partial class NotchWindow
    {
        // 渲染线程独占的一份背板引用：只有它能释放，避免和后台线程抢着析构
        private SKImage? _acrylicOwned;

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

            int dstX = _cachedMonitorX + (_cachedMonitorWidth - _scaledWidth) / 2;
            int dstY = _cachedMonitorY + (int)(Renderer.IslandBaseY * _dpiScale) + (int)_currentY;
            IslandBackdrop.SetTarget(dstX, dstY, _scaledWidth, _scaledHeight, _hwnd);

            var fresh = IslandBackdrop.TakePending();
            if (fresh != null)
            {
                _acrylicOwned?.Dispose();
                _acrylicOwned = fresh;
                Renderer.AcrylicBackdrop = fresh;
            }

            // 胶囊在画布里的位置：x 居中，y 平移了 topY（画布已整体 Translate，所以源矩形要加上 topY）
            const float k = IslandBackdrop.Downscale;
            float left = (Renderer.WINDOW_WIDTH - _currentWidth) / 2f;
            float topY = 12f * _currentStyleProgress;
            float s = _dpiScale / k;
            Renderer.AcrylicBackdropSrc = new SKRect(
                left * s, topY * s,
                (left + _currentWidth) * s, (topY + _currentHeight) * s);
        }

        // 设置面板切材质 / 换主题后调用（UI 线程）：标记过期，后台线程下一轮重抓
        public static void RequestAcrylicRebuild() => IslandBackdrop.Expire();

        private static void ShutdownIslandBackdrop() => IslandBackdrop.Stop();
    }
}
