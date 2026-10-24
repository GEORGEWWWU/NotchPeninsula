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
    // 内存口径：GDI 直接 StretchBlt 成 1/12 小图（约 200x37），绝不把 4MB 的整屏原图搬进托管堆；
    //          每次只新产一张 ~30KB 的 SKImage，旧的那张在渲染线程手里、由它释放。
    internal static unsafe class IslandBackdrop
    {
        private const int RefreshMs = 250;      // 背板刷新节拍（屏幕读取很贵，别太频繁）
        public const float Downscale = 12f;     // 缩略倍数：越大越糊（实测 k=8→std31 / k=16→std6.6）

        // 液态玻璃用的降采样：折射位移得看得出来，1/12 那种糊度会把玻璃拍成一团色块。
        // 随「玻璃质量」变（精细档 1:1），所以是属性不是常量。
        public static float GlassDownscale => LiquidGlass.CaptureDownscale;

        // 渲染线程每帧告知一次当前材质；切换会触发过期重抓
        private static volatile bool _glass;

        // 抓屏方式：true = 走放大镜 API（玻璃模式 1:1 全分辨率），false = 老 GDI StretchBlt。
        // 放大镜不可用时会自动回退到 GDI，这个开关只是用户偏好。
        public static bool UseMagnifier { get; set; } = true;

        private static volatile bool _magUsed;

        public static bool UsingMagnifier => _magUsed;

        // 本帧背板对应的【屏幕原点】：源矩形换算要用它，不能假定等于请求值
        // （放大镜在屏幕边界处可能裁掉一截，GDI 则严格等于请求值）
        private static volatile int _originX, _originY;

        public static int LastOriginX => _originX;

        public static int LastOriginY => _originY;

        // 背板图 1 像素 = 屏幕多少像素（= 1 / 降采样倍数，放大镜就是 1）。
        // 用实际产出图的尺寸反推，而不是用请求值 —— 首帧/边界裁剪时也不会算错源矩形。
        private static volatile float _scale = 1f / Downscale;

        public static float LastScale => _scale > 0f ? _scale : 1f / Downscale;

        public static float CurrentDownscale => _magUsed ? 1f : (_glass ? GlassDownscale : Downscale);

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
        private static volatile bool _run;
        private static readonly AutoResetEvent _work = new(false);
        private static IntPtr _memDc, _hBitmap, _oldBitmap, _pBits;
        private static int _capW, _capH;
        private static long _lastMs;
        private static bool _disabled;
        private static int _loggedX = int.MinValue, _loggedY = int.MinValue;

        // 上一帧背板的整体亮度：用来抓「全黑 → 有内容」这一跳（见 Capture 尾部）
        private static int _lastLum;

        // 背板从全黑变亮的标记：渲染线程取走它再自己调 LiquidGlass.Invalidate()
        private static volatile bool _glassWake;

        /// <summary>渲染线程：背板刚从全黑变亮就要把玻璃缓存作废（后台线程不能碰 _glass 位图）。</summary>
        public static bool TakeGlassWake()
        {
            if (!_glassWake) return false;
            _glassWake = false;
            return true;
        }

        // 胶囊在背板图里的矩形（渲染线程每帧写 / 抓屏线程读）：用来把岛体自己那块抹掉。
        // 不用锁：读到的最多是上一帧的矩形，擦除区再放几像素余量即可兜住。
        private static volatile int _maskX0, _maskY0, _maskX1, _maskY1;

        // 渲染线程：告诉抓屏线程「岛体占的是背板图里的哪一块」（图像像素坐标）
        public static void SetMask(float x0, float y0, float x1, float y1)
        {
            // 放大镜抓屏已经通过「排除窗口列表」把岛体剔掉了，不用再抹自己 ——
            // 遮罩一动还会触发重抓，白白浪费一次屏幕读取。
            if (_magUsed && _glass) return;

            int nx0 = (int)MathF.Floor(x0), ny0 = (int)MathF.Floor(y0);
            int nx1 = (int)MathF.Ceiling(x1), ny1 = (int)MathF.Ceiling(y1);
            bool moved = Math.Abs(nx1 - _maskX1) > 3 || Math.Abs(nx0 - _maskX0) > 3 || Math.Abs(ny1 - _maskY1) > 3;
            _maskX0 = nx0; _maskY0 = ny0; _maskX1 = nx1; _maskY1 = ny1;

            // 遮挡区在明显变化（展开/收起动画）→ 尽快用新矩形重抓一次，免得岛体边缘漏进模糊里；但要节流
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

        private static long _lastMaskWakeMs;

        public static void ClearMask() => _maskX0 = _maskY0 = _maskX1 = _maskY1 = 0;


        // ---- 交给渲染线程的成品（交换后由渲染线程释放）----
        private static SKImage? _pending;

        public static void Expire() { _expire = true; _dirty = true; _magForce = true; _work.Set(); }

        // 渲染线程：告诉后台线程「要抓这块屏幕矩形」。矩形没变就不会重新抓。
        public static void SetTarget(int x, int y, int w, int h)
        {
            if (x == _tx && y == _ty && w == _tw && h == _th) return;
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
        /// target 一直是 0×0 = 渲染侧没给矩形；dirty=False 且 target 正常 = 线程没被唤醒。
        /// </summary>
        public static string DebugState
            => $"target={_tw}x{_th}@{_tx},{_ty} dirty={_dirty} expire={_expire} "
             + $"disabled={_disabled} thread={(_thread != null)} lastMs={_lastMs}";

        public static void Stop()
        {
            _run = false;
            _work.Set();
            _thread = null;
            var p = Interlocked.Exchange(ref _pending, null);
            p?.Dispose();
        }

        private static void EnsureThread()
        {
            if (_thread != null) return;
            _run = true;
            _thread = new Thread(ThreadLoop)
            {
                IsBackground = true,
                Name = "NPS-Backdrop",
                Priority = ThreadPriority.BelowNormal
            };
            _thread.Start();
        }

        private static void ThreadLoop()
        {
            while (_run)
            {
                // 等待时长跟随当前节拍：自定义成 60ms 时不能被固定的 250ms 等待拖住
                _work.WaitOne(_glass ? LiquidGlass.RefreshMs : RefreshMs);
                if (!_run) break;

                if (_expire)
                {
                    _expire = false;
                    FreeBuffer();
                    var old = Interlocked.Exchange(ref _pending, null);
                    old?.Dispose();
                    _lastMs = 0;
                }

                if (_disabled || _tw < 16 || _th < 16) continue;

                long now = Environment.TickCount64;
                int gap = _glass ? LiquidGlass.RefreshMs : RefreshMs;
                if (!_dirty && now - _lastMs < gap) continue;
                _dirty = false;

                try
                {
                    if (Capture()) _lastMs = now;
                    else _lastMs = now;    // 失败也别每 250ms 重试同一帧
                }
                catch (Exception ex)
                {
                    Error("[IslandBackdrop] 抓取背板失败，亚克力已停用", ex);
                    _disabled = true;
                    FreeBuffer();
                }
            }
        }

        private static bool Capture()
        {
            int w = _tw, h = _th, x = _tx, y = _ty;
            if (w < 16 || h < 16) return false;

            // 玻璃 + 放大镜可用 → 走全分辨率那条线（亚克力仍走 GDI：1/12 缩略对它绰绰有余）
            if (_glass && UseMagnifier && MagnifierBackdrop.IsAvailable)
                return CaptureMagnified(x, y, w, h);

            // 还不知道岛体占哪块 → 先别抓（不然会把岛体自己拍进去，画面会往涂层色糊成一坨）
            int mx0 = _maskX0, my0 = _maskY0, mx1 = _maskX1, my1 = _maskY1;
            if (mx1 <= mx0 || my1 <= my0) return false;

            float ds = CurrentDownscale;
            int sw = Math.Max(1, (int)(w / ds));
            int sh = Math.Max(1, (int)(h / ds));
            EnsureBuffer(sw, sh);
            if (_memDc == IntPtr.Zero) return false;

            IntPtr sdc = Win32.GetDC(IntPtr.Zero);
            if (sdc == IntPtr.Zero) return false;
            try
            {
                // GDI 一步缩到 1/12，不经过全尺寸原图
                Win32.SetStretchBltMode(_memDc, Win32.HALFTONE);
                if (!Win32.StretchBlt(_memDc, 0, 0, sw, sh, sdc, x, y, w, h, Win32.SRCCOPY)) return false;
            }
            finally
            {
                Win32.ReleaseDC(IntPtr.Zero, sdc);
            }

            // ⚠️ 这里**不再用 SetWindowDisplayAffinity(WDA_EXCLUDEFROMCAPTURE)**。
            // 它会让窗口在录屏/截图里「隐身」，而我们必须每 250ms 开关它一次（抓屏时开、抓完关）——
            // 实测按这个节奏，OBS 一类的捕获端有 **11.5% 的帧看不到岛体**，观感就是「亚克力模式下灵动岛一直闪」。
            // 改成：照抓（岛体在内），然后把岛体自己占的那块用**左右邻像素横向插值**抹掉 ——
            // 岛体在屏幕顶部居中，同一行左右两侧就是它背后的那层（标题栏/壁纸），1/12 缩略 + 平滑后看不出差别。
            EraseIsland(sw, sh, mx0, my0, mx1, my1);

            // ⚠️ 必须把 DIB 的 alpha 字节全部置 255。
            // StretchBlt/BitBlt 只写 BGR、**不写 alpha**（留下 0），而 SKImage.FromPixels 会把 a=0 当「全透明」——
            // 贴上去等于什么都没贴，亚克力整体消失（实测：不置 alpha → 画进画布后整块变透明）。
            for (int i = 3, n = sw * sh * 4; i < n; i += 4) Marshal.WriteByte(_pBits, i, 255);

            var info = new SKImageInfo(sw, sh, SKColorType.Bgra8888, SKAlphaType.Opaque);
            using var src = SKImage.FromPixels(info, _pBits, sw * 4);
            if (src == null) return false;

            SKImage? img;
            if (_glass)
            {
                // 液态玻璃：这里只做 1:1 搬运，保留抓屏原始清晰度
                // （StretchBlt 的降采样本身已是一次盒式平均）。真正的高斯模糊交给
                // LiquidGlass 只在「胶囊 + 折射余量」那一小块上做 —— 全屏糊一遍太贵。
                using var raw = SKSurface.Create(new SKImageInfo(sw, sh, SKColorType.Bgra8888, SKAlphaType.Premul));
                if (raw == null) return false;
                raw.Canvas.DrawImage(src, 0f, 0f, _resample);
                raw.Canvas.Flush();
                img = raw.Snapshot();
            }
            else
            {
                // 无滤镜等效模糊：先降到 1/2（双线性 = 盒式平均），再升回原尺寸
                int hw = Math.Max(1, sw / 2), hh = Math.Max(1, sh / 2);
                using var half = SKSurface.Create(new SKImageInfo(hw, hh, SKColorType.Bgra8888, SKAlphaType.Premul));
                if (half == null) return false;
                half.Canvas.DrawImage(src, new SKRect(0, 0, hw, hh), _resample);
                half.Canvas.Flush();
                using var halfImg = half.Snapshot();
                if (halfImg == null) return false;

                using var smooth = SKSurface.Create(new SKImageInfo(sw, sh, SKColorType.Bgra8888, SKAlphaType.Premul));
                if (smooth == null) return false;
                smooth.Canvas.DrawImage(halfImg, new SKRect(0, 0, sw, sh), _resample);
                smooth.Canvas.Flush();
                img = smooth.Snapshot();
            }
            if (img == null) return false;

            // 旧的那张还没被渲染线程取走 → 直接丢掉它（我们自己产的，安全）
            _originX = x;
            _originY = y;
            _scale = sw / (float)w;
            _magUsed = false;

            var stale = Interlocked.Exchange(ref _pending, img);
            stale?.Dispose();
            Interlocked.Increment(ref _version);   // 液态玻璃据此重算折射层

            // 桌面从「全黑」变成「有内容」的那一刻必须让玻璃重算一次。
            // 实测启动后十几秒内抓到的都是 (0,0,0)（锁屏/壁纸还没加载完），玻璃就照着黑图生成，
            // 用户看到的是"启动后玻璃不显示、手动切一次质量档才正常"——切质量做的正是 Invalidate。
            // 这里盯住这一跳，替用户做掉。
            // ⚠️ 只能置标记：_glass 位图归渲染线程所有，这里 Dispose 会直接 AV。
            if (_glass)
            {
                int lum = SampleLuminance(sw, sh);
                if (_lastLum < 6 && lum >= 6) _glassWake = true;
                _lastLum = lum;
            }

            if (x != _loggedX || y != _loggedY)
            {
                _loggedX = x; _loggedY = y;
                // 抓取矩形一变就报一行：把它和「背板中央平均 RGB」一起看 —— 矩形跑到屏幕外会得到全黑。
                // 直接从 DIB 取样（BGRA），不再建 SKPixmap。
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
                    + $" 抓屏 ok：屏幕矩形=({x},{y},{w},{h})  图={sw}x{sh}"
                    + $"  背板中央平均 RGB=({(cnt == 0 ? -1 : ar / cnt)},{(cnt == 0 ? -1 : ag / cnt)},{(cnt == 0 ? -1 : ab / cnt)})");
            }
            return true;
        }

        // ---- 放大镜路径：UI 线程抓，这里只取走成品 ----
        // 与 GDI 那条线的差别：1:1 全分辨率（不降采样）、岛体被排除列表直接剔掉（不用再抹自己）。
        private static bool CaptureMagnified(int x, int y, int w, int h)
        {
            // 告诉宿主窗口"抓这块"，真正的抓取在 UI 线程的下一条消息里发生（异步，不阻塞渲染线程）
            MagnifierBackdrop.Request(x, y, w, h);

            if (!MagnifierBackdrop.Take(out byte[] buf, out int bw, out int bh,
                    out int ox, out int oy, out int ver))
                return false;

            // 同一张图就别重复做一遍快照（放大镜回调频率可能高于刷新节拍）
            if (ver == _magVersion && !_magForce) return false;
            _magVersion = ver;
            _magForce = false;

            // 放大镜裁过一截时以它给的原点为准，否则源矩形会偏
            if (ox == 0 && oy == 0) { ox = x; oy = y; }

            fixed (byte* p = buf)
            {
                var info = new SKImageInfo(bw, bh, SKColorType.Bgra8888, SKAlphaType.Opaque);
                using var src = SKImage.FromPixels(info, (IntPtr)p, bw * 4);
                if (src == null) return false;

                // 玻璃模式：原样搬运，模糊留给 LiquidGlass 只在胶囊那一小块上做
                using var raw = SKSurface.Create(new SKImageInfo(bw, bh, SKColorType.Bgra8888, SKAlphaType.Premul));
                if (raw == null) return false;
                raw.Canvas.DrawImage(src, 0f, 0f, _resample);
                raw.Canvas.Flush();
                var img = raw.Snapshot();
                if (img == null) return false;

                _originX = ox;
                _originY = oy;
                _scale = bw / (float)w;
                _magUsed = true;

                var stale = Interlocked.Exchange(ref _pending, img);
                stale?.Dispose();
                Interlocked.Increment(ref _version);
                return true;
            }
        }

        private static int _magVersion;

        private static bool _magForce;

        // 把岛体自己占的那块（背板图坐标）抹掉。
        //
        // 填法用「四方向最近邻延展」：每个像素取上下左右四条参考边里**距离最近**的那一个的颜色，
        // 不做插值、也不偏向任何方向。
        // ⚠️ 这里换过两版，都留下明显的方向性痕迹，别再退回去：
        //    · 左右邻列横向插值 → 贯穿整个胶囊的横向渐变带（"条纹"）
        //    · 上下邻行垂直插值 → 胶囊内容被纵向拉长（"上下拉太长了"）
        // 最近邻延展只在 45° 方向留一条分界，玻璃自己的高斯模糊会把它揉开，观感最接近磨砂。
        private static void EraseIsland(int sw, int sh, int x0, int y0, int x1, int y1)
        {
            // 余量：兜住「遮挡矩形比实际岛体晚一帧」的滞后（动画中胶囊在变宽变窄）
            x0 -= 2; x1 += 2; y0 -= 1; y1 += 1;
            if (x0 < 0) x0 = 0;
            if (y0 < 0) y0 = 0;
            if (x1 > sw) x1 = sw;
            if (y1 > sh) y1 = sh;
            if (x1 - x0 < 1 || y1 - y0 < 1) return;

            int lx = x0 - 1, rx = x1, ty = y0 - 1, by = y1;
            bool hasL = lx >= 0, hasR = rx < sw, hasT = ty >= 0, hasB = by < sh;
            if (!hasL && !hasR && !hasT && !hasB) return;

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

        // 背板整体亮度（0..255）：抽样 64×32 网格，避开四周一圈，够判断"是不是全黑"就行
        private static int SampleLuminance(int sw, int sh)
        {
            if (_pBits == IntPtr.Zero || sw < 8 || sh < 8) return 0;
            byte* bits = (byte*)_pBits;
            int stepX = Math.Max(1, sw / 64), stepY = Math.Max(1, sh / 32);
            long sum = 0;
            int n = 0;
            for (int j = sh / 8; j < sh - sh / 8; j += stepY)
            {
                int row = j * sw * 4;
                for (int i = sw / 8; i < sw - sw / 8; i += stepX)
                {
                    int p = row + i * 4;
                    sum += bits[p] + bits[p + 1] + bits[p + 2];
                    n++;
                }
            }
            return n == 0 ? 0 : (int)(sum / (n * 3));
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
