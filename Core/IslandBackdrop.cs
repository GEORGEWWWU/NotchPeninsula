using System.Runtime.InteropServices;
using SkiaSharp;
using static NotchPeninsula.Logger;

namespace NotchPeninsula
{
    // 灵动岛的「亚克力」背板。
    //
    // 为什么不用系统原生亚克力（本机实测结论，别再走回头路）：
    //   · DWM 的 accent / SystemBackdrop 背板按【整块窗口矩形】铺，SetWindowRgn 对它完全无效
    //     （实测：加不加胶囊 region 截屏逐字节一致），而任意形状 + 抗锯齿只有路径裁切给得出。
    //
    // 为什么必须跑在后台线程（实测数据）：
    //   · 从屏幕读一次像素本身就要 **18~23ms**（BitBlt 1:1 19ms / StretchBlt 缩到 1/12 23ms /
    //     COLORONCOLOR 18ms —— 跟抓多大、怎么缩无关，是 DWM 回读的固定开销）。
    //     放在渲染线程上 = 每秒数次 20ms 级别的硬停顿，帧率必崩。
    //   · 所以：渲染线程只写「要抓哪块」，抓取/缩放/模糊全在 NPS-Backdrop 线程做，
    //     产出的小图通过 Interlocked 交换交给渲染线程，**由渲染线程负责释放**（唯一的消费者）。
    //
    // 内存口径：抓屏统一 1:1（约 1920x219，≈1.7MB DIB，后台线程持有，不进渲染帧）；
    //          每次只新产一张 SKImage，旧的那张在渲染线程手里、由它释放。
    internal static unsafe class IslandBackdrop
    {
        private const int RefreshMs = 250;      // 背板刷新节拍（屏幕读取很贵，别太频繁）
        // 抓屏统一 1:1（2026-10-24 起亚克力与玻璃同规格）：1/12 缩略会把背板糊成一团色，
        // 折射 / 亮度采样都失真；亚克力的雾面感交给渲染层的降采样，不再靠抓屏缩放实现。
        public const float Downscale = 1f;

        // 液态玻璃用的降采样：折射位移得看得出来，1/12 那种糊度会把玻璃拍成一团色块。
        // 随「玻璃质量」变（精细档 1:1），所以是属性不是常量。
        public static float GlassDownscale => LiquidGlass.CaptureDownscale;

        // 渲染线程每帧告知一次当前材质；切换会触发过期重抓
        private static volatile bool _glass;

        // 玻璃优先走 DXGI Desktop Duplication，拿不到画面时回退 GDI StretchBlt；
        // 用哪种由下面的 CaptureBackend 决定（个性化 → 抓屏方式）。

        // 抓屏后端（个性化 → 抓屏方式）：0 = 默认，1 = 强制 DXGI，2 = 只用 GDI。
        // 只影响玻璃：亚克力本来就只走 GDI。
        private static volatile int _captureBackend;

        // 后端刚被切过：由抓屏线程在下一轮循环开头自行释放 DXGI 资源（见 ThreadLoop）。
        private static volatile bool _backendReset;

        /// <summary>
        /// 抓屏后端：0 = 默认（DXGI 优先，连续拿不到画面就自动退回 GDI）；
        /// 1 = DXGI（每轮都问，不再自动判死）；2 = GDI（连 D3D 设备都不建）。
        /// 切换立即生效：DXGI 资源由抓屏线程在下一轮循环开头自行释放并重建。
        /// </summary>
        public static int CaptureBackend
        {
            get => _captureBackend;
            set
            {
                int v = Math.Clamp(value, 0, 2);
                if (v == _captureBackend) return;
                _captureBackend = v;
                _backendReset = true;
                Expire();          // 顺带清掉判死状态与背板，下一轮按新后端重抓
            }
        }

        /// <summary>
        /// 当前选的后端是否允许走 DXGI。渲染侧据此决定抓取矩形要不要只截屏幕顶部一条
        /// （DXGI 是 1:1，整块画布那么大的拷贝纯属浪费；选了 GDI 就没有这个前提）。
        /// </summary>
        public static bool DxgiAllowed => _captureBackend != 2;

        // 本帧背板是否来自 DXGI Desktop Duplication（诊断与日志用）。
        // DXGI 复制的是合成后的桌面，**岛体一定在画面里**，所以擦除逻辑照旧执行。
        private static volatile bool _dxgiUsed;

        // 是否成功产出过背板。DXGI 报「无新帧」时用它决定「复用上一帧」还是「必须 GDI 兜底」。
        private static volatile bool _everProduced;

        public static bool UsingDxgi => _dxgiUsed;

        // 本帧背板对应的【屏幕原点】：源矩形换算要用它，不能假定等于请求值（屏幕边界处可能裁掉一截）
        private static volatile int _originX, _originY;

        public static int LastOriginX => _originX;

        public static int LastOriginY => _originY;

        // 背板图 1 像素 = 屏幕多少像素（= 1 / 降采样倍数）。
        // 用实际产出图的尺寸反推，而不是用请求值 —— 首帧/边界裁剪时也不会算错源矩形。
        private static volatile float _scale = 1f / Downscale;

        public static float LastScale => _scale > 0f ? _scale : 1f / Downscale;

        // 液态玻璃 2 一律 1:1：它的观感就是「桌面被这块玻璃弯折」，
        // 抓屏先降采样再双线性放大，本身就是一层去不掉的模糊 ——
        // 那正是「桌面还是有高斯模糊」最直接的来源（与模糊算法、细节判定都无关）。
        public static float CurrentDownscale =>
            Renderer.IslandLiquidGlass2 ? 1f : (_glass ? GlassDownscale : Downscale);

        // 每次产出新背板 +1：液态玻璃靠它判断「要不要重算」（几何没动就不用再糊一遍）
        private static volatile int _version;

        public static int Version => _version;

        public static void SetMode(bool glass)
        {
            if (_glass == glass) return;
            _glass = glass;
            _expire = true;      // 换降采样倍数 → 缓冲尺寸变了，必须重抓
            _dirty = true;
            _work.Set();
        }

        // ⚠️ 这里的平滑**绝不能用 SKImageFilter.CreateBlur**：Skia 会把「每次过滤的产物」按源图的 uniqueID
        // 缓存起来，而背板每 250ms 就产出一张**新图** → 缓存只增不减。实测（纯管道 200s）：
        // 带滤镜 +13.9MB/分（私有内存 50.7→92.3MB），去掉滤镜后 0 增长。这就是「内存暴增」的真凶。
        // 改用「降到 1/2 再升回」的双线性等效模糊：无滤镜、无缓存、开销极小（GDI 那一步 1/12 已经做过盒式平均）。
        private static readonly SKPaint _resample = new()
        { IsAntialias = true, FilterQuality = SKFilterQuality.Low };

        // ---- 渲染线程写入 ----
        private static volatile int _tx, _ty, _tw, _th;
        private static volatile bool _dirty;
        private static volatile bool _expire;

        // ---- 后台线程独占 ----
        private static Thread? _thread;
        // 抓屏线程只允许存在一条：Capture/TryGrab 会改写 DXGI 复制会话、staging、D3D 上下文，
        // 两条线程同时跑就是重复 AcquireNextFrame/ReleaseFrame —— 原生访问违例，进程直接消失。
        private static readonly object _threadLock = new();
        private static volatile bool _run;
        private static readonly AutoResetEvent _work = new(false);
        private static IntPtr _memDc, _hBitmap, _oldBitmap, _pBits;
        private static int _capW, _capH;
        private static long _lastMs;
        private static bool _disabled;
        private static int _loggedX = int.MinValue, _loggedY = int.MinValue;

        // ---- 抓屏管线体检 ----
        // 「玻璃背景永远停在第一次抓到的那张图」这种问题，光看代码看不出来是"线程没跑"、
        // "DXGI 一直报无新帧"还是"渲染侧没取走"。这三个数 + version 的时间戳一眼就能分清：
        //   · 产出 0 次     → 线程没在抓（target / mask 不对，或有异常）
        //   · 产出有、version 不动 → 抓到了却没换图（DXGI 卡死）
        //   · 产出有、version 在涨 → 抓屏没问题，问题在玻璃合成/缓存
        private static int _statOk, _statFail, _statSkipOnly;
        private static long _diagMs;
        private static int _diagVersion;
        private static long _diagVersionMs;

        // 「DXGI 无新帧」的处理方式：**不判死**，按时间落一次 GDI 复核。
        //
        // 岛体现在在捕获里隐身（两档背景模糊都要真实桌面），所以「桌面静止就没有新帧」是正确行为，
        // 不是故障。以前那个「连续 12 次无新帧就永久切 GDI」的判据在这条路径上是纯伤害：
        // 只要桌面安静 1.5 秒，就会永久掉进每秒 8 次的全屏 GDI 回读（每次 20ms，约 16% 单核）。
        //
        // 真正要防的是另一种情况：分辨率切换 / 驱动重置 / 进出全屏之后，DXGI 会话有概率长期
        // 只报无新帧（画面明明变了），玻璃会停在最初那一张 —— 用户看到就是「换了壁纸玻璃不变」。
        // 所以超过 DxgiResyncMs 没拿到新帧，就落一次 GDI 抓取当复核，抓到什么就用什么；
        // DXGI 会话保留，下一秒有新帧自然跟上。一次回读只有岛体上方那一条，成本可忽略。
        private const int DxgiResyncMs = 150;

        //「上一次 DXGI 复核」的时刻。**不能只靠 _lastDxgiFrameMs**：它只在 DXGI 真的产出帧时才更新，
        // 一旦 DXGI 长期拿不到帧，它就是初值 0，`now - 0` 永远大于阈值 ——
        // 于是每一轮都判定「超时，落 GDI 复核」，后台线程变成 60ms 一次的 1920×256 全屏回读。
        private static long _lastDxgiFrameMs = Environment.TickCount64;

        // 抓屏节拍：玻璃下由用户在「抓屏刷新」滑轨上自己调（1s / 0.5s / 250ms / 160ms / 60ms）。
        // 只垫一个下限：GDI 回读一次要 20ms，节拍比它快多少，就白烧多少 CPU。
        private static int CaptureGap
        {
            get
            {
                if (!_glass) return RefreshMs;
                int gap = LiquidGlass.RefreshMs;
                // GDI 回读一次要 20ms，节拍低于 60ms 就是纯空转；DXGI 超时是等待，不耗 CPU。
                return gap < 60 ? 60 : gap;
            }
        }

