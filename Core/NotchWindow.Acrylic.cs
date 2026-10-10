namespace NotchPeninsula
{
    public partial class NotchWindow
    {
        // 亚克力模式下，每帧（帧首）把胶囊正后方的屏幕矩形喂给背板抓取器。
        // 矩形口径必须与 Renderer 画胶囊时用的完全同源，否则背板会错位。
        private void SyncIslandBackdrop()
        {
            if (!Renderer.IslandAcrylic || Renderer.FullHideAlpha < 0.5f)
            {
                if (Renderer.AcrylicBackdrop != null)
                {
                    Renderer.AcrylicBackdrop = null;
                    IslandBackdrop.Reset();
                }
                return;
            }

            int dstX = _cachedMonitorX + (_cachedMonitorWidth - _scaledWidth) / 2;
            int dstY = _cachedMonitorY + (int)(Renderer.IslandBaseY * _dpiScale) + (int)_currentY;

            int left = (int)((Renderer.WINDOW_WIDTH - _currentWidth) / 2f * _dpiScale);
            int top = (int)(12f * _currentStyleProgress * _dpiScale);
            int pw = (int)(_currentWidth * _dpiScale);
            int ph = (int)(_currentHeight * _dpiScale);

            IslandBackdrop.Sync(dstX + left, dstY + top, pw, ph, _hwnd);
            Renderer.AcrylicBackdrop = IslandBackdrop.Image;
        }

        // 设置面板切材质 / 换主题后调用（UI 线程）：标记过期，渲染线程下一帧重抓
        public static void RequestAcrylicRebuild() => IslandBackdrop.Expire();
    }
}
