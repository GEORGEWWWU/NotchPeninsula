using SkiaSharp;
using System.Numerics;
using Windows.System;

namespace NotchPeninsula
{
    public partial class NotchWindow
    {
        // 渲染线程独占的一份背板引用：只有它能释放，避免和后台线程抢着析构
        private SKImage? _acrylicOwned;
        private NativeAcrylic? _nativeAcrylic;
        private DispatcherQueueController? _acrylicDispatcher;
        private bool _nativeAcrylicFailed;
        private int _lastNativeMaterial = -1;

        private static bool UsesLegacyAcrylic => !NativeAcrylic.IsSupported && Renderer.IslandAcrylic;

        private void PresentNativeBackdrop(int x, int y)
        {
            if (!NativeAcrylic.IsSupported) return;

            int material = Renderer.IslandMaterialIndex;
            if (_lastNativeMaterial != material)
            {
                _lastNativeMaterial = material;
                // 切回亚克力时重新建立 DWM 背板，包括宿主创建成功但没有出图的情况。
                ReleaseNativeBackdrop();
                _nativeAcrylicFailed = false;
            }

            // 透明度档位只调整前景色调，原生背板随岛体的隐藏和穿透动画淡出。
            float opacity = Renderer.FullHideAlpha * Renderer.PassthroughAlpha;
            // 静默模式是「缩向中心」，不走 FullHideAlpha —— 而亚克力背板是 DWM 合成的，
            // 不跟着 Skia 画布一起缩，所以必须自己乘 SilentScale：
            // 否则岛缩没后，原位置会留一块空白的亚克力矩形（实体材质 / 液态玻璃都画在画布里，没这问题）。
            if (Renderer.SilentActive) opacity *= Math.Clamp(Renderer.SilentScale, 0f, 1f);
            if (material != 1 || _nativeAcrylicFailed || opacity <= 0.001f
                || _currentWidth * _dpiScale < 1f || _currentHeight * _dpiScale < 1f)
            {
                HideNativeBackdrop();
                return;
            }

            try
            {
                bool newHost = _nativeAcrylic == null;
                _acrylicDispatcher ??= NativeAcrylic.CreateDispatcher();
                _nativeAcrylic ??= new NativeAcrylic(_hwnd);

                // 与 Renderer.Draw 的胶囊使用同一帧尺寸和逻辑坐标。
                var outline = IslandOutline.Create(_currentHeight, _currentStyleProgress);
                float left = (Renderer.WINDOW_WIDTH - _currentWidth) / 2f * _dpiScale;
                float top = IslandOutline.CapsuleOffset * _currentStyleProgress * _dpiScale;
                var size = new Vector2(_currentWidth, _currentHeight) * _dpiScale;
                float flare = outline.Flare * _dpiScale;
                int leftPx = (int)MathF.Floor(left - flare);
                int topPx = (int)MathF.Floor(top);
                int rightPx = (int)MathF.Ceiling(left + size.X + flare);
                int bottomPx = (int)MathF.Ceiling(top + size.Y);
                var offset = new Vector2(left - leftPx, top - topPx);
                var scaledOutline = new IslandOutline(outline.TopX * _dpiScale, outline.TopY * _dpiScale, outline.Bottom * _dpiScale);
                _nativeAcrylic.Present(_hwnd, x + leftPx, y + topPx, rightPx - leftPx, bottomPx - topPx,
                    offset, size, scaledOutline, _currentStyleProgress == 1f, opacity);
                if (newHost) Logger.Info("[亚克力] 已建立 Windows Composition 背板宿主");
            }
            catch (Exception ex)
            {
                _nativeAcrylicFailed = true;
                Logger.Error("[背景材质] Windows Composition 宿主失败，使用色调兜底", ex);
                ReleaseNativeBackdrop();
            }
        }

        private void ReleaseNativeBackdrop()
        {
            var backdrop = _nativeAcrylic;
            _nativeAcrylic = null;
            try { backdrop?.Dispose(); }
            catch (Exception ex) { Logger.Error("[背景材质] 释放 Composition 宿主失败", ex); }
        }

        private void HideNativeBackdrop()
        {
            _nativeAcrylic?.Hide();
        }

        private bool _acrLogged;
        private bool _legacyCaptureActive;
        private bool _legacyCaptureGlass;
        private float _captureOriginX, _captureOriginY, _captureScale;
        private long _legacyFirstCaptureMs;
        private bool _legacyRefreshedAfterStartup;
        // 背板自愈用：最近一次看到的背板版本号 + 它的时间戳（见 SyncIslandBackdrop）
        private int _lastBackdropVer = -1;
        private long _lastBackdropVerMs;