        // GDI 两次回读之间的硬间隔：岛体展开/收起时 SetMask 会催抓，
        // 没有这道闸门的话，60fps 的几何动画会把抓屏催成 60 次/秒的全屏回读，
        // 直接跟渲染线程抢内存带宽 —— 观感就是"一动就卡"。
        private const int MinCaptureMs = 80;

        // DXGI 的检查节拍：AcquireNextFrame(timeout=0) 报"无新帧"是立即返回的空查询，
        // 一次几十微秒（100 次/秒 ≈ 0.5% 单核），跟 GDI 全屏回读不是一个量级。
        // 按 10ms 一轮问，
        // 桌面一变最多 10ms 就被发现 —— 这是背景跟手度的下限。
        private const int DxgiCheckMs = 10;

        /// <summary>DXGI 是否真的在出帧（决定检查节拍：出帧就 10ms 问一次，不出帧就按 GDI 的档位）。</summary>
        private static bool DxgiLive => DxgiBackdrop.Available && DxgiBackdrop.HasProduced && !_dxgiUnreliable;

        // DXGI 说「没新帧」，紧接着 GDI 复核却发现岛体外的桌面变了 —— 说明它漏帧。
        // 连续 3 次就判定这台机器的 Desktop Duplication 不可信，之后完全走 GDI 轮询。
        //
        // 为什么必须这么判：它偶尔能成功抓到一帧（HasProduced 就永久为真），
        // 但之后大部分时候只报 WAIT_TIMEOUT。上层若继续信它，就会一路等到超时才回落
        // GDI，背景刷新掉到秒级。
        private static int _dxgiMissedChanges;
        private static bool _dxgiUnreliable;
        private static bool _dxgiSaidNoChange;

        /// <summary>
        /// 从没出过帧时，多久再给它一次机会（避免永久放弃，换显示器 / 驱动更新后会恢复）。
        /// ⚠️ 别设小：探测这一下在「DXGI 不出帧」的机器上要阻塞 300ms（AcquireNextFrame 的超时），
        /// 5 秒一次就是每 5 秒卡一下。
        /// </summary>
        private const int DxgiProbeMs = 60000;

        /// <summary>启动后这么久还没出过任何一帧 → 判定本机 Desktop Duplication 不出帧，直接判死。</summary>
        private const int DxgiStartupGiveUpMs = 8000;

        private static long _dxgiStartMs;

        private static long _dxgiProbeMs;

        // ---- 内容守卫：抓到的这块到底"有没有东西" ----
        // 判据只看【玻璃真正采样的那块区域】（= 遮罩区，也就是岛体那块），不看整张背板：
        // 整图中央可能一直有内容（桌面），而胶囊那块恰好被岛体自己的黑色涂层占着 ——
        // 那正是「玻璃永远灰 45、点一下质量档才正常」的样子，只看整图会把它判成"正常"。
        private static volatile bool _contentAlive;

        private static int _deadStreak;

        private static long _lastDeadExpireMs;

        // 本次"死黑"里已经重建过几次缓冲：封顶 3 次。
        // 桌面本来就全黑（暗壁纸 / 全屏黑窗口）时不能每 1.5 秒就重建一次 DC+DIB —— 那是纯浪费。
        private static int _deadExpires;

        // -1 = 还没打过日志；0 = 上一次打的是"死黑"；1 = "恢复"
        private static int _guardLogged = -1;

        /// <summary>
        /// 最近一次抓到的玻璃源区域是否有内容（亮度/极差过关）。
        /// 启动自愈靠它判断"该不该收工"，别在背板还是黑的时候就把自愈机会用掉。
        /// </summary>
        public static bool IsContentAlive => _contentAlive;

        // 背板从全黑变亮的标记：渲染线程取走它再自己调 LiquidGlass.Invalidate()
        private static volatile bool _glassWake;

        /// <summary>渲染线程：背板刚从全黑变亮就要把玻璃缓存作废（后台线程不能碰 _glass 位图）。</summary>
        public static bool TakeGlassWake()
        {
            if (!_glassWake) return false;
            _glassWake = false;
            return true;
        }

        // 岛体在【屏幕物理像素】里的矩形（渲染线程每帧写 / 抓屏线程读）：用来把岛体自己那块抹掉。
        //
        // ⚠️ 必须记屏幕像素，**不能**记背板图像像素。图像像素要乘 LastScale、减 LastOrigin，
        //    而这两个值在"本次抓屏落地之前"还是上一张图的 —— 启动首帧正好是默认的 1/12，
        //    换算出来的遮罩只有 1/12 大小、位置也不对，于是岛体原封不动进了背板。
        //    玻璃的源矩形又正好是岛体那块 ⇒ 玻璃采到的是自己上一帧的输出（还没画玻璃时是纯黑涂层），
        //    而玻璃图层铺满同一块 ⇒ 45/46 的灰度成了不动点，永不自愈；只有点一次质量档强制
        //    Expire + 重建缓冲才跳出来。换算放到 Capture 里用本次的原点与缩放比做，那里没有"上一帧"。
        private static volatile float _maskSx0, _maskSy0, _maskSx1, _maskSy1;

        // ---- 额外遮罩：实况窗详情页面板 + 左侧实况窗本体 ----
        // 这些都是宿主自己画的像素，抓屏时必须一起擦掉，否则它们（尤其是现在也吃玻璃材质的
        // 气泡 / 面板）会采到自己的上一帧，形成残影 / 反馈。
        // 岛体那份仍走上面的 union（有动画历史、只有一个）；这些数量不定（面板 1 + 气泡最多 8），
        // 用固定上限数组 + 锁发布，不需要动画并集。
        public const int MaxExtraMaskRects = 10;   // 详情页面板 1 + 气泡 8 + 余量 1

        private static readonly object _extraMaskLock = new();
        private static readonly float[] _extraMaskRects = new float[MaxExtraMaskRects * 4];
        private static int _extraMaskCount;
        private static long _lastExtraMaskWakeMs;

        // 抓屏线程独占的拷贝，避免擦除期间一直持锁（会把渲染线程的发布卡住）。
        private static readonly float[] _extraMaskCapture = new float[MaxExtraMaskRects * 4];

        // ---- 擦除矩形 = 「当前岛体」∪「最近 400ms 内岛体待过的地方」----
        // 为什么需要历史：DXGI 给的是**已经合成好的桌面**，这一帧里的岛体是 100~250ms 前的位置和尺寸；
        // 而擦除用的是"此刻"的矩形。展开/收起（以及启动时的入场动画）过程中岛体尺寸一直在变，
        // 只擦当前矩形就会在边缘留下一圈上一帧的岛体像素 —— 玻璃里透出来就是**错位重影 / 双灵动岛**。
        // 把最近一小段时间的矩形并起来擦，大小动画的每一帧都能盖住；700ms 覆盖最慢的抓屏节拍有余。
        // 代价只有"多擦一圈"：这圈位置本来就在岛体背后，视觉上看不出来。
        private static volatile float _eraseUx0, _eraseUy0, _eraseUx1, _eraseUy1;
        private static long _eraseUnionMs;
        private const long EraseUnionMs = 400;

        // 并集每边最多比当前岛体多扩这么多：擦掉的面积就是要用平均色填掉的面积，
        // 扩太大玻璃下面就成了一整块纯色（"高斯模糊背景没了"就是这么来的）。
        private const float EraseUnionPadX = 20f;
        private const float EraseUnionPadY = 14f;

        // 渲染线程：告诉抓屏线程「岛体在屏幕上占哪一块」（屏幕物理像素）
        public static void SetMask(float x0, float y0, float x1, float y1)
        {
            // 形状一变，环带采样的位置也跟着变（展开/收起动画里每帧都在变）。
            // 记下时间戳，抓屏那边在动画期间不做"桌面变没变"的判断 —— 否则每帧都会误判成变了。
            if (MathF.Abs(x1 - _maskSx1) > 2f || MathF.Abs(y1 - _maskSy1) > 2f
                || MathF.Abs(x0 - _maskSx0) > 2f)
                _maskShapeMs = Environment.TickCount64;

            _maskSx0 = x0; _maskSy0 = y0; _maskSx1 = x1; _maskSy1 = y1;

            // 并集：超出 EraseUnionMs 没更新就从当前矩形重新起算，否则只扩不缩（但有外扩上限）
            long nowU = Environment.TickCount64;
            if (nowU - _eraseUnionMs > EraseUnionMs)
            {
                _eraseUx0 = x0; _eraseUy0 = y0; _eraseUx1 = x1; _eraseUy1 = y1;
            }
            else
            {
                float lo = x0 - EraseUnionPadX, ro = x1 + EraseUnionPadX;
                float to = y0 - EraseUnionPadY, bo = y1 + EraseUnionPadY;
                if (x0 < _eraseUx0) _eraseUx0 = MathF.Max(x0, lo);
                if (y0 < _eraseUy0) _eraseUy0 = MathF.Max(y0, to);
                if (x1 > _eraseUx1) _eraseUx1 = MathF.Min(x1, ro);
                if (y1 > _eraseUy1) _eraseUy1 = MathF.Min(y1, bo);
                // 当前矩形又缩回去了 → 并集也要跟着收到「当前 ± 上限」以内
                if (_eraseUx0 < lo) _eraseUx0 = lo;
                if (_eraseUy0 < to) _eraseUy0 = to;
                if (_eraseUx1 > ro) _eraseUx1 = ro;
                if (_eraseUy1 > bo) _eraseUy1 = bo;
            }
            _eraseUnionMs = nowU;

            // 遮挡区在明显变化（展开/收起动画）→ 尽快用新矩形重抓一次，免得岛体边缘漏进模糊里；但要节流
            bool moved = MathF.Abs(x1 - _maskLastX1) > 3f || MathF.Abs(x0 - _maskLastX0) > 3f;
            _maskLastX0 = x0; _maskLastX1 = x1;
            if (moved)
            {
                long now = Environment.TickCount64;
                if (now - _lastMaskWakeMs >= 150)
                {
                    _lastMaskWakeMs = now;
                    _dirty = true;
                    _work.Set();
                }
            }
        }

