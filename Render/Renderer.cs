using SkiaSharp;
using System.Drawing.Imaging;
using System.IO;

namespace NotchPeninsula
{
    public static partial class Renderer
    {
        // 1. 布局核心参数 (改为无锁动态变量)
        private static volatile float _standbyWidth = 125f;

        private static volatile float _mediaWidth = 250f;

        private static volatile float _mediaHeight = 35f;

        private static volatile float _toastWidth = 260f;

        private static volatile float _toastHeight = 55f;

        private static volatile float _globalDpi = 1.0f;

        private static volatile float _notchBottomRadius = 12f;

        public static float STANDBY_WIDTH { get => _standbyWidth; set => _standbyWidth = value; }

        public static float MEDIA_WIDTH { get => _mediaWidth; set => _mediaWidth = value; }

        public static float MEDIA_HEIGHT { get => _mediaHeight; set => _mediaHeight = value; }

        public static float TOAST_WIDTH { get => _toastWidth; set => _toastWidth = value; }

        public static float TOAST_HEIGHT { get => _toastHeight; set => _toastHeight = value; }

        public static bool IsToastFullMode = false;

        public static bool IsToastCompactMode = false;
        // 紧凑模式下右侧双行信息预留宽度

        public static readonly float COMPACT_RIGHT_WIDTH = 90f;

        public static readonly float FULL_TOAST_MIN_WIDTH = 300f;

        public static readonly float FULL_TOAST_MIN_HEIGHT = 72f;

        public static float GLOBAL_DPI { get => _globalDpi; set => _globalDpi = value; }

        public static float NOTCH_BOTTOM_RADIUS { get => _notchBottomRadius; set => _notchBottomRadius = value; }

        private static int _themeMode = 0; // 0=黑, 1=白, 2=跟随系统

        public static int ThemeMode
        {
            get => _themeMode;
            set
            {
                if (_themeMode == value) return;
                _themeMode = value;
                InvalidateSystemThemeCache();
            }
        }

        // 虽然只在主题变更时调用，但同样白读。
        // 缓存失效点（只有这三种，够了）：
        private static bool _systemIsLightTheme;

        private static bool _systemThemeCached;

        public static bool SystemIsLightTheme
        {
            get
            {
                if (_systemThemeCached) return _systemIsLightTheme;

                bool light = false;
                try
                {
                    using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                        @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
                    light = key?.GetValue("AppsUseLightTheme") is int val && val == 1;
                }
                catch
                {
                }

                _systemIsLightTheme = light;
                _systemThemeCached = true;
                return light;
            }
        }

        public static void InvalidateSystemThemeCache()
        {
            _systemThemeCached = false;
        }

        public static int NotchStyle { get; set; } = 0; // 0=经典刘海, 1=灵动岛

        public static int StandbyDisplayMode { get; set; } = 0; // 旧「待机显示内容」（0=时间日期, 1=空白, 2=硬件占用）：已被显示设置的复选框取代，只用于老配置迁移

        // 待机显示内容：空串 = 空白；内置项用 BuiltinWidgets.* 键；其余是插件 Key（设置页下拉里选的就是它）。
        public static string StandbySceneKey { get; set; } = Plugins.BuiltinWidgets.Clock;

        public static bool StandbyActive
        {
            get => _standbyActive;
            set
            {
                if (_standbyActive == value) return;
                _standbyActive = value;
                try { StandbyActiveChanged?.Invoke(); }
                catch (Exception ex) { Logger.Error("[Renderer] StandbyActiveChanged 事件处理异常", ex); }
            }
        }
        private static bool _standbyActive;

        // 诊断：背板为空只报一次（玻璃模式下背板为空 = 玻璃层整帧不画）

        // 「内容整体不可见」诊断的日志节流
        private static long _alphaDiagMs;

        public static event Action? StandbyActiveChanged;

        public static bool StandbyToggleByDoubleClick { get; set; } = false;

        // 材质编号与设置页及引导页一致：0 实体，1 亚克力，2 液态玻璃。
        private static volatile int _materialIndex;
        public static int IslandMaterialIndex
        {
            get => _materialIndex;
            set => _materialIndex = Math.Clamp(value, 0, 2);
        }