        // 记录抓屏路径的无效目标。
        private long _backdropSkipMs;
        private string _backdropSkipWhy = "";

        // 额外遮罩（详情页面板 + 实况窗气泡）的 flat 缓冲：渲染线程独占，稳态零分配。
        private static readonly float[] _extraMaskScratch = new float[IslandBackdrop.MaxExtraMaskRects * 4];

        private void LogBackdropSkip(string why)
        {
            long now = Environment.TickCount64;
            if (why == _backdropSkipWhy && now - _backdropSkipMs < 5000) return;
            _backdropSkipWhy = why;
            _backdropSkipMs = now;
            Logger.Warn($"[背板] 跳过抓屏：{why}");
        }

        // 亚克力背板：抓的是【整个画布框 ∩ 屏幕】，不是当前胶囊框。
        // 这样胶囊变大变小（悬停展开 / 待机↔媒体切换）时只挪源矩形，完全不用重抓 —— 尺寸变换期间的闪烁就没了。
        // 渲染线程这里只写「要抓哪块」+「岛体占哪块」+ 取成品，真正的抓屏在 NPS-Backdrop 线程上（屏幕读取要 20ms 量级）。
        private void SyncIslandBackdrop()
        {
            // 显示器拓扑刚变化时（WM_DISPLAYCHANGE 把 _cachedMonitorIndex 置 -1），
            // UpdateWindow 还没跑（它在本帧更后面），但抓取矩形马上要用 _cachedMonitor*。
            // 先在这里重算一次，否则会把旧显示器的坐标喂给抓屏线程。
            if (NeedsMonitorRefresh()) UpdateMonitorBounds();

            bool glass = Renderer.IslandLiquidGlass;
            if ((!glass && !UsesLegacyAcrylic) || Renderer.FullHideAlpha < 0.5f)
            {
                if (_legacyCaptureActive)
                {
                    _legacyCaptureActive = false;
                    IslandBackdrop.SetTarget(0, 0, 0, 0);
                    IslandBackdrop.ClearMask();
                    IslandBackdrop.SetMode(false);
                    IslandBackdrop.Expire();
                    Renderer.AcrylicBackdrop = null;
                    _acrylicOwned?.Dispose();
                    _acrylicOwned = null;
                    GlassMaterial.BackdropReady = false;
                    if (_legacyCaptureGlass) LiquidGlass.Invalidate();
                }
                return;
            }
            if (!_legacyCaptureActive || _legacyCaptureGlass != glass)
            {
                IslandBackdrop.SetTarget(0, 0, 0, 0);
                IslandBackdrop.Expire();
                IslandBackdrop.SetMode(glass);
                _acrLogged = false;
                Renderer.AcrylicBackdrop = null;
                _acrylicOwned?.Dispose();
                _acrylicOwned = null;
                GlassMaterial.BackdropReady = false;
                if (_legacyCaptureGlass) LiquidGlass.Invalidate();
                _legacyCaptureGlass = glass;
                _legacyCaptureActive = true;
            }
            if (glass)
            {
                GlassMaterial.SyncCaptureSettings();
                if (IslandBackdrop.TakeGlassWake()) LiquidGlass.Invalidate();
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
            if (glass && IslandBackdrop.DxgiAllowed && DxgiBackdrop.Available)
            {
                float needed = Math.Max(_currentHeight + 60f, LiveWindowHost.RequiredWindowHeight + 64f);
                int rows = Math.Max(120, (int)MathF.Ceiling(needed * _dpiScale));
                capH = Math.Min(capH, (rows + 31) / 32 * 32);
            }

            // 交矩形与胶囊尺寸无关 → 胶囊变大变小只挪源矩形，不重抓，尺寸变换期间不闪
            if (capW < 16 || capH < 16)
            {
                LogBackdropSkip($"抓取矩形无效 {capW}x{capH}@({capX},{capY}) 屏幕={_cachedMonitorX},{_cachedMonitorY},{_cachedMonitorWidth}x{_cachedMonitorHeight} dpi={_dpiScale:F2}");
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
            float topY = IslandOutline.CapsuleOffset * _currentStyleProgress;
            float islandX = dstX + left * _dpiScale;
            float islandY = dstY + topY * _dpiScale;
            float islandW = _currentWidth * _dpiScale;
            float islandH = _currentHeight * _dpiScale;

            // 刘海模式顶角向外张开的那两块角料也属于岛体：玻璃会在那里采样，
            // 遮罩必须一起盖住，否则 GDI 那条线会把岛体自己的像素留在背板里（玻璃采到自己上一帧的输出）。
            float flare = IslandOutline.Create(_currentHeight, _currentStyleProgress).Flare * _dpiScale;

            // 先告诉抓屏线程「岛体占哪块」（抓完要把这块抹掉），再给抓取矩形 —— 顺序反了首帧可能抓到没抹的
            IslandBackdrop.SetMask(islandX - flare, islandY, islandX + islandW + flare, islandY + islandH);

            // 实况窗（详情页面板 + 左侧气泡）也要计入遮罩，口径同样是屏幕物理像素。
            {
                var liveFrame = LiveWindowHost.Frame;
                // 面板没展开时先把「面板区域还是纯桌面」的这一帧背板存一份，供面板展开后做逐元素明暗。
                if (!liveFrame.PanelVisible) Renderer.SnapshotPanelToneBackdrop(Renderer.AcrylicBackdrop);
                var extra = _extraMaskScratch;
                int n = 0;
                if (liveFrame.PanelVisible && liveFrame.PanelRect.Width > 1f && liveFrame.PanelRect.Height > 1f)
                {
                    var pr = liveFrame.PanelRect;
                    extra[n++] = dstX + pr.Left * _dpiScale;
                    extra[n++] = dstY + pr.Top * _dpiScale;
                    extra[n++] = dstX + pr.Right * _dpiScale;
                    extra[n++] = dstY + pr.Bottom * _dpiScale;
                }
                for (int i = 0; i < liveFrame.Count && n + 4 <= extra.Length; i++)
                {
                    ref readonly var item = ref liveFrame.Items[i];
                    if (item.Alpha < 16 || item.Rect.Width < 1f || item.Rect.Height < 1f) continue;
                    var r = item.Rect;
                    extra[n++] = dstX + r.Left * _dpiScale;
                    extra[n++] = dstY + r.Top * _dpiScale;
                    extra[n++] = dstX + r.Right * _dpiScale;
                    extra[n++] = dstY + r.Bottom * _dpiScale;
                }
                IslandBackdrop.SetExtraMasks(extra.AsSpan(0, n));
            }

            // 贴图用的源矩形按【实际产出图】的比例与原点换算
            IslandBackdrop.SetTarget(capX, capY, capW, capH);

            var fresh = IslandBackdrop.TakePending();
            if (fresh != null)
            {
                if (fresh.Glass != glass) fresh.Image.Dispose();
                else
                {
                    _acrylicOwned?.Dispose();
                    _acrylicOwned = fresh.Image;
                    _captureOriginX = fresh.X;
                    _captureOriginY = fresh.Y;
                    _captureScale = fresh.Scale;
                    Renderer.AcrylicBackdrop = fresh.Image;
                    GlassMaterial.BackdropReady = glass;
                    GlassMaterial.BackdropVersion = fresh.Version;
                    if (!_acrLogged)
                    {
                        _acrLogged = true;
                        Logger.Info($"[{(glass ? "液态玻璃" : "亚克力")}] 渲染侧拿到背板 {fresh.Image.Width}x{fresh.Image.Height}");
                    }
                }
            }

            int version = IslandBackdrop.Version;
            long now = Environment.TickCount64;
            if (!_legacyRefreshedAfterStartup)
            {
                if (_legacyFirstCaptureMs == 0) _legacyFirstCaptureMs = now;
                if (now - _legacyFirstCaptureMs >= 400 && Renderer.FullHideAlpha >= 0.9f
                    && Math.Abs(_currentY) < 0.5f)
                {
                    _legacyRefreshedAfterStartup = true;
                    IslandBackdrop.Expire();
                }
            }

            if (version != _lastBackdropVer)
            {
                _lastBackdropVer = version;
                _lastBackdropVerMs = now;
            }
            else if (now - _lastBackdropVerMs > 15000 && IslandBackdrop.CaptureFailStreak >= 3)
            {
                _lastBackdropVerMs = now;
                Logger.Warn($"[背板] 15 秒没有新背板且抓屏连续失败 {IslandBackdrop.CaptureFailStreak} 次，重建背板");
                IslandBackdrop.Expire();
            }

            Renderer.AcrylicBackdropSrc = new SKRect(
                (islandX - _captureOriginX) * _captureScale, (islandY - _captureOriginY) * _captureScale,
                (islandX + islandW - _captureOriginX) * _captureScale, (islandY + islandH - _captureOriginY) * _captureScale);
        }

        // 设置面板切材质 / 换主题后调用（UI 线程）：标记过期，后台线程下一轮重抓
        public static void RequestAcrylicRebuild()
        {
            if (UsesLegacyAcrylic || Renderer.IslandLiquidGlass) IslandBackdrop.Expire();
        }

        private static void ShutdownIslandBackdrop() => IslandBackdrop.Stop();
    }
}