        /// <summary>
        /// 渲染线程：发布本帧「额外要擦掉的宿主矩形」（屏幕物理像素，flat 的 x0,y0,x1,y1，长度是 4 的倍数）。
        /// 实况窗详情页面板 / 左侧气泡都走这里。和岛体遮罩分开：数量不定，且不需要动画并集。
        /// </summary>
        public static void SetExtraMasks(ReadOnlySpan<float> rects)
        {
            int count = Math.Min(rects.Length / 4, MaxExtraMaskRects);
            bool changed = false;
            lock (_extraMaskLock)
            {
                if (count != _extraMaskCount) changed = true;
                if (!changed)
                {
                    for (int i = 0; i < count * 4; i++)
                    {
                        float a = _extraMaskRects[i], b = rects[i];
                        // 动画里每帧只差零点几像素：不重抓（否则会催成每帧一次抓屏）。
                        if (MathF.Abs(a - b) > 0.5f) { changed = true; break; }
                    }
                }
                for (int i = 0; i < count * 4; i++) _extraMaskRects[i] = rects[i];
                _extraMaskCount = count;
            }

            // 面板展开 / 气泡分裂 / 滑动时矩形在变 → 催一次重抓（与岛体同款 150ms 节流）。
            if (changed)
            {
                long now = Environment.TickCount64;
                if (now - _lastExtraMaskWakeMs >= 150)
                {
                    _lastExtraMaskWakeMs = now;
                    _dirty = true;
                    _work.Set();
                }
            }
        }

        private static long _lastMaskWakeMs;

        private static float _maskLastX0, _maskLastX1;

        public static void ClearMask()
        {
            _maskSx0 = _maskSy0 = _maskSx1 = _maskSy1 = 0f;
            _maskLastX0 = _maskLastX1 = 0f;   // 下次 SetMask 一定会被判成"移动了"，把抓屏线程叫醒
            lock (_extraMaskLock) _extraMaskCount = 0;
            _eraseUx0 = _eraseUy0 = _eraseUx1 = _eraseUy1 = 0f;   // 并集也一起作废，别拿上一轮的矩形继续擦
            _eraseUnionMs = 0;
        }


        // ---- 交给渲染线程的成品（交换后由渲染线程释放）----
        private static SKImage? _pending;

        public static void Expire()
        {
            _expire = true; _dirty = true; _work.Set();
            // 换材质 / 换桌面分辨率之后重新起算复核计时：首帧由 _everProduced 兜底走 GDI，
            // 之后照常等 DXGI 供货，别因为计时器归零而每轮都落 GDI
            _lastDxgiFrameMs = Environment.TickCount64;
            _hideDxgiMissStreak = 0;
            _dxgiProbeMs = 0;
            _dxgiUnreliable = false;
            _dxgiMissedChanges = 0;
            _dxgiStartMs = 0;
            _dxgiSaidNoChange = false;
            // 换材质 / 尺寸变化后旧背板已作废：允许下一轮退回 GDI 重新起一张，
            // 否则桌面恰好静止时 DXGI 报「无新帧」，会一直空着等不到图。
            _everProduced = false;
        }

        // 渲染线程：告诉后台线程「要抓这块屏幕矩形」。矩形没变就不会重新抓。
        public static void SetTarget(int x, int y, int w, int h)
        {
            if (x == _tx && y == _ty && w == _tw && h == _th) return;

            // 抓取矩形变了 = 采样窗口挪了位置，旧环带基准不能再比 —— 让下一轮直接隐身重抓一次。
            // ⚠️ 只在这里清：Expire 还会被"version 5 秒不动"的自愈调用，静止桌面下若在那里清，
            //    就会每 5 秒白白隐一次身，截图周期性地拍不到岛。
            _ringValid = false;
            _tx = x; _ty = y; _tw = w; _th = h;
            _dirty = true;
            _work.Set();
        }

        // 渲染线程：取走最新背板；返回 null 表示没有新图（继续用旧的）
        public static SKImage? TakePending()
        {
            EnsureThread();
            return Interlocked.Exchange(ref _pending, null);
        }

        /// <summary>
        /// 抓屏线程的状态快照：排查「启动不抓屏、要手动刷新」时看这一行就够。
        /// target 一直是 0×0 = 渲染侧没给矩形；dirty=False 且 target 正常 = 线程没被唤醒；
        /// alive=False = 抓到的玻璃源区域没内容（全黑/死平），内容守卫正在自己重建缓冲。
        /// </summary>
        public static string DebugState
            => $"target={_tw}x{_th}@{_tx},{_ty} dirty={_dirty} expire={_expire} "
             + $"disabled={_disabled} thread={(_thread?.IsAlive == true)} lastMs={_lastMs} "
             + $"alive={_contentAlive} deadStreak={_deadStreak} "
             + $"mask=({_maskSx0:F0},{_maskSy0:F0})-({_maskSx1:F0},{_maskSy1:F0})"
             + $" cap=阶段{CaptureFailStage}/连续失败{CaptureFailStreak}次";

        // ---- 抓屏失败诊断（2026-11-01）----
        // 起因：启动后出现过「目标与遮罩都就位、线程也在跑，却连续 3 秒一帧都产不出来，直到用户手动
        // 切一次材质才好」，而 Capture() 里每条失败路径都是静默 return false —— 日志里只剩一片空白，
        // 只能靠猜。这里把「卡在哪一步」记下来：连续失败 ≥5 次才打一行（限 1s 一次），恢复时收尾一行。
        // 下次再遇到「启动不抓屏」，看这两行的阶段号就能直接定位。
        private const int CapOk = 0;
        private const int CapFailTargetTooSmall = 1;   // 抓取矩形 < 16px
        private const int CapFailMaskMissing = 2;      // 岛体矩形还没就位（没遮罩会把岛体自己拍进背板，宁可先不抓）
        private const int CapFailBuffer = 3;           // DC / DIB 创建失败
        private const int CapFailScreenDc = 4;         // GetDC(NULL) 失败
        private const int CapFailStretchBlt = 5;       // StretchBlt 失败（驱动 / DWM 回读被拒）
        private const int CapFailSkia = 6;             // SKImage / SKSurface 建图失败
        private const int CapFailErase = 9;            // 遮罩擦不掉（这一帧会带着岛体，已丢弃）
        private const int CapFailDisabled = 8;         // 已被异常停用

        private static int _capFailStage = CapOk;
        private static int _capFailStreak;
        private static long _capFailSinceMs;
        private static long _capFailLoggedMs;
        private static string _capFailExtra = "";

        public static int CaptureFailStage => _capFailStreak == 0 ? CapOk : _capFailStage;

        public static int CaptureFailStreak => _capFailStreak;

        /// <summary>失败阶段的人话名（诊断包 / 日志用）。</summary>
        public static string CaptureFailStageName(int stage) => stage switch
        {
            CapOk => "无",
            CapFailTargetTooSmall => "抓取矩形过小",
            CapFailMaskMissing => "岛体遮罩未就位",
            CapFailBuffer => "抓屏缓冲创建失败",
            CapFailScreenDc => "取屏幕 DC 失败",
            CapFailStretchBlt => "StretchBlt 回读失败",
            CapFailSkia => "Skia 建图失败",
            CapFailErase => "遮罩擦不掉",
            CapFailDisabled => "已停用",
            _ => "未知"
        };

        private static bool Fail(int stage, string extra = "")
        {
            _capFailStage = stage;
            _capFailExtra = extra;

            // 同一处失败 5 秒最多一条：连续失败由 NoteCaptureFail 汇总，这里留的是"卡在哪一步"的第一现场。
            // 没有这一行，"玻璃背景是假的"在日志里查不到根因（之前的表现就是什么都没打，
            // 但抓屏一次都没成功）。
            long now = Environment.TickCount64;
            string key = stage + "|" + extra;
            if (key != _failWhy || now - _failLogMs >= 5000)
            {
                _failWhy = key;
                _failLogMs = now;
                Warn($"[背板] 抓屏中断：{CaptureFailStageName(stage)} target={_tw}x{_th}@{_tx},{_ty}"
                    + $" mask=({_maskSx0:F0},{_maskSy0:F0})-({_maskSx1:F0},{_maskSy1:F0})"
                    + (extra.Length > 0 ? " " + extra : ""));
            }
            return false;
        }

        private static void NoteCaptureFail()
        {
            long now = Environment.TickCount64;
            if (_capFailStreak == 0) _capFailSinceMs = now;
            _capFailStreak++;
            if (_capFailStreak >= 5 && now - _capFailLoggedMs >= 1000)
            {
                _capFailLoggedMs = now;
                Debug($"[背板] 抓屏连续失败 {_capFailStreak} 次（{now - _capFailSinceMs}ms）"
                    + $"卡在：{CaptureFailStageName(_capFailStage)}"
                    + (_capFailExtra.Length > 0 ? "｜" + _capFailExtra : "")
                    + $"｜{DebugState}");
            }
        }

        private static void NoteCaptureOk()
        {
            if (_capFailStreak >= 5)
                Info($"[背板] 抓屏恢复：之前连续失败 {_capFailStreak} 次（{CaptureFailStageName(_capFailStage)}）");
            _capFailStreak = 0;
            _capFailStage = CapOk;
            _capFailExtra = "";
        }

        public static void Stop()
        {
            _run = false;
            _work.Set();
            // ⚠️ 必须等线程真正退出：之前只 _run=false + Set 就走，线程多半还在 Capture() 的
            //    unsafe 块里（StretchBlt / EraseIsland，持着屏幕 DC 与 DIB 指针）。
            //    Environment.Exit 随后强杀它 ⇒ 未处理异常 ⇒ Fail Fast（每次退出都弹）。
            Thread? t;
            lock (_threadLock) t = _thread;
            if (t != null && !t.Join(2000))
            {
                // ⚠️ 这里**不能**把 _thread 置空：线程还活着，置空之后 EnsureThread 会再起一条，
                //    两条线程并发往同一个 DXGI 复制会话里 AcquireNextFrame / ReleaseFrame，
                //    重复释放是原生访问违例 0xc0000005（崩在 DxgiBackdrop.Dup_ReleaseFrame，
                //    托管层 catch 不住，进程直接消失）。
                //    留给 EnsureThread：它按 IsAlive 判断，旧线程真正退出之后才重建。
                Warn("[IslandBackdrop] 抓屏线程未在 2s 内退出（已置 _run=false，迟早退；不强行 Abort）");
            }
            else
            {
                // 只在字段仍指向这条已退出的线程时清空：期间可能已经有新线程接管
                lock (_threadLock)
                {
                    if (ReferenceEquals(_thread, t)) _thread = null;
                }
            }
            var p = Interlocked.Exchange(ref _pending, null);
            p?.Dispose();
        }

