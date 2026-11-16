using System.Runtime.InteropServices;
using SkiaSharp;
using static NotchPeninsula.Logger;

namespace NotchPeninsula
{
    // Windows 10 亚克力兼容背板。后台线程用 GDI 缩放和擦除岛体蒙版，
    // 产物通过 Interlocked 移交给渲染线程；Windows 11 及液态玻璃不使用本类抓取桌面。
    internal static unsafe class IslandBackdrop
    {
        private const int RefreshMs = 250;      // 背板刷新节拍（屏幕读取很贵，别太频繁）
        private const int MinCaptureMs = 80;
        private const int MaskSettleMs = 260;

        // 本帧背板对应的【屏幕原点】：源矩形换算要用它，不能假定等于请求值（屏幕边界处可能裁掉一截）
        private static volatile int _originX, _originY;

        public static int LastOriginX => _originX;

        public static int LastOriginY => _originY;

        // 背板图 1 像素 = 屏幕多少像素（= 1 / 降采样倍数）。
        // 用实际产出图的尺寸反推，而不是用请求值 —— 首帧/边界裁剪时也不会算错源矩形。
        private static volatile float _scale = 1f;

        public static float LastScale => _scale > 0f ? _scale : 1f;

        private static int _version;
        public static int Version => Volatile.Read(ref _version);

        // ⚠️ 这里的平滑**绝不能用 SKImageFilter.CreateBlur**：Skia 会把「每次过滤的产物」按源图的 uniqueID
        // 缓存起来，而背板每 250ms 就产出一张**新图** → 缓存只增不减。实测（纯管道 200s）：
        // 带滤镜 +13.9MB/分（私有内存 50.7→92.3MB），去掉滤镜后 0 增长。这就是「内存暴增」的真凶。
        // 改用「降到 1/2 再升回」的双线性等效模糊：无滤镜、无缓存、开销极小。
        private static readonly SKPaint _resample = new()
        { IsAntialias = true, FilterQuality = SKFilterQuality.Low };

        // ---- 渲染线程写入 ----
        private static volatile int _tx, _ty, _tw, _th;
        private static volatile bool _dirty;
        private static volatile bool _expire;

        // ---- 后台线程独占 ----
        private static Thread? _thread;
        private static readonly object _threadLock = new();
        private static volatile bool _run;
        private static readonly AutoResetEvent _work = new(false);
        private static IntPtr _memDc, _hBitmap, _oldBitmap, _pBits;
        private static int _capW, _capH;
        private static long _lastMs;
        private static bool _disabled;
        private static int _loggedX = int.MinValue, _loggedY = int.MinValue;

        private static int _statOk, _statFail;
        private static long _diagMs;
        private static int _diagVersion;
        private static long _diagVersionMs;

        // 遮罩用屏幕物理像素，抓取时按本次背板原点转换。
        private static volatile float _maskSx0, _maskSy0, _maskSx1, _maskSy1;

        // ---- 额外遮罩：实况窗详情页面板 + 左侧实况窗本体 ----
        // 同一分层窗口上的实况窗也要从兼容背板里擦除。
        public const int MaxExtraMaskRects = 10;   // 详情页面板 1 + 气泡 8 + 余量 1

        private static readonly object _extraMaskLock = new();
        private static readonly float[] _extraMaskRects = new float[MaxExtraMaskRects * 4];
        private static int _extraMaskCount;
        private static long _lastExtraMaskWakeMs;

        // 抓屏线程独占的拷贝，避免擦除期间一直持锁（会把渲染线程的发布卡住）。
        private static readonly float[] _extraMaskCapture = new float[MaxExtraMaskRects * 4];

        // ---- 擦除矩形 = 「当前岛体」∪「最近 400ms 内岛体待过的地方」----
        // GDI 取到的桌面帧可能带着岛体上一帧的位置和尺寸。
        // 而擦除用的是"此刻"的矩形。展开/收起（以及启动时的入场动画）过程中岛体尺寸一直在变，
        // 只擦当前矩形就会在边缘留下一圈上一帧的岛体像素 —— 玻璃里透出来就是**错位重影 / 双灵动岛**。
        // 把最近一小段时间的矩形并起来擦，大小动画的每一帧都能盖住；700ms 覆盖最慢的抓屏节拍有余。
        // 代价只有"多擦一圈"：这圈位置本来就在岛体背后，视觉上看不出来。
        private static volatile float _eraseUx0, _eraseUy0, _eraseUx1, _eraseUy1;
        private static long _eraseUnionMs;
        private const long EraseUnionMs = 400;

        // 并集每边最多比当前岛体多扩这么多：擦掉的面积就是要用平均色填掉的面积，
        private const float EraseUnionPadX = 20f;
        private const float EraseUnionPadY = 14f;

        // 渲染线程：告诉抓屏线程「岛体在屏幕上占哪一块」（屏幕物理像素）
        public static void SetMask(float x0, float y0, float x1, float y1)
        {
            // 形状一变，环带采样的位置也跟着变（展开/收起动画里每帧都在变）。
            // 尺寸变化期间延后抓取，等遮罩与合成画面稳定。
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
        }

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
        /// target 一直是 0×0 = 渲染侧没给矩形；dirty=False 且 target 正常 = 线程没被唤醒；
        /// </summary>
        public static string DebugState
            => $"target={_tw}x{_th}@{_tx},{_ty} dirty={_dirty} expire={_expire} "
             + $"disabled={_disabled} thread={(_thread?.IsAlive == true)} lastMs={_lastMs} "
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
                // 保留线程引用，直到旧线程退出再允许 EnsureThread 重建。
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
                _work.WaitOne(_tw < 16 || _th < 16 || _disabled ? Timeout.Infinite : MinCaptureMs);
                if (!_run) break;