        public static bool IslandAcrylic => IslandMaterialIndex == 1;
        public static bool IslandLiquidGlass => IslandMaterialIndex == 2;

        // 亚克力背板：已模糊好的桌面截图，由 NotchWindow 每帧喂进来（只允许渲染线程读写）
        public static SKImage? AcrylicBackdrop { get; set; }

        // 背板上「本轮胶囊」对应的源矩形（图像像素坐标）——
        // 背板抓的是整个画布框，所以胶囊变大变小时只挪源矩形，不用重抓，避免闪烁
        public static SKRect AcrylicBackdropSrc { get; set; }

        // ---- 画布逻辑坐标 → 背板图像坐标的映射（本帧有效）----
        // 实况窗的详情页面板画在岛外，但和岛体吃同一张背板 / 同一套材质；
        // 映射基准就是「岛体 canvas 矩形 ↔ AcrylicBackdropSrc」，唯一真源，面板只是同一个线性换算。
        private static bool _backdropMapValid;
        private static float _backdropMapCanvasLeft, _backdropMapCanvasW, _backdropMapCanvasH;
        private static SKRect _backdropMapSrc;

        internal static void UpdateBackdropCanvasMapping(bool valid, float canvasLeft,
            float canvasW, float canvasH, SKRect src)
        {
            _backdropMapValid = valid && canvasW > 1f && canvasH > 1f
                                && src.Width > 2f && src.Height > 2f;
            if (!_backdropMapValid) return;
            _backdropMapCanvasLeft = canvasLeft;
            _backdropMapCanvasW = canvasW;
            _backdropMapCanvasH = canvasH;
            _backdropMapSrc = src;
        }

        /// <summary>把画布逻辑矩形换算成当前背板上的源矩形；面板材质 / 玻璃合成用。</summary>
        internal static bool TryCanvasToBackdrop(SKRect canvasRect, out SKRect src)
        {
            src = default;
            if (!_backdropMapValid) return false;
            float kx = _backdropMapSrc.Width / _backdropMapCanvasW;
            float ky = _backdropMapSrc.Height / _backdropMapCanvasH;
            src = new SKRect(
                _backdropMapSrc.Left + (canvasRect.Left - _backdropMapCanvasLeft) * kx,
                _backdropMapSrc.Top + canvasRect.Top * ky,
                _backdropMapSrc.Left + (canvasRect.Right - _backdropMapCanvasLeft) * kx,
                _backdropMapSrc.Top + canvasRect.Bottom * ky);
            return src.Width >= 4f && src.Height >= 2f;
        }

        /// <summary>当前材质的背景涂层色（亚克力 / 实体那层色调）：实况窗详情页面板直接复用。</summary>
        internal static SKColor MaterialCoatColor => _bgPaint.Color;

        public static int TargetMonitorIndex { get; set; } = 0; // 目标显示器索引
        // 最近一次有效目标显示器的记忆值（注册表键 TargetMonitorIndex_Remembered）。
        // 目标显示器只是暂时不在（KVM 切走输入源、重启后副屏未唤醒）时，用它兜底；
        // 等显示器回来后按 TargetMonitorIndex 里保留的用户偏好自动恢复。
        public static int RememberedMonitorIndex { get; set; } = -1;

        // ---- 实体和亚克力的背景透明度分别保存 ----
        // 档位 0..4 = 0% / 25% / 50% / 75% / 100%，数值越大涂层越厚（越不透明）。
        // 纯色：alpha 0..255；亚克力：alpha 0..140（上限压到 55%，保住"奶感"）。
        private static int _opacitySolid = 4;
        private static int _opacityAcrylic = 4;

        public static int SolidBgOpacity
        {
            get => _opacitySolid;
            set => _opacitySolid = Math.Clamp(value, 0, 4);
        }

        public static int AcrylicBgOpacity
        {
            get => _opacityAcrylic;
            set => _opacityAcrylic = Math.Clamp(value, 0, 4);
        }

        /// <summary>当前材质那一份透明度档位（滑轨读它）。</summary>
        public static int BgOpacityLevel
        {
            get => IslandAcrylic ? _opacityAcrylic : _opacitySolid;
            set
            {
                if (IslandAcrylic) _opacityAcrylic = Math.Clamp(value, 0, 4);
                else _opacitySolid = Math.Clamp(value, 0, 4);
            }
        }