        private static void EnsureThread()
        {
            lock (_threadLock)
            {
                // ⚠️ 必须判 IsAlive：Stop() 里 Join 超时会保留 _thread（不置空），
                //    只判 != null 会把已经退出的线程当成还在跑，或者反过来在它还活着时重建。
                var live = _thread;
                if (live != null && live.IsAlive) return;
                _run = true;
                _thread = new Thread(ThreadLoop)
                {
                    IsBackground = true,
                    Name = "NPS-Backdrop",
                    Priority = ThreadPriority.BelowNormal
                };
                _thread.Start();
            }
            Info("[背板] 抓屏线程已启动");
        }

        private static void ThreadLoop()
        {
            while (_run)
            {
                // 等待上限 = 检查节拍（DXGI 10ms / GDI 80ms）：醒得勤只是去问一句"有新帧吗"，
                // 真正抓不抓仍由下面的 checkGap 把关，不会因为醒得勤就抓得勤。
                _work.WaitOne(Math.Min(CaptureGap, DxgiLive ? DxgiCheckMs : MinCaptureMs));   // GDI 模式下 MinCaptureMs 只是等待上限
                if (!_run) break;

                if (_expire)
                {
                    _expire = false;
                    FreeBuffer();
                    var old = Interlocked.Exchange(ref _pending, null);
                    old?.Dispose();
                    _lastMs = 0;
                }

                // 抓屏后端刚被切过：在这里（抓屏线程自己、且没有抓取在进行）释放 DXGI 资源。
                // 不能在 UI 线程释放 —— TryGrab 正在用这些对象，跨线程释放就是原生访问违例。
                if (_backendReset)
                {
                    _backendReset = false;
                    DxgiBackdrop.Reinitialize();
                }

                if (_disabled || _tw < 16 || _th < 16) continue;

                long now = Environment.TickCount64;
                int gap = CaptureGap;
                // 硬间隔：动画期间 SetMask 一直催抓，没有这一条就会每帧都回读一次屏幕。
                // DXGI 下这个间隔只是"多久问一次屏幕有没有变"，成本几十微秒，所以放到 12ms。
                // GDI 模式下直接用用户「刷新」档（最低 60ms）当硬间隔：
                // 80ms 那道闸门是给"动画催抓"准备的，现在动画期间有 _dirty + 150ms 节流兜着，
                // 再卡一道 80 只会让背景更慢。
                int minGap = DxgiLive ? DxgiCheckMs : gap;
                if (now - _lastMs < minGap) continue;

                // 岛体还在变形：环带采样点跟着动，比出来必然全是"变了"。
                // ⚠️ 这里不能把 _dirty 吃掉 —— 吃掉的话变形结束后还要再等一个完整周期
                //    （实测 settle 260 + gap 250 ≈ 半秒），观感就是"拖完岛体背景半天不更新"。
                if (now - _maskShapeMs < MaskSettleMs) { _dirty = true; continue; }

                // DXGI 的「无新帧」是立即返回的空查询，几乎零成本 —— 按 DxgiCheckMs 一直问即可，
                // 桌面一变最多 10ms 就被发现，完全不用等用户设的「刷新」档。
                // GDI 每次回读要 20ms，必须老老实实按 gap 限流。
                int checkGap = DxgiLive ? DxgiCheckMs : gap;
                if (!_dirty && now - _lastMs < checkGap) continue;
                _dirty = false;

                try
                {
                    if (Capture()) { _lastMs = now; _statOk++; NoteCaptureOk(); }
                    else
                    {
                        _lastMs = now;     // 失败也别每 250ms 重试同一帧
                        // 桌面没变 / 隐身节流推迟，都只是「这轮不抓」，不是故障：
                        // Capture() 只在真正出错时才写失败阶段，靠它区分，别把正常跳过也算进连续失败
                        // （算进去会每隔几秒打一对「连续失败 / 抓屏恢复」，把日志淹掉）。
                        if (_capFailStage == CapOk) _statSkipOnly++;
                        else { _statFail++; NoteCaptureFail(); }
                    }
                }
                catch (Exception ex)
                {
                    Error("[IslandBackdrop] 抓取背板失败，亚克力已停用", ex);
                    Renderer.EnsureVisibleInCapture();   // 别把窗口留在"隐身"状态，否则岛像消失了
                    _capFailStage = CapFailDisabled;
                    _disabled = true;
                    FreeBuffer();
                }

                DiagTick();
            }
        }

        // 每 10s 一行体检（只在亚克力/玻璃下打）：见 _statOk 那段的说明。
        private static void DiagTick()
        {
            if (!_glass && !Renderer.IslandAcrylic) return;

            long now = Environment.TickCount64;
            int v = _version;
            if (v != _diagVersion)
            {
                _diagVersion = v;
                _diagVersionMs = now;
            }
            if (_diagMs == 0) { _diagMs = now; return; }
            if (now - _diagMs < 10000) return;

            // DXGI 自己的计数：帧 / 无新帧 / 空白帧。三者一起看就能分清
            // 「桌面静止（Timeout 大、Frame 为 0）」和「后端根本没在工作（全是 Blank）」。
            var (dxF, dxT, dxB) = DxgiBackdrop.TakeStats();
            string dxgi = _dxgiUsed || dxF > 0 || dxB > 0
                ? $" · DXGI: 帧={dxF} 无新帧={dxT} 空白={dxB}"
                : "";

            long stuckMs = now - _diagVersionMs;
            string head = $"[背板] 近 10s 抓屏 {_statOk} 次 / 跳过 {_statSkipOnly} 次 / 失败 {_statFail} 次 · version={v}（{stuckMs}ms 未变）"
                + $" · {(_dxgiUsed ? "DXGI" : "GDI")} · target={_tw}x{_th}@{_tx},{_ty}"
                + $" · mask=({_maskSx0:F0},{_maskSy0:F0})-({_maskSx1:F0},{_maskSy1:F0})"
                + $" · scale={_scale:F2} alive={_contentAlive}"
                + $" · 隐含身={_statHiddenGrab} 次 / 环带未变={_statRingSkip} 次 / 节流推迟={_statHideThrottled} 次"
                + $" / 隐身后无帧={_statHideMiss} 次"
                + dxgi;
            // 静止桌面本来就不该更新：只要发生过"环带未变"的主动跳过，就不是故障
            if ((_statOk == 0 && _statRingSkip == 0) || (stuckMs > 6000 && _statRingSkip == 0))
                Warn(head + "  ← 抓屏没在更新（玻璃背景会冻住）");
            else
                Info(head);

            _statOk = 0; _statFail = 0; _statSkipOnly = 0; _statHiddenGrab = 0; _statRingSkip = 0;
            _statHideThrottled = 0; _statHideMiss = 0; _diagMs = now;
        }

        // 抓屏失败原因（节流 5s 一条）：没有这一行，"玻璃背景是假的"这件事在日志里查不到根因 ——
        // 之前的表现就是「什么都没打，但抓屏一次都没成功」。
        private static long _failLogMs;
        private static string _failWhy = "";


        // ── 「岛体周围那一圈」的亮度采样：判断桌面到底变没变 ──
        //
        // 为什么要这一圈而不是整张图：隐身只在「桌面确实变了」的那一刻才开（见 Capture 的 ①②），
        // 判断"变没变"必须用**不隐身**的帧，而不隐身的帧里岛体自己挡着中心 ——
        // 只有外侧这一圈是真实桌面。所以只在矩形四条边外 12px 处采样。
        //
        private const int RingPts = 24;              // 每条边取几个点（越密越容易发现小范围变化）

        // 单点算"变了"的亮度差：3% 已经很保守，抖动/压缩噪声在 1% 以内
        private const float RingPointDelta = 0.03f;

        // 「变了」的点占到多少比例就算整块桌面变了（15% ≈ 96 个点里 15 个）
        private const float RingChangedShare = 0.15f;

        // 「变化全在岛体底下」的最后兜底：超过这么久没换过背板就无条件隐身抓一次。
        // 正常情况下 SuspiciousMax 那一关就拦住了（约 50ms）；这一条只是防止连击计数被反复清零时漏掉。
        private const int BackdropRefreshBackstopMs = 400;

        private static long _lastGoodMs;

        private static readonly float[] _ringNow = new float[RingPts * 4];

        private static readonly float[] _ringPrev = new float[RingPts * 4];

        private static bool _ringValid;

        // 岛体形状最后变动的时刻：变形中不做环带判断（采样点还在动，比出来的全是"变了"）
        private static long _maskShapeMs;

        // 形状稳定多久之后才允许重新判桌面。动画期间采样点在动，比出来全是"变了"；
        // 形状一停采样点就固定了，90ms 足够（原来 150ms 会让拖完岛体后背景明显滞后）。
        private const int MaskSettleMs = 90;

        // 体检用：这一轮里"桌面没变所以没隐身"和"真隐身抓了一次"各多少
        private static int _statRingSkip;

        private static int _statHiddenGrab;

        // 隐身后仍拿不到干净帧、只能放弃本轮 的次数（见 Capture 的 ②）
        private static int _statHideMiss;

        // 连续几次「隐身后 DXGI 给不出新帧」；到 3 次就跳过 DXGI 直奔 GDI
        private static int _hideDxgiMissStreak;

        // 临时隐身要持续多久：DWM 至少合成一次才把"岛体已排除"写进捕获缓冲。
        // 60Hz 下一帧 16.7ms，28ms 覆盖 1~2 次合成；屏幕上看不出任何变化（WDA 只影响捕获）。
        private const int TransientHideMs = 28;

        // 两次「隐身抓」之间的最小间隔：视频播放、滚动这类连续变化会让每个检查周期都判定"变了"，
        // 没有这道闸门就会变成长期隐身 —— 那时候截图是真的拍不到岛了。
        // 90ms 一次、每次 28ms，隐身时间占比约 31%：静止桌面 0%，连续变化时截图仍有约 2/3 命中率。
        private const int MinHideGrabMs = 90;

