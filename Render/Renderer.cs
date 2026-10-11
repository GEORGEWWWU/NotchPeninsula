using SkiaSharp;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;

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

        public static int StandbyScene { get; set; } = 1;

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

        public static event Action? StandbyActiveChanged;

        public static bool StandbyToggleByDoubleClick { get; set; } = false;

        // 灵动岛背景材质：true = 亚克力（胶囊内铺模糊后的真实桌面）
        public static bool IslandAcrylic { get; set; } = false;

        // 亚克力背板：已模糊好的桌面截图，由 NotchWindow 每帧喂进来（只允许渲染线程读写）
        public static SKImage? AcrylicBackdrop { get; set; }

        // 背板上「本轮胶囊」对应的源矩形（图像像素坐标）——
        // 背板抓的是整个画布框，所以胶囊变大变小时只挪源矩形，不用重抓，避免闪烁
        public static SKRect AcrylicBackdropSrc { get; set; }

        public static int TargetMonitorIndex { get; set; } = 0; // 目标显示器索引

        public static int BgOpacityLevel { get; set; } = 4; // 透明度档位：0=0%, 1=25%, 2=50%, 3=75%, 4=100%

        //    「待机显示内容」，勾多个就是多模块并排。
        public static bool CompositeModeEnabled => true;

        public static bool CompShowDateTime { get; set; } = true;  // 显示时间日期

        public static bool CompShowHardware { get; set; } = true;  // 显示硬件占用

        public static bool CompShowMedia { get; set; } = true;     // 显示媒体控制器(含频谱)

        public static bool PassthroughModeEnabled = false; // 穿透模式总开关

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

        // 媒体控制器的长度完全放开，因此删掉了两个上限常量：

        // 动态计算最大边界，防止因刘海变大导致出界
        public static float WINDOW_WIDTH => Math.Max(1200f,
            Math.Max(MAX_ISLAND_WIDTH,
                Math.Max(ActiveDetailWidth, Math.Max(STANDBY_WIDTH, Math.Max(MEDIA_WIDTH, TOAST_WIDTH))))) + 80f;

        public static float MAX_WINDOW_HEIGHT => Math.Max(220f, Math.Max(ActiveDetailHeight, Math.Max(TOAST_HEIGHT, MEDIA_HEIGHT)) + 45f);

        public const int OUTER_R = 14;

        public const int INNER_R = 12;

        public static void Draw(SKCanvas canvas, MediaController media, bool isHovered, float currentWidth, float currentHeight, float startupProgress = 1f, float[]? bars = null, ToastData? toast = null, float styleProgress = 0f, float transitionAlpha = 1f)
        {
            if (!System.Threading.Monitor.TryEnter(_renderLock)) return;
            try
            {
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
                string? nativeBuiltinId = CompositeModeEnabled ? null : GetNativeBuiltinId(media.IsActive);
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

                float topY = 12f * styleProgress;

                canvas.Save();
                // 整个画布向下平移，让内部所有元素自动完美适应居中
                canvas.Translate(0, topY);

                _layerPaint.Color = SKColors.White.WithAlpha((byte)(255 * PassthroughAlpha * FullHideAlpha));
                canvas.SaveLayer(_layerPaint);

                _bgPath.Rewind();

                // 自动把四个圆角调到最大，动态计算插值半径
                // 限制灵动岛展开后的最大圆角为 20f，防止变成大圆球
                float islandRadius = Math.Min(currentHeight / 2f, 20f);
                float rBottom = NOTCH_BOTTOM_RADIUS * (1 - styleProgress) + islandRadius * styleProgress;
                float rTopY = OUTER_R * (1 - styleProgress) + islandRadius * styleProgress;
                float rTopX = -OUTER_R * (1 - styleProgress) + islandRadius * styleProgress;

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

                // 亚克力：胶囊内先铺「模糊后的真实桌面像素」，再叠自绘涂层当色调。
                // ⚠️ 顺序不能颠倒 —— 涂层必须画在模糊之上，否则会被背板整块盖掉（看起来就只剩"假玻璃"）。
                var backdrop = AcrylicBackdrop;
                if (IslandAcrylic && backdrop != null)
                {
                    canvas.Save();
                    canvas.ClipPath(_bgPath, SKClipOperation.Intersect, true);
                    canvas.DrawImage(backdrop, AcrylicBackdropSrc,
                        new SKRect(islandLeft, 0f, islandRight, currentHeight), _acrylicPaint);
                    canvas.Restore();
                }

                canvas.DrawPath(_bgPath, _bgPaint);

                canvas.Save();
                canvas.ClipPath(_bgPath, SKClipOperation.Intersect, true);

                byte alpha = (byte)(255 * startupProgress * transitionAlpha);
                float textOffsetY = 0f;

                // 仅恢复原版代码中软件刚启动时的位移，不影响状态切换
                if (!media.IsActive && startupProgress < 1f)
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
                    canvas.Restore();
                    canvas.Restore();
                    canvas.Restore();
                    return;
                }

                // 插件详情页（右键展开）
                var detailPage = Plugins.PluginManager.Instance.Host.ActiveDetailPage;
                if (detailPage != null && !_detailBroken)
                {
                    DrawDetailPage(canvas, detailPage, left, currentHeight, currentWidth, alpha, textOffsetY, bars,
                        _pluginMouseX, _pluginMouseY - topY);
                    canvas.Restore(); // 1. 恢复 ClipPath 裁切
                    canvas.Restore(); // 2. 闭合 SaveLayer 透明层
                    canvas.Restore(); // 3. 恢复最外层的 Translate 画布平移
                    return;
                }

                // 媒体控制与待机状态
                if (media.IsActive)
                    UpdateMediaState(media);
                UpdateClockCache();

                // 自定义组合模式 / 原生布局
                if (IsMediaPanelShowing(media, currentHeight))
                    DrawMediaControl(canvas, media, isHovered, bars,
                        new MediaBlockGeometry(left, left, right), currentHeight, textOffsetY, alpha);
                else if (CompositeModeEnabled)
                    DrawCompositeLayout(canvas, media, isHovered, bars, left, currentHeight, topY, textOffsetY, alpha);
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

                canvas.Restore(); // 1. 恢复 ClipPath 裁切
                canvas.Restore(); // 2. 闭合 SaveLayer 透明层，本体内部渲染彻底完结！任何阴影、遮罩全部随之消失。
                // 独立于本体之外，绘制隐形物理热区与极速渐变唤醒按钮。
                // 手动隐藏时连唤醒按钮一起收掉：此时「唤回」只认全局快捷键与托盘菜单，
                // 屏幕上不该再留一颗亮着的小圆（那就不叫隐藏了）。
                if (!IslandHidden
                    && ((PassthroughModeEnabled && PassthroughAlpha < 0.99f) || FullHideAlpha < 0.99f))
                    DrawWakeButton(canvas, currentHeight);

                canvas.Restore(); // 3. 恢复最外层的 Translate 画布平移
            }
            finally
            {
                Monitor.Exit(_renderLock);
            }
        }

    }
}