        /// <summary>当前材质的透明度设置键。</summary>
        public static string BgOpacityKey =>
            IslandAcrylic ? "BgOpacityAcrylic" : "BgOpacityLevel";

        //    「待机显示内容」，勾多个就是多模块并排。
        public static bool CompositeModeEnabled => true;

        public static bool CompShowDateTime { get; set; } = true;  // 显示时间日期

        public static bool CompShowHardware { get; set; } = true;  // 显示硬件占用

        public static bool CompShowMedia { get; set; } = true;     // 显示媒体控制器(含频谱)

        public static bool PassthroughModeEnabled = false; // 穿透模式总开关

        /// <summary>
        /// 媒体内容已交给左侧实况窗显示：岛体不再画媒体（避免「左边显示、右边重复」）。
        /// 由内置的 <c>MediaLiveWindowController</c> 在切换时置位，媒体结束 / 开关关闭时复位。
        /// </summary>
        public static volatile bool SuppressMediaInIsland = false;

        public static float PassthroughAlpha = 1.0f; // 穿透动画平滑插值

        public static volatile bool FileDragInProgress = false;

        public static float IslandBaseY { get; set; } = 0f;

        public static float FullHideAlpha = 1.0f;

        // 手动「隐藏灵动岛」（交互设置页的全局快捷键翻转）。只活在本次运行里：
        // 不落注册表 —— 万一那组键被别的程序占着，重启还能把岛找回来。
        public static volatile bool IslandHidden = false;

        // 媒体交互状态：0=直接交互，1=展开交互(默认)
        public static int MediaInteractionMode = 1;

        // ---- 折叠态媒体区的展开入口（唯一真源） ----
        public static bool MediaExpandByRightClick => MediaInteractionMode == 1 && MediaController.IsAppLaunchEnabled;

        public static bool MediaExpandByLeftClick => MediaInteractionMode == 1 && !MediaController.IsAppLaunchEnabled;

        public const float MAX_ISLAND_WIDTH = 1920f;

        /// <summary>
        /// 岛体实际可用的宽度上限（逻辑像素）：目标显示器工作区宽度与 <see cref="MAX_ISLAND_WIDTH"/> 取小。
        /// 屏幕就是天花板 —— 1366 / 1600 这类窄屏上，拿 1920 当预算基准会让岛体直接出界。
        /// 由 <c>NotchWindow</c> 在显示器拓扑 / DPI 变化时更新。
        /// </summary>
        public static float IslandWidthCap { get; set; } = MAX_ISLAND_WIDTH;

        // 媒体控制器的长度完全放开，因此删掉了两个上限常量：

        // 动态计算最大边界，防止因刘海变大导致出界
        public static float WINDOW_WIDTH => Math.Max(1200f,
            Math.Max(MAX_ISLAND_WIDTH,
                Math.Max(ActiveDetailWidth, Math.Max(STANDBY_WIDTH, Math.Max(MEDIA_WIDTH, TOAST_WIDTH))))) + 80f;

        public static float MAX_WINDOW_HEIGHT => Math.Max(
            Math.Max(220f, Math.Max(ActiveDetailHeight, Math.Max(TOAST_HEIGHT, MEDIA_HEIGHT)) + 45f),
            // 实况窗详情页：RequiredWindowHeight 已含面板高度，这里再给玻璃合成留出边缘折射 / 模糊的采样余量
            // （窗口多出来的那块是透明的，不影响显示；抓屏高度靠它才能盖住面板底部）。
            LiveWindowHost.RequiredWindowHeight > 0f ? LiveWindowHost.RequiredWindowHeight + 72f : 0f);

        public const int OUTER_R = 14;

        public const int INNER_R = 12;