        private static long _lastHideGrabMs;

        // 体检用：因为节流而推迟的隐身抓次数
        private static int _statHideThrottled;

        // 「DXGI 说有新帧、但岛体外那一圈没变」连击几次就无条件隐身抓：
        // 变化很可能全落在岛体正下方（窗口刚好被它盖住后消失/出现），这种情况下环带永远看不出，
        // 只能靠这个兜底。3 × 10ms = 30ms。
        private const int SuspiciousMax = 3;

        private static int _suspicious;

        private static unsafe void SampleRing(int sw, int sh, int x0, int y0, int x1, int y1, float[] buf)
        {
            // 12px：岛体自己的阴影 / 抗锯齿不超过这个宽度，再往里会采到岛体本身，
            // 那就会与"隐身帧"的同一点永远不一致 —— 表现为每轮都判成"桌面变了"，一直在隐身。
            const int pad = 12;
            int lx = x0 - pad, rx = x1 + pad, ty = y0 - pad, by = y1 + pad;
            byte* bits = (byte*)_pBits;
            int k = 0;
            for (int i = 0; i < RingPts; i++)
            {
                float t = (i + 0.5f) / RingPts;
                int px = (int)(x0 + (x1 - x0) * t);
                int py = (int)(y0 + (y1 - y0) * t);
                buf[k++] = LumAt(bits, sw, sh, px, ty);
                buf[k++] = LumAt(bits, sw, sh, px, by);
                buf[k++] = LumAt(bits, sw, sh, lx, py);
                buf[k++] = LumAt(bits, sw, sh, rx, py);
            }
        }

        private static unsafe float LumAt(byte* bits, int sw, int sh, int x, int y)
        {
            if (x < 0 || y < 0 || x >= sw || y >= sh) return -1f;   // 越界的点跳过比较
            int o = (y * sw + x) * 4;
            return (0.072f * bits[o] + 0.715f * bits[o + 1] + 0.213f * bits[o + 2]) * (1f / 255f);
        }

        /// <summary>
        /// 环带变没变：用「明显变化的采样点占比」而不是平均差 ——
        /// 少数几个点被岛体边缘/阴影污染时不会把结论带偏。
        /// 壁纸、窗口切换会让 80% 以上的点同时变；静止桌面的抖动远低于单点阈值。
        /// </summary>
        private static bool RingChanged()
        {
            int changed = 0, n = 0;
            for (int i = 0; i < _ringNow.Length; i++)
            {
                float a = _ringPrev[i], b = _ringNow[i];
                if (a < 0f || b < 0f) continue;
                n++;
                if (MathF.Abs(a - b) > RingPointDelta) changed++;
            }
            if (n == 0) return true;
            return changed >= n * RingChangedShare;
        }