                if (_expire)
                {
                    _expire = false;
                    FreeBuffer();
                    var old = Interlocked.Exchange(ref _pending, null);
                    old?.Dispose();
                    _lastMs = 0;
                }

                if (_disabled || !Renderer.IslandAcrylic || _tw < 16 || _th < 16) continue;

                long now = Environment.TickCount64;
                if (now - _lastMs < RefreshMs || now - _maskShapeMs < MaskSettleMs) continue;
                _dirty = false;
                try
                {
                    _lastMs = now;
                    if (Capture()) { _statOk++; NoteCaptureOk(); }
                    else { _statFail++; NoteCaptureFail(); }
                }
                catch (Exception ex)
                {
                    Error("[IslandBackdrop] 抓取背板失败，亚克力已停用", ex);
                    _capFailStage = CapFailDisabled;
                    _disabled = true;
                    FreeBuffer();
                }

                DiagTick();
            }
            FreeBuffer();
        }

        private static void DiagTick()
        {
            long now = Environment.TickCount64;
            int v = _version;
            if (v != _diagVersion)
            {
                _diagVersion = v;
                _diagVersionMs = now;
            }
            if (_diagMs == 0) { _diagMs = now; return; }
            if (now - _diagMs < 10000) return;

            long stuckMs = now - _diagVersionMs;
            string head = $"[背板] 近 10s 抓屏 {_statOk} 次 / 失败 {_statFail} 次 · version={v}（{stuckMs}ms 未变）"
                + $" · GDI · target={_tw}x{_th}@{_tx},{_ty}"
                + $" · mask=({_maskSx0:F0},{_maskSy0:F0})-({_maskSx1:F0},{_maskSy1:F0})";
            if (_statOk == 0 || stuckMs > 6000) Warn(head);
            else Info(head);
            _statOk = 0;
            _statFail = 0;
            _diagMs = now;
        }

        private static long _maskShapeMs, _failLogMs;
        private static string _failWhy = "";

        private static bool Capture()
        {
            int w = _tw, h = _th, x = _tx, y = _ty;
            if (w < 16 || h < 16) return Fail(CapFailTargetTooSmall, $"target={w}x{h}");

            // 还不知道岛体占哪块 → 先别抓（不然会把岛体自己拍进去，画面会往涂层色糊成一坨）
            float mSx0 = _maskSx0, mSy0 = _maskSy0, mSx1 = _maskSx1, mSy1 = _maskSy1;
            if (mSx1 - mSx0 < 1f || mSy1 - mSy0 < 1f)
                return Fail(CapFailMaskMissing, $"mask=({mSx0:F0},{mSy0:F0})-({mSx1:F0},{mSy1:F0})");

            int sw = w, sh = h;
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

            IntPtr sdc = Win32.GetDC(IntPtr.Zero);
            if (sdc == IntPtr.Zero) return Fail(CapFailScreenDc, "GetDC(NULL)");
            try
            {
                Win32.SetStretchBltMode(_memDc, Win32.HALFTONE);
                if (!Win32.StretchBlt(_memDc, 0, 0, sw, sh, sdc, x, y, w, h, Win32.SRCCOPY))
                    return Fail(CapFailStretchBlt, $"src=({x},{y},{w},{h}) dst={sw}x{sh}");
            }
            finally
            {
                Win32.ReleaseDC(IntPtr.Zero, sdc);
            }
            Win32.GdiFlush();

            int lumBefore = SampleLuminance((byte*)_pBits, sw * 4, sw, sh, mx0, my0, mx1, my1, out _);
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

            int lumAfter = SampleLuminance((byte*)_pBits, sw * 4, sw, sh, mx0, my0, mx1, my1, out _);

            // ⚠️ 必须把 DIB 的 alpha 字节全部置 255。
            // StretchBlt/BitBlt 只写 BGR、**不写 alpha**（留下 0），而 SKImage.FromPixels 会把 a=0 当「全透明」——
            // 贴上去等于什么都没贴，亚克力整体消失（实测：不置 alpha → 画进画布后整块变透明）。
            // 按 uint 整字置位：逐字节 Marshal.WriteByte 在 1080p 下是 140 万次托管调用（7~14ms/次）。
            uint* pxAlpha = (uint*)_pBits;
            for (int i = 0, n = sw * sh; i < n; i++) pxAlpha[i] |= 0xFF000000u;

            var info = new SKImageInfo(sw, sh, SKColorType.Bgra8888, SKAlphaType.Opaque);
            using var src = SKImage.FromPixels(info, _pBits, sw * 4);
            if (src == null) return Fail(CapFailSkia, "SKImage.FromPixels");

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
            var img = smooth.Snapshot();
            if (img == null) return Fail(CapFailSkia, "snapshot(final)");
            // 旧的那张还没被渲染线程取走 → 直接丢掉它（我们自己产的，安全）
            _originX = x;
            _originY = y;
            _scale = sw / (float)w;

            var stale = Interlocked.Exchange(ref _pending, img);
            stale?.Dispose();
            Interlocked.Increment(ref _version);

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
                Info($"[亚克力/原分辨率] 抓屏 ok（GDI）：屏幕矩形=({x},{y},{w},{h})  图={sw}x{sh}"
                    + $"  遮罩=({mx0},{my0})-({mx1},{my1})"
                    + $"  岛体区域亮度 擦除前={lumBefore}/擦除后={lumAfter}"
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