        public static void Draw(SKCanvas canvas, MediaController media, bool isHovered, float currentWidth, float currentHeight, float startupProgress = 1f, float[]? bars = null, ToastData? toast = null, float styleProgress = 0f, float transitionAlpha = 1f)
        {
            if (!System.Threading.Monitor.TryEnter(_renderLock)) return;
            try
            {
                // 本帧频谱留给 LiveWindowRenderer 用：实况窗详情页复用的就是主岛那块媒体面板，
                // 右边那组律动条必须和岛内是同一份数据。
                _lastBars = bars;

                canvas.Clear(SKColors.Transparent);

                SetHitTestHeight(currentHeight);

                // 每帧清空插件命中区，仅当本帧实际绘制插件行时才重新填充
                InvalidatePluginHitAreas();

                // 时间轴几何登记表帧首作废：本帧不画就等于命中区不存在
                _tlBarX1 = _tlBarX2 = _tlBarY = 0f;

                InvalidateNativeHitZones();

                // 岛体物理左边界（背景形状 / 裁剪范围以它为准）
                float islandLeft = (WINDOW_WIDTH - currentWidth) / 2f;
                // 岛体物理右边界（背景形状 / 裁剪范围以它为准）
                float islandRight = islandLeft + currentWidth;
                // 插件行的位置：
                //     不单独占用预留区；
                //     都不会影响原生功能本身。
                float pluginReserve = toast == null && !CompositeModeEnabled ? GetPluginRowReserve() : 0f;
                string? nativeBuiltinId = CompositeModeEnabled ? null : GetNativeBuiltinId(media.IsActive && !SuppressMediaInIsland);
                int nativeOrderIndex = OrderIndexOf(nativeBuiltinId);
                float leftGroupW = 0f, rightGroupW = 0f;
                float leftPluginBlock = 0f;
                float rightPluginBlock = pluginReserve;
                if (pluginReserve > 0f)
                {
                    MeasurePluginSides(nativeOrderIndex, out leftGroupW, out rightGroupW);
                    leftPluginBlock = leftGroupW > 0f ? leftGroupW + PLUGIN_GAP : 0f;
                    rightPluginBlock = rightGroupW > 0f ? rightGroupW + PLUGIN_GAP : 0f;
                    //    原生内容区本来就是空的，没有东西会被负宽度翻面。
                    if (nativeBuiltinId != null)
                    {
                        float totalBlock = leftPluginBlock + rightPluginBlock;
                        float maxBlock = Math.Max(0f, currentWidth - MIN_NATIVE_AREA);
                        if (totalBlock > maxBlock + 0.5f) // 0.5px 容差：稳态下不会触发，别被浮点误差误判
                        {
                            float k = maxBlock / totalBlock;
                            leftPluginBlock *= k;
                            rightPluginBlock *= k;
                        }
                    }
                }
                float left = islandLeft + leftPluginBlock;
                float right = islandRight - rightPluginBlock;
                _compositeMediaRight = CompositeModeEnabled || pluginReserve > 0f ? right : -1f;

                float topY = IslandOutline.CapsuleOffset * styleProgress;

                // 实况窗：独立于主岛、画在岛体左侧，每帧先算布局与动画（插件 GetVisual 也在这里被调）。
                LiveWindowHost.Tick(topY, islandLeft, currentWidth, currentHeight);

                canvas.Save();
                // 整个画布向下平移，让内部所有元素自动完美适应居中
                canvas.Translate(0, topY);

                // 图层只在「整块岛要淡出」时才需要：透明组的作用就是给内部所有像素乘一个 alpha。
                // 稳态 alpha=255 时这层是个恒等变换，但它仍然要为整张画布（2000×265，150% 缩放后
                // 3000×398 ≈ 4.8MB）开一层离屏、清空、再合成一遍 —— 每帧一次纯属白做。
                // 淡入淡出 / 穿透 / 隐藏动画期间照旧开层，观感完全不变。
                byte layerAlpha = (byte)(255 * PassthroughAlpha * FullHideAlpha);
                bool useLayer = layerAlpha < 255;
                if (useLayer)
                {
                    _layerPaint.Color = SKColors.White.WithAlpha(layerAlpha);
                    canvas.SaveLayer(_layerPaint);
                }

                _bgPath.Rewind();

                // 自动把四个圆角调到最大，动态计算插值半径
                // 限制灵动岛展开后的最大圆角为 20f，防止变成大圆球
                var (rTopX, rTopY, rBottom) = IslandOutline.Create(currentHeight, styleProgress);

                float w = 0.70710678f;

                _bgPath.MoveTo(islandLeft + rTopX, 0);
                _bgPath.ConicTo(islandLeft, 0, islandLeft, rTopY, w);
                _bgPath.LineTo(islandLeft, currentHeight - rBottom);
                _bgPath.ConicTo(islandLeft, currentHeight, islandLeft + rBottom, currentHeight, w);
                _bgPath.LineTo(islandRight - rBottom, currentHeight);
                _bgPath.ConicTo(islandRight, currentHeight, islandRight, currentHeight - rBottom, w);
                _bgPath.LineTo(islandRight, rTopY);
                _bgPath.ConicTo(islandRight, 0, islandRight - rTopX, 0, w);
                _bgPath.Close();

                bool glass = IslandLiquidGlass;
                var backdrop = IslandAcrylic ? AcrylicBackdrop : null;
                var source = AcrylicBackdropSrc;
                var destination = new SKRect(islandLeft, 0f, islandRight, currentHeight);
                UpdateBackdropCanvasMapping(backdrop != null, islandLeft, currentWidth, currentHeight, source);

                if (backdrop != null)
                {
                    float flare = Math.Max(0f, -rTopX);
                    var acrylicSource = source;
                    acrylicSource.Inflate(flare * source.Width / Math.Max(1f, currentWidth), 0f);
                    canvas.Save();
                    canvas.ClipPath(_bgPath, SKClipOperation.Intersect, true);
                    canvas.DrawImage(backdrop, acrylicSource,
                        new SKRect(islandLeft - flare, 0f, islandRight + flare, currentHeight), _acrylicPaint);
                    canvas.Restore();
                }

                BeginToneGrid(canvas, backdrop, destination, source);
                if (IslandAcrylic && PluginToneReady)
                    UpdateGlassTextTone((int)MathF.Round(ToneGridAverage * 255f), true);
                if (glass)
                    GlassMaterial.DrawCoating(canvas, _bgPath, _paletteIsLight, 1f, GlassMaterial.BackdropReady);
                else
                    canvas.DrawPath(_bgPath, _bgPaint);

                canvas.Save();
                canvas.ClipPath(_bgPath, SKClipOperation.Intersect, true);

                byte alpha = (byte)(255 * startupProgress * transitionAlpha);
                // 诊断：启动淡入早已结束、内容却仍近乎全透明 —— 只可能是状态机在反复重置
                // （每次状态切换都把 _stateChangeTime 刷成「现在」，transitionAlpha 就永远起不来）。
                // 这一档的表现正是「岛还在，里面的文字和组件全都不显示」，留一行证据便于对时间点。
                if (alpha < 8 && startupProgress > 0.99f)
                {
                    long tA = Environment.TickCount64;
                    if (tA - _alphaDiagMs >= 2000)
                    {
                        _alphaDiagMs = tA;
                        Logger.Warn($"[渲染] 内容透明度只有 {alpha}/255（transitionAlpha={transitionAlpha:F2}），"
                            + "岛内元素会整体不可见");
                    }
                }
                float textOffsetY = 0f;

                // 仅恢复原版代码中软件刚启动时的位移，不影响状态切换
                if ((!media.IsActive || SuppressMediaInIsland) && startupProgress < 1f)
                {
                    textOffsetY = (1f - startupProgress) * 15f;
                }

                SKColor currentA = _currentTextColor.WithAlpha(alpha);
                SKColor subA = _currentSubTextColor.WithAlpha(alpha);

                _titlePaint.Color = currentA;
                _bodyPaint.Color = subA;
                _textPaint.Color = currentA;
                _timePaint.Color = currentA;
                _datePaint.Color = subA;
                _mediaIconPaint.Color = currentA;
                _barPaint.Color = currentA;
                _highQualitySampling.Color = SKColors.White.WithAlpha(alpha); // 同步作用于图片图标
                // Toast 消息通知
                if (toast != null)
                {
                    InvalidateDetailHitArea();
                    DrawToastLayer(canvas, toast, left, right, currentHeight);
                    EndContents(canvas, useLayer);   // 裁切 + 透明层
                    canvas.Restore();                // Translate
                    LiveWindowRenderer.Draw(canvas);
                    return;
                }

                // 插件详情页（右键展开）
                var detailPage = Plugins.PluginManager.Instance.Host.ActiveDetailPage;
                if (detailPage != null && !_detailBroken)
                {
                    DrawDetailPage(canvas, detailPage, left, currentHeight, currentWidth, alpha, textOffsetY, bars,
                        _pluginMouseX, _pluginMouseY - topY);
                    EndContents(canvas, useLayer);   // 1. 裁切 2. 透明层
                    canvas.Restore();                // 3. 恢复最外层的 Translate 画布平移
                    LiveWindowRenderer.Draw(canvas);
                    return;
                }

                // 媒体控制与待机状态
                if (media.IsActive)
                    UpdateMediaState(media);
                UpdateClockCache();

                // 自定义组合模式 / 原生布局
                if (!SuppressMediaInIsland && IsMediaPanelShowing(media, currentHeight))
                    DrawMediaControl(canvas, media, isHovered, bars,
                        new MediaBlockGeometry(left, left, right), currentHeight, textOffsetY, alpha);
                else if (CompositeModeEnabled)
                    DrawCompositeLayout(canvas, media, isHovered, bars, left, currentWidth, currentHeight, topY, textOffsetY, alpha);
                else
                    DrawNativeLayout(canvas, media, isHovered, bars, left, right,
                        currentHeight, textOffsetY, alpha);

                // ---- 插件组件行（非组合模式） ----
                if (!CompositeModeEnabled && pluginReserve > 0f)
                {
                    if (leftPluginBlock > 0f)
                    {
                        // 插件行只跟着边缘一起平移，不会自己额外挪动。
                        //    插件是随岛体长大从两侧滑入的。
                        var leftOrder = Plugins.PluginManager.Instance.Host.ContentOrder;
                        canvas.Save();
                        canvas.ClipRect(new SKRect(islandLeft, 0f, left, currentHeight));
                        float lx = islandLeft + PLUGIN_GAP;
                        for (int i = 0; i < nativeOrderIndex && i < leftOrder.Count; i++)
                        {
                            string item = leftOrder[i];
                            if (Plugins.BuiltinWidgets.IsBuiltin(item)) continue;
                            lx = DrawPluginWidgets(canvas, item, lx, currentHeight, alpha, textOffsetY, bars, _pluginMouseX, _pluginMouseY - topY);
                        }
                        canvas.Restore();
                    }
                    if (rightGroupW > 0f)
                    {
                        canvas.Save();
                        canvas.ClipRect(new SKRect(right, 0f, islandRight, currentHeight));
                        DrawPluginWidgets(canvas, null, islandRight - rightGroupW, currentHeight, alpha, textOffsetY, bars, _pluginMouseX, _pluginMouseY - topY);
                        canvas.Restore();
                    }
                }

                // 下方旧版残留的 _wakePath 绘制代码已彻底删除

                EndContents(canvas, useLayer);   // 1. 裁切 2. 透明层：本体内部渲染到此彻底完结，任何阴影/遮罩随之消失
                // 独立于本体之外，绘制隐形物理热区与极速渐变唤醒按钮。
                // 手动隐藏时连唤醒按钮一起收掉：此时「唤回」只认全局快捷键与托盘菜单，
                // 屏幕上不该再留一颗亮着的小圆（那就不叫隐藏了）。
                if (!IslandHidden
                    && ((PassthroughModeEnabled && PassthroughAlpha < 0.99f) || FullHideAlpha < 0.99f))
                    DrawWakeButton(canvas, currentHeight);

                canvas.Restore(); // 3. 恢复最外层的 Translate 画布平移

                // 实况窗画在主岛之上（画布已回基线）：气泡在岛体左侧，右键展开的面板也由它接管。
                LiveWindowRenderer.Draw(canvas);
            }
            finally
            {
                Monitor.Exit(_renderLock);
            }
        }

        /// <summary>
        /// 收尾岛体内部的「裁切层 + 透明层」。两个 Restore 是否成对取决于本帧有没有开透明层
        /// （见 Draw 里 useLayer 的说明）—— 稳态 alpha=255 时那层是恒等的，直接跳过。
        /// </summary>
        private static void EndContents(SKCanvas canvas, bool layered)
        {
            canvas.Restore();          // ClipPath 裁切
            if (layered) canvas.Restore();   // 透明层
        }

    }
}