        private static bool Capture()
        {
            int w = _tw, h = _th, x = _tx, y = _ty;
            if (w < 16 || h < 16) return Fail(CapFailTargetTooSmall, $"target={w}x{h}");

            // 还不知道岛体占哪块 → 先别抓（不然会把岛体自己拍进去，画面会往涂层色糊成一坨）
            float mSx0 = _maskSx0, mSy0 = _maskSy0, mSx1 = _maskSx1, mSy1 = _maskSy1;
            if (mSx1 - mSx0 < 1f || mSy1 - mSy0 < 1f)
                return Fail(CapFailMaskMissing, $"mask=({mSx0:F0},{mSy0:F0})-({mSx1:F0},{mSy1:F0})");

            float ds = CurrentDownscale;
            int sw = Math.Max(1, (int)(w / ds));
            int sh = Math.Max(1, (int)(h / ds));
            EnsureBuffer(sw, sh);
            // ⚠️ DIB 创建失败时 _pBits 会是空指针，后面 SampleLuminance / EraseIsland 直接裸写就会 AV：
            //    所以这里既当"这次抓不了"的出口，也顺带把半成品缓冲判死（下次 Expire 会重建）。
            if (_memDc == IntPtr.Zero || _hBitmap == IntPtr.Zero || _pBits == IntPtr.Zero)
                return Fail(CapFailBuffer, $"memDc={_memDc} hBitmap={_hBitmap} pBits={_pBits} {sw}x{sh}");

            // 遮罩是屏幕物理像素 → 换算到本次背板图像坐标。用的是本次的原点与缩放比（下面那三个值），
            // 不是"上一张图"的 LastScale/LastOrigin，所以首帧也不会错位。
            float inv = sw / (float)w;
            int mx0 = (int)MathF.Floor((mSx0 - x) * inv);
            int my0 = (int)MathF.Floor((mSy0 - y) * inv);
            int mx1 = (int)MathF.Ceiling((mSx1 - x) * inv);
            int my1 = (int)MathF.Ceiling((mSy1 - y) * inv);

            // 擦除用「当前 ∪ 最近 700ms」的矩形（见 SetMask 里的说明）：
            // 抓到的这一帧里的岛体可能是几百毫秒前的尺寸/位置，只擦当前矩形会在边缘留下重影。
            float eSx0 = _eraseUx0, eSy0 = _eraseUy0, eSx1 = _eraseUx1, eSy1 = _eraseUy1;
            if (eSx1 - eSx0 < 1f || eSy1 - eSy0 < 1f) { eSx0 = mSx0; eSy0 = mSy0; eSx1 = mSx1; eSy1 = mSy1; }
            int ex0 = (int)MathF.Floor((eSx0 - x) * inv);
            int ey0 = (int)MathF.Floor((eSy0 - y) * inv);
            int ex1 = (int)MathF.Ceiling((eSx1 - x) * inv);
            int ey1 = (int)MathF.Ceiling((eSy1 - y) * inv);

            // 额外遮罩（详情页面板 / 实况窗气泡）：本帧矩形拷到抓屏线程自己的缓冲里再慢慢擦。
            int extraCount;
            lock (_extraMaskLock)
            {
                extraCount = _extraMaskCount;
                for (int i = 0; i < extraCount * 4; i++) _extraMaskCapture[i] = _extraMaskRects[i];
            }

            // 抓屏后端：玻璃优先 DXGI（GPU 采集），其余走 GDI StretchBlt。
            // gdiOnly：跳过 DXGI 直接读屏幕。隐身后的第二次抓取要用它兜底 ——
            // GDI 读的是"当前这一刻"的屏幕，岛体已隐身就一定拿得到干净桌面；
            // 而 DXGI 只在 DWM 重新合成时才有新帧，切换亲和性后的那一次并不保证存在。
            bool GrabOnce(bool gdiOnly = false)
            {
                // 抓屏后端：0 默认（下面那套自动判死）/ 1 强制 DXGI / 2 只用 GDI。
                bool forceGdi = _captureBackend == 2;
                bool forceDxgi = _captureBackend == 1;

                // 启动后 8 秒一帧都没出过 → 判死。正常桌面在这段时间里几乎不可能完全没有 present
                // （时钟、光标、任何窗口动画都会产生帧）；就算真是静止壁纸，判死的代价也只是
                // 走 GDI 轮询 —— 而继续等它，代价是背景三秒才更新一次。
                // ⚠️ 这条必须放在 tryDxgi 判断之前：判死之后 tryDxgi 恒为 false，放里面就再也走不到。
                // 用户手动选了「DXGI」就不替他判：他就是要这条路，慢也是他的选择。
                if (!forceDxgi && _glass && !_dxgiUnreliable && !DxgiBackdrop.HasProduced
                    && _dxgiStartMs > 0
                    && Environment.TickCount64 - _dxgiStartMs >= DxgiStartupGiveUpMs)
                {
                    _dxgiUnreliable = true;
                    Warn($"[DXGI] 启动 {DxgiStartupGiveUpMs / 1000} 秒内没有产出任何一帧，"
                        + "判定本机 Desktop Duplication 不出帧，抓屏改为 GDI 轮询");
                }

                // 是否值得调用 DXGI：
                //   · 强制 DXGI → 每轮都问，不看历史（拿不到帧就落 GDI 兜底，不会给一块空玻璃）
                //   · 默认档：它这一辈子出过帧 → 每次都问（静止时零开销，有变化立刻给）
                //             从没出过帧（本机 Intel HD 530 就是这样）→ 每 DxgiProbeMs 试一次，
                //             其余时候直接 GDI —— 它给不出「桌面变没变」的任何信息，问它只会白等。
                bool tryDxgi = _glass && !gdiOnly && !forceGdi
                    && (forceDxgi
                        || (!_dxgiUnreliable
                            && (DxgiBackdrop.HasProduced
                                || Environment.TickCount64 - _dxgiProbeMs >= DxgiProbeMs)));
                if (tryDxgi)
                {
                    if (_dxgiStartMs == 0) _dxgiStartMs = Environment.TickCount64;
                    _dxgiProbeMs = Environment.TickCount64;
                    switch (DxgiBackdrop.TryGrab(x, y, w, h, _pBits, sw * 4, sw, sh))
                    {
                        case DxgiBackdrop.GrabResult.Ok:
                            _dxgiUsed = true;
                            _lastDxgiFrameMs = Environment.TickCount64;
                            // 它这次自己给出了帧 → 上一次的「说没变」不算漏帧，清掉凭据，
                            // 否则正常机器上「静止 → 变化」每个来回都会被误记一次
                            _dxgiSaidNoChange = false;
                            return true;
                        case DxgiBackdrop.GrabResult.NoNewFrame:
                            // 桌面静止 → 直接复用上一帧。这是 DXGI 相对 GDI 的全部价值：
                            // 静止时零开销，不用去 BitBlt 一遍整个屏幕。
                            //
                            // ⚠️ 判据必须是「有没有成功产出过」，不能是「_pending 还在不在」：
                            //    _pending 会被渲染线程每帧取走，拿它当判据等于几乎每轮都判定成首次，
                            //    于是每轮都白跑一次 20ms 的 GDI BitBlt —— 这就是「卡卡的」的直接原因。
                            //
                            // ⚠️ 但「无新帧」不能无限信：会话切过分辨率 / 驱动重置 / 进出全屏之后，
                            //    有概率长期只报无新帧（画面明明变了）。超时就落一次 GDI 复核，
                            //    抓到什么用什么，DXGI 会话保留（见 DxgiResyncMs 的注释）。
                            // DXGI 从没产出过帧 → 它说「没新帧」不能当作「桌面没变」，直接落 GDI
                            if (!DxgiBackdrop.HasProduced) break;
                            if (!_everProduced) break;                                  // 首帧：必须落 GDI
                            long nowDx = Environment.TickCount64;
                            if (nowDx - _lastDxgiFrameMs < DxgiResyncMs)
                            {
                                _dxgiSaidNoChange = true;   // 留个凭据：等 GDI 复核时对账
                                return false;
                            }
                            // 复核之后重新计时：不加这一句，DXGI 一次拿不到帧就会永远「超时」，
                            // 每轮都掉进 GDI 全屏回读（这台机器实测就是这样，玻璃一直在走 GDI 擦除）。
                            _lastDxgiFrameMs = nowDx;
                            break;
                    }
                }
                IntPtr sdc = Win32.GetDC(IntPtr.Zero);
                if (sdc == IntPtr.Zero) return Fail(CapFailScreenDc, "GetDC(NULL)");
                try
                {
                    // 1:1 直拷（ds=1 时 StretchBlt 就是原样搬运，不过缩放器）
                    Win32.SetStretchBltMode(_memDc, Win32.HALFTONE);
                    if (!Win32.StretchBlt(_memDc, 0, 0, sw, sh, sdc, x, y, w, h, Win32.SRCCOPY))
                        return Fail(CapFailStretchBlt, $"src=({x},{y},{w},{h}) dst={sw}x{sh}");
                }
                finally
                {
                    Win32.ReleaseDC(IntPtr.Zero, sdc);
                }
                // 刷批：下面的擦除、亮度、SKImage 全是 CPU 直接读 DIB 的位（见 Win32.GdiFlush 的注释）
                Win32.GdiFlush();
                _dxgiUsed = false;
                return true;
            }

            // ── ① 先按「不隐身」抓一帧 ──
            // 这一帧里岛体还在，绝不能当背板发布（那就是双灵动岛 / 错位重影）。
            // 它唯一的用途是回答一个问题：岛体周围那圈桌面，变了没有？
            if (!GrabOnce()) return false;

            bool hidNow = false;
            if (_glass)
            {
                long nowMs = Environment.TickCount64;
                SampleRing(sw, sh, mx0, my0, mx1, my1, _ringNow);
                if (_ringValid && RingChanged())
                {
                    _suspicious = 0;   // 环带明确变了，不用再猜

                    // 对账：上一次它说"没新帧"，结果桌面其实变了 → 它漏帧
                    if (_dxgiSaidNoChange && !_dxgiUnreliable)
                    {
                        _dxgiSaidNoChange = false;
                        if (++_dxgiMissedChanges >= 3)
                        {
                            _dxgiUnreliable = true;
                            Warn("[DXGI] 连续 3 次漏掉桌面变化（说无新帧但 GDI 看到变了），"
                                + "判定本机 Desktop Duplication 不可信，抓屏改为 GDI 轮询");
                        }
                    }
                }
                else if (_ringValid)
                {
                    // DXGI 已经说"画面动过"，但岛体外面那一圈看不出变化 —— 变化很可能全落在
                    // 岛体正下方（窗口刚好被它盖住后消失 / 出现）。环带永远看不见这种情况，
                    // 所以连击够次数就无条件抓一次（3 次 × 10ms = 30ms）。
                    if (_suspicious < SuspiciousMax) _suspicious++;
                    if (_suspicious < SuspiciousMax
                        && nowMs - _lastGoodMs < BackdropRefreshBackstopMs)
                    {
                        // 桌面确实没变 → 背景沿用上一张。**全程没有隐身**，截图照常拍得到岛。
                        _statRingSkip++;
                        return false;
                    }
                }

                // 连续变化（视频、滚动）时节流：每 MinHideGrabMs 才隐一次身，否则截图就真拍不到岛了。
                // 被挡住的这轮不动环带基准 —— 下一轮还会判"变了"，闸门一过立刻抓。
                if (nowMs - _lastHideGrabMs < MinHideGrabMs)
                {
                    _statHideThrottled++;
                    return false;
                }
                _lastHideGrabMs = nowMs;

                // ── ② 桌面变了才临时隐身，等 DWM 重新合成一帧再读 ──
                // 隐身只持续这几十毫秒，屏幕上没有任何变化（WDA 只影响捕获，不影响显示）。
                // 亲和性没贴上就别跳过擦除 —— 那帧里岛体还在，直接发布就是双灵动岛
                bool hideApplied = Renderer.SetCaptureHide(true);
                try
                {
                    if (hideApplied)
                    {
                        // DWM 要重新合成一帧才会把「岛体已排除」写进捕获缓冲。
                        // 只等一次就要求 DXGI 给出新帧，在 60Hz 上余量太小：
                        // 拿不到时 _pBits 里还是第①步那帧"含岛体"的画面 ——
                        // 那条路走下去就是擦除填色，玻璃直接退回一块平均色（"真实背景没了"）。
                        // 连续几次隐身后 DXGI 都给不出新帧（DWM 不为亲和性变化重新合成）→
                        // 之后直奔 GDI，省掉每轮几十毫秒的空等。
                        bool got = false;
                        if (_hideDxgiMissStreak < 3)
                        {
                            for (int attempt = 0; attempt < 2 && !got; attempt++)
                            {
                                Thread.Sleep(attempt == 0 ? TransientHideMs : 14);
                                got = GrabOnce();
                            }
                            if (got) _hideDxgiMissStreak = 0;
                            else _hideDxgiMissStreak++;
                        }

                        // 还不给：用 GDI 直接读一次屏幕。它读的是当前状态（岛体已隐身），
                        // 一定能拿到真实桌面；20ms 的代价远小于"退回平均色"。
                        if (!got) got = GrabOnce(gdiOnly: true);

                        if (!got)
                        {
                            // 这一轮实在拿不到干净帧（DWM 没重合成 / 会话刚重置）：
                            // **直接放弃**，沿用上一张真实桌面背板。
                            // 绝不能退回「不隐身 + 擦除」—— 那会让玻璃变成平滑色块。
                            _statHideMiss++;
                            return false;
                        }

                        SampleRing(sw, sh, mx0, my0, mx1, my1, _ringNow);
                        Array.Copy(_ringNow, _ringPrev, _ringNow.Length);
                        _ringValid = true;
                        _suspicious = 0;
                        _statHiddenGrab++;
                        hidNow = true;
                    }
                    // hideApplied == false：亲和性贴不上（老系统 / 被策略禁止），
                    // 没法拿到不含岛体的画面，只能走下面的擦除降级 —— 至少还能更新、不会出现双岛。
                }
                finally
                {
                    Renderer.SetCaptureHide(false);
                }
            }

            // hidNow = 这一帧是「岛体已经隐身」时读到的 → 遮罩那块本来就是真实桌面，
            // 再擦一次等于把真实内容抹成平均色（玻璃下面又成平滑色块），所以擦除一律不跑。
            // 亚克力 / 隐身没生效（亲和性设置失败、老系统）时走下面的擦除兜底：
            // 把岛体自己占的那块用四方向最近邻延展抹掉，总比把岛体拍进玻璃里强。
            //
            // 擦除前的亮度也量一次：它和"擦除后"一前一后写进诊断行，
            // 一眼就能分清「背板本来就是黑的」还是「岛体自己被抓进来了、擦除没盖住」。
            int lumBefore = SampleLuminance((byte*)_pBits, sw * 4, sw, sh, mx0, my0, mx1, my1, out _);
            int lumAfter;
            int rangeAfter;
            if (hidNow)
            {
                lumAfter = lumBefore;
                rangeAfter = 0;
            }
            else
            {
                // 擦不掉就别发这张图：发出去的那一刻玻璃里就是"岛体自己"，也就是错位重影 / 双灵动岛。
                // 宁可这一轮不产出（沿用上一张），也不能把带岛体的背板交出去。
                if (!EraseIsland(sw, sh, ex0, ey0, ex1, ey1))
                    return Fail(CapFailErase, $"erase=({ex0},{ey0})-({ex1},{ey1})");
                // 详情页面板 / 实况窗气泡也擦：它们同样是宿主自己画的像素，留在背板里会被玻璃
                // 采样成残影。（完全落在抓取矩形外时跳过 —— 已不可见，没必要因此丢一张好背板。）
                for (int i = 0; i < extraCount; i++)
                {
                    int ex = i * 4;
                    int rx0 = (int)MathF.Floor((_extraMaskCapture[ex] - x) * inv);
                    int ry0 = (int)MathF.Floor((_extraMaskCapture[ex + 1] - y) * inv);
                    int rx1 = (int)MathF.Ceiling((_extraMaskCapture[ex + 2] - x) * inv);
                    int ry1 = (int)MathF.Ceiling((_extraMaskCapture[ex + 3] - y) * inv);
                    if (rx1 <= 0 || ry1 <= 0 || rx0 >= sw || ry0 >= sh) continue;
                    if (!EraseIsland(sw, sh, rx0, ry0, rx1, ry1))
                        return Fail(CapFailErase, $"erase extra#{i}=({rx0},{ry0})-({rx1},{ry1})");
                }
                lumAfter = SampleLuminance((byte*)_pBits, sw * 4, sw, sh, mx0, my0, mx1, my1, out rangeAfter);
            }

            if (_glass) TrackContent(lumAfter, rangeAfter);

            // ⚠️ 必须把 DIB 的 alpha 字节全部置 255。
            // StretchBlt/BitBlt 只写 BGR、**不写 alpha**（留下 0），而 SKImage.FromPixels 会把 a=0 当「全透明」——
            // 贴上去等于什么都没贴，亚克力整体消失（实测：不置 alpha → 画进画布后整块变透明）。
            // 按 uint 整字置位：逐字节 Marshal.WriteByte 在 1080p 下是 140 万次托管调用（7~14ms/次）。
            uint* pxAlpha = (uint*)_pBits;
            for (int i = 0, n = sw * sh; i < n; i++) pxAlpha[i] |= 0xFF000000u;

            var info = new SKImageInfo(sw, sh, SKColorType.Bgra8888, SKAlphaType.Opaque);
            using var src = SKImage.FromPixels(info, _pBits, sw * 4);
            if (src == null) return Fail(CapFailSkia, "SKImage.FromPixels");

            SKImage? img;
            if (_glass)
            {
                // 液态玻璃：这里只做 1:1 搬运，保留抓屏原始清晰度
                // （StretchBlt 的降采样本身已是一次盒式平均）。真正的高斯模糊交给
                // LiquidGlass 只在「胶囊 + 折射余量」那一小块上做 —— 全屏糊一遍太贵。
                using var raw = SKSurface.Create(new SKImageInfo(sw, sh, SKColorType.Bgra8888, SKAlphaType.Premul));
                if (raw == null) return Fail(CapFailSkia, "SKSurface(glass)");
                raw.Canvas.DrawImage(src, 0f, 0f, _resample);
                raw.Canvas.Flush();
                img = raw.Snapshot();
            }
            else
            {
                // 无滤镜等效模糊：先降到 1/2（双线性 = 盒式平均），再升回原尺寸
                int hw = Math.Max(1, sw / 2), hh = Math.Max(1, sh / 2);
                using var half = SKSurface.Create(new SKImageInfo(hw, hh, SKColorType.Bgra8888, SKAlphaType.Premul));
                if (half == null) return Fail(CapFailSkia, "SKSurface(half)");
                half.Canvas.DrawImage(src, new SKRect(0, 0, hw, hh), _resample);
                half.Canvas.Flush();
                using var halfImg = half.Snapshot();
                if (halfImg == null) return Fail(CapFailSkia, "snapshot(half)");

                using var smooth = SKSurface.Create(new SKImageInfo(sw, sh, SKColorType.Bgra8888, SKAlphaType.Premul));
                if (smooth == null) return Fail(CapFailSkia, "SKSurface(smooth)");
                smooth.Canvas.DrawImage(halfImg, new SKRect(0, 0, sw, sh), _resample);
                smooth.Canvas.Flush();
                img = smooth.Snapshot();
            }
            if (img == null) return Fail(CapFailSkia, "snapshot(final)");

            // 旧的那张还没被渲染线程取走 → 直接丢掉它（我们自己产的，安全）
            _originX = x;
            _originY = y;
            _scale = sw / (float)w;

            _everProduced = true;
            _lastGoodMs = Environment.TickCount64;
            var stale = Interlocked.Exchange(ref _pending, img);
            stale?.Dispose();
            Interlocked.Increment(ref _version);   // 液态玻璃据此重算折射层

            // 桌面从「全黑」变成「有内容」的那一刻必须让玻璃重算一次（TrackContent 里判定）。
            // 实测启动后十几秒内抓到的都是 (0,0,0)（锁屏/壁纸还没加载完），玻璃就照着黑图生成，
            // 用户看到的是"启动后玻璃不显示、手动切一次质量档才正常"——切质量做的正是 Invalidate。
            // ⚠️ 只能置标记：_glass 位图归渲染线程所有，这里 Dispose 会直接 AV。

            if (x != _loggedX || y != _loggedY)
            {
                _loggedX = x; _loggedY = y;
                // 抓取矩形一变就报一行。四个数一起看就能定位问题：
                //   · 遮罩 = 岛体在【本次图像】里的矩形（空/越界 = 没盖住自己）
                //   · 擦除前 → 擦除后 = 岛体那块有没有被抹掉（前黑后亮 = 抹对了；两个都黑 = 桌面本来就黑）
                //   · 背板中央平均 RGB = 整张图有没有内容（矩形跑到屏幕外会得到全黑）
                int cw = Math.Max(1, sw / 3), chh = Math.Max(1, sh / 3);
                long ar = 0, ag = 0, ab = 0; int cnt = 0;
                for (int j = sh / 3; j < sh / 3 + chh && j < sh; j++)
                    for (int i = sw / 3; i < sw / 3 + cw && i < sw; i++)
                    {
                        int p = (j * sw + i) * 4;
                        ab += Marshal.ReadByte(_pBits, p);
                        ag += Marshal.ReadByte(_pBits, p + 1);
                        ar += Marshal.ReadByte(_pBits, p + 2);
                        cnt++;
                    }
                Info($"[{(_glass ? "液态玻璃" : "亚克力")}/{(CurrentDownscale == 1f ? "原分辨率" : "1/" + CurrentDownscale)}]"
                    + $" 抓屏 ok（{(_dxgiUsed ? "DXGI" : "GDI")}）：屏幕矩形=({x},{y},{w},{h})  图={sw}x{sh}"
                    + $"  遮罩=({mx0},{my0})-({mx1},{my1})"
                    + $"  玻璃区亮度 擦除前={lumBefore}/擦除后={lumAfter}"
                    + $"  背板中央平均 RGB=({(cnt == 0 ? -1 : ar / cnt)},{(cnt == 0 ? -1 : ag / cnt)},{(cnt == 0 ? -1 : ab / cnt)})");
            }
            return true;
        }

        // 把岛体自己占的那块（背板图坐标）抹掉。
        //
        // 填法用「四方向最近邻延展」：每个像素取上下左右四条参考边里**距离最近**的那一个的颜色，
        // 不做插值、也不偏向任何方向。
        // ⚠️ 这里换过两版，都留下明显的方向性痕迹，别再退回去：
        //    · 左右邻列横向插值 → 贯穿整个胶囊的横向渐变带（"条纹"）
        //    · 上下邻行垂直插值 → 胶囊内容被纵向拉长（"上下拉太长了"）
        // 最近邻延展只在 45° 方向留一条分界，玻璃自己的高斯模糊会把它揉开，观感最接近磨砂。
        /// <summary>
        /// 大面积遮罩的低频填充：取四条边（遮罩外紧邻那一圈）的平均色，在遮罩内做双线性过渡。
        /// 相比最近邻延展，这里没有任何「按最近边取色」的方向性分界，所以不会再出现
        /// 45° 菱形 / 圆顶状的伪结构 —— 展开态玻璃上就只剩一层极缓的明暗过渡，
        /// 高斯一糊就是「纯玻璃 + 模糊」，看不出被填充过。
        /// </summary>
        private static unsafe void FillSoft(int sw, int sh, int x0, int y0, int x1, int y1,
            int lx, int rx, int ty, int by, bool hasL, bool hasR, bool hasT, bool hasB)
        {
            byte* bits = (byte*)_pBits;
            float lb = 0, lg = 0, lr = 0; int ln = 0;
            float rb = 0, rg = 0, rr = 0; int rn = 0;
            float tb = 0, tg = 0, tr = 0; int tn = 0;
            float bb = 0, bg = 0, br = 0; int bn = 0;

            if (hasL)
                for (int j = y0; j < y1; j++) { int s = (j * sw + lx) * 4; lb += bits[s]; lg += bits[s + 1]; lr += bits[s + 2]; ln++; }
            if (hasR)
                for (int j = y0; j < y1; j++) { int s = (j * sw + rx) * 4; rb += bits[s]; rg += bits[s + 1]; rr += bits[s + 2]; rn++; }
            if (hasT)
                for (int i = x0; i < x1; i++) { int s = (ty * sw + i) * 4; tb += bits[s]; tg += bits[s + 1]; tr += bits[s + 2]; tn++; }
            if (hasB)
                for (int i = x0; i < x1; i++) { int s = (by * sw + i) * 4; bb += bits[s]; bg += bits[s + 1]; br += bits[s + 2]; bn++; }

            // 整块的平均色：某条边够不到（遮罩贴着抓屏边缘）时用它兜底
            float allB = lb + rb + tb + bb, allG = lg + rg + tg + bg, allR = lr + rr + tr + br;
            int allN = ln + rn + tn + bn;
            float invN = allN > 0 ? 1f / allN : 0f;
            float avgB = allB * invN, avgG = allG * invN, avgR = allR * invN;

            float Lb = ln > 0 ? lb / ln : avgB, Lg = ln > 0 ? lg / ln : avgG, Lr = ln > 0 ? lr / ln : avgR;
            float Rb = rn > 0 ? rb / rn : avgB, Rg = rn > 0 ? rg / rn : avgG, Rr = rn > 0 ? rr / rn : avgR;
            float Tb = tn > 0 ? tb / tn : avgB, Tg = tn > 0 ? tg / tn : avgG, Tr = tn > 0 ? tr / tn : avgR;
            float Bb = bn > 0 ? bb / bn : avgB, Bg = bn > 0 ? bg / bn : avgG, Br = bn > 0 ? br / bn : avgR;

            float mw = MathF.Max(1f, x1 - x0), mh = MathF.Max(1f, y1 - y0);
            float invMw = 1f / mw;
            for (int j = y0; j < y1; j++)
            {
                float fy = (j - y0 + 0.5f) / mh;
                float vb = Tb + (Bb - Tb) * fy, vg = Tg + (Bg - Tg) * fy, vr = Tr + (Br - Tr) * fy;
                int row = j * sw * 4;
                for (int i = x0; i < x1; i++)
                {
                    float fx = (i - x0 + 0.5f) * invMw;
                    float hb = Lb + (Rb - Lb) * fx, hg = Lg + (Rg - Lg) * fx, hr = Lr + (Rr - Lr) * fx;
                    int p = row + i * 4;
                    bits[p] = (byte)Math.Clamp(hb * (1 - fy) + vb * fy, 0, 255);
                    bits[p + 1] = (byte)Math.Clamp(hg * (1 - fy) + vg * fy, 0, 255);
                    bits[p + 2] = (byte)Math.Clamp(hr * (1 - fy) + vr * fy, 0, 255);
                }
            }
        }

        // 返回 false = 遮罩无效/贴边到四条参考边都取不到，这一帧擦不掉岛体（调用方应丢弃这张图）。
        private static bool EraseIsland(int sw, int sh, int x0, int y0, int x1, int y1)
        {
            // 余量：兜住「遮挡矩形比实际岛体晚一帧」的滞后（动画中胶囊在变宽变窄），
            // 以及窗口定位/DWM 取整留下的 1~2px 偏差（那点偏差在玻璃里就是一条岛体自己的边）。
            x0 -= 6; x1 += 6; y0 -= 4; y1 += 4;
            if (x0 < 0) x0 = 0;
            if (y0 < 0) y0 = 0;
            if (x1 > sw) x1 = sw;
            if (y1 > sh) y1 = sh;
            if (x1 - x0 < 1 || y1 - y0 < 1) return false;

            int lx = x0 - 1, rx = x1, ty = y0 - 1, by = y1;
            bool hasL = lx >= 0, hasR = rx < sw, hasT = ty >= 0, hasB = by < sh;
            if (!hasL && !hasR && !hasT && !hasB) return false;

            // ── 大面积（展开态 / 详情页卡片）走低频填充 ──
            // 最近邻延展是按「离哪条边最近」取色的：四条边的分界线正好是 45° 对角线，
            // 抹出来的块因此是个菱形/圆顶，面积一大就透过玻璃显形成「梯形色块」。
            // 折叠态高 35px 看不出来，展开态高 200px+ 就成了明显的伪结构。
            // 大面积改用「四边平均色 + 双线性」的极低频填充：没有任何可辨认的方向性结构，
            // 玻璃再高斯一糊就是纯色玻璃（用户要的「纯玻璃 + 高斯模糊」）。
            if (y1 - y0 >= 64) { FillSoft(sw, sh, x0, y0, x1, y1, lx, rx, ty, by, hasL, hasR, hasT, hasB); return true; }

            byte* bits = (byte*)_pBits;
            for (int j = y0; j < y1; j++)
            {
                int row = j * sw * 4;
                for (int i = x0; i < x1; i++)
                {
                    int dl = hasL ? i - lx : int.MaxValue;
                    int dr = hasR ? rx - i : int.MaxValue;
                    int dt = hasT ? j - ty : int.MaxValue;
                    int db = hasB ? by - j : int.MaxValue;
                    int best = Math.Min(Math.Min(dl, dr), Math.Min(dt, db));

                    int si;
                    if (best == dl) si = row + lx * 4;
                    else if (best == dr) si = row + rx * 4;
                    else if (best == dt) si = ty * sw * 4 + i * 4;
                    else si = by * sw * 4 + i * 4;

                    int p = row + i * 4;
                    bits[p] = bits[si];
                    bits[p + 1] = bits[si + 1];
                    bits[p + 2] = bits[si + 2];
                }
            }

            // 最近邻延展在 45° 会留硬分界：折叠态面积小看不出来，展开态整块胶囊被填成大色斑，
            // 观感就是"尺寸一大背景就被放大了"。补一次盒式模糊把分界揉开（玻璃自己的高斯只有 4px）。
            SoftenErasedRegion(sw, sh, x0, y0, x1, y1);
            return true;
        }

        private static byte[] _eraseSrc = Array.Empty<byte>();
        private static byte[] _eraseDst = Array.Empty<byte>();

        // 只柔化擦除区：两趟盒式（水平 + 垂直）。
        // 半径按擦除区短边自适应：折叠态面积小 6 就够；展开态整块都是填充，
        // 半径小了色斑分界还在（观感就是"背景被放大了"），必须揉成均匀雾面。
        private static void SoftenErasedRegion(int sw, int sh, int x0, int y0, int x1, int y1)
        {
            int w = x1 - x0, h = y1 - y0;
            if (w < 6 || h < 6) return;

            int need = w * h * 4;
            if (_eraseSrc.Length < need)
            {
                _eraseSrc = new byte[need];
                _eraseDst = new byte[need];
            }

            int R = Math.Max(6, Math.Min(w, h) / 8);
            byte* bits = (byte*)_pBits;
            fixed (byte* src = _eraseSrc)
            fixed (byte* dst = _eraseDst)
            {
                for (int j = 0; j < h; j++)
                    Buffer.MemoryCopy(bits + (long)(y0 + j) * sw * 4 + x0 * 4,
                        src + (long)j * w * 4, w * 4, w * 4);

                // 水平趟：src → dst
                for (int j = 0; j < h; j++)
                {
                    byte* s = src + (long)j * w * 4;
                    byte* d = dst + (long)j * w * 4;
                    for (int i = 0; i < w; i++)
                    {
                        int i0 = i - R < 0 ? 0 : i - R;
                        int i1 = i + R >= w ? w - 1 : i + R;
                        int b = 0, g = 0, r = 0, n = 0;
                        for (int k = i0; k <= i1; k++)
                        {
                            byte* q = s + k * 4;
                            b += q[0]; g += q[1]; r += q[2];
                            n++;
                        }
                        byte* o = d + i * 4;
                        o[0] = (byte)(b / n); o[1] = (byte)(g / n); o[2] = (byte)(r / n);
                    }
                }

                // 垂直趟：dst → src
                for (int i = 0; i < w; i++)
                {
                    for (int j = 0; j < h; j++)
                    {
                        int j0 = j - R < 0 ? 0 : j - R;
                        int j1 = j + R >= h ? h - 1 : j + R;
                        int b = 0, g = 0, r = 0, n = 0;
                        for (int k = j0; k <= j1; k++)
                        {
                            byte* q = dst + (long)k * w * 4 + i * 4;
                            b += q[0]; g += q[1]; r += q[2];
                            n++;
                        }
                        byte* o = src + (long)j * w * 4 + i * 4;
                        o[0] = (byte)(b / n); o[1] = (byte)(g / n); o[2] = (byte)(r / n);
                    }
                }

                for (int j = 0; j < h; j++)
                    Buffer.MemoryCopy(src + (long)j * w * 4,
                        bits + (long)(y0 + j) * sw * 4 + x0 * 4, w * 4, w * 4);
            }
        }

        // 某块区域的平均亮度（0..255）+ 极差：只看【玻璃真正采样的那块】，绕开"整图有内容但胶囊全黑"的误判。
        // 返回 -1 = 这块没有有效面积（岛体滑出抓取矩形外），调用方应跳过判定。
        private static int SampleLuminance(byte* bits, int stride, int iw, int ih,
                                           int x0, int y0, int x1, int y1, out int range)
        {
            range = 0;
            if (bits == null || iw < 2 || ih < 2) return -1;
            if (x0 < 0) x0 = 0;
            if (y0 < 0) y0 = 0;
            if (x1 > iw) x1 = iw;
            if (y1 > ih) y1 = ih;
            int w = x1 - x0, h = y1 - y0;
            if (w < 2 || h < 2) return -1;

            int stepX = Math.Max(1, w / 24), stepY = Math.Max(1, h / 12);
            long sum = 0;
            int n = 0, lo = 255, hi = 0;
            for (int j = y0; j < y1; j += stepY)
            {
                byte* row = bits + j * stride;
                for (int i = x0; i < x1; i += stepX)
                {
                    byte* q = row + i * 4;
                    int v = (q[0] * 29 + q[1] * 150 + q[2] * 77) >> 8;   // BT.601 近似
                    sum += v; n++;
                    if (v < lo) lo = v;
                    if (v > hi) hi = v;
                }
            }
            if (n == 0) return -1;
            range = hi - lo;
            return (int)(sum / n);
        }

        /// <summary>
        /// 抓屏线程：给刚抓到的那块打「有没有内容」的标记，并顺手自愈。
        ///
        /// dead = 平均亮度 &lt; 6 且极差 &lt; 4（纯黑，或整块死平）。两个出口：
        ///   · 连续死黑 → 重建 DC / DIB 再抓（等价于用户在设置里点一次「玻璃质量」做的 Expire）。
        ///     典型场景：显示器刚唤醒 / DWM 刚重置，GDI 那张位图会一直是黑帧，不重建永远黑。
        ///   · 死黑 → 有内容 → 置 _glassWake，让渲染线程把玻璃缓存作废，别拿黑图生成的图层继续用。
        /// ⚠️ 只能置标记：_glass 位图归渲染线程所有，后台 Dispose 会直接 AV。
        /// </summary>
        private static void TrackContent(int lum, int range)
        {
            if (lum < 0) return;   // 这块没有有效面积（岛体滑出去了）→ 不判定

            bool dead = lum < 6 && range < 4;
            long now = Environment.TickCount64;

            if (dead)
            {
                _deadStreak++;
                if (_deadStreak >= 8 && _deadExpires < 3 && now - _lastDeadExpireMs >= 1500)
                {
                    _deadExpires++;
                    _lastDeadExpireMs = now;
                    _expire = true;
                    _dirty = true;
                    _work.Set();
                    if (_guardLogged != 0)
                    {
                        _guardLogged = 0;
                        Info($"[背板] 内容守卫：玻璃源矩形连续 {_deadStreak} 帧无内容（亮度 {lum}/极差 {range}），重建抓屏缓冲（第 {_deadExpires} 次）");
                    }
                }
            }
            else
            {
                if (_deadStreak > 0)
                {
                    _glassWake = true;   // 死→活：玻璃必须重算，否则还是黑图生成的那一层
                    if (_guardLogged != 1)
                    {
                        _guardLogged = 1;
                        Info($"[背板] 内容守卫：玻璃源矩形恢复内容（亮度 {lum}/极差 {range}）");
                    }
                }
                _deadStreak = 0;
                _deadExpires = 0;
            }
            _contentAlive = !dead;
        }

        private static void EnsureBuffer(int sw, int sh)
        {
            if (_memDc != IntPtr.Zero && _capW == sw && _capH == sh) return;
            FreeBuffer();

            IntPtr sdc = Win32.GetDC(IntPtr.Zero);
            _memDc = Win32.CreateCompatibleDC(sdc);
            var bmi = new Win32.BITMAPINFO
            {
                bmiHeader = new Win32.BITMAPINFOHEADER
                {
                    biSize = (uint)Marshal.SizeOf(typeof(Win32.BITMAPINFOHEADER)),
                    biWidth = sw,
                    biHeight = -sh,      // 负数 = 从上到下
                    biPlanes = 1,
                    biBitCount = 32,
                    biCompression = 0
                }
            };
            _hBitmap = Win32.CreateDIBSection(sdc, ref bmi, Win32.DIB_RGB_COLORS, out _pBits, IntPtr.Zero, 0);
            _oldBitmap = Win32.SelectObject(_memDc, _hBitmap);
            Win32.ReleaseDC(IntPtr.Zero, sdc);

            _capW = sw; _capH = sh;
        }

        private static void FreeBuffer()
        {
            if (_memDc != IntPtr.Zero && _oldBitmap != IntPtr.Zero) Win32.SelectObject(_memDc, _oldBitmap);
            if (_hBitmap != IntPtr.Zero) Win32.DeleteObject(_hBitmap);
            if (_memDc != IntPtr.Zero) Win32.DeleteDC(_memDc);
            _memDc = _hBitmap = _oldBitmap = _pBits = IntPtr.Zero;
            _capW = _capH = 0;
        }
    }
}
