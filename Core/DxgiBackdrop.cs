using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using static NotchPeninsula.Logger;

namespace NotchPeninsula
{
    // DXGI Desktop Duplication 抓屏后端（替代放大镜 API）。
    //
    // 为什么换：
    //   · 放大镜 API 在不少机器上初始化失败（本机日志连续 8 次「环境不支持放大镜抓屏」），
    //     回退到 GDI 后要先把岛体拍进去再擦除，展开态会留下可辨的填充痕迹；
    //   · 放大镜是系统辅助功能组件，与部分录屏/截图软件互相干扰；
    //   · DXGI 直接拿 DWM 合成后的桌面，GPU 侧完成采集，脏矩形机制让静止桌面几乎零开销
    //     （GDI 是每 250ms 全画面 BitBlt，实测单次 18~23ms）。
    //
    // 与放大镜的关键差别：**DXGI 没有「按窗口排除」能力**，岛体一定在画面里。
    // 所以擦除（IslandBackdrop.EraseIsland / FillSoft）必须保留，这一点和 GDI 路径相同。
    //
    // 实现约定（别踩这些坑）：
    //   · 只在 IslandBackdrop 的后台线程调用，全程单线程，不加锁；
    //   · 只走 vtable 直调，不声明完整 COM 接口 —— 那几个接口动辄上百个方法，
    //     声明整串既冗长又容易错位，这里只按下标调真正用到的那几个；
    //   · 任何一步失败都置 _failed 并永久回退 GDI（用户不会因为抓屏挂掉而失去玻璃）；
    //   · AcquireNextFrame 返回 WAIT_TIMEOUT = 桌面没变化，直接返回 false 复用上一帧，
    //     这正是它比 GDI 省的地方，不要当成错误。
    internal static unsafe class DxgiBackdrop
    {
        // ---- DXGI / D3D11 常量 ----
        private const int D3D_DRIVER_TYPE_UNKNOWN = 0;
        private const int D3D_DRIVER_TYPE_HARDWARE = 1;
        private const int D3D_DRIVER_TYPE_WARP = 5;

        private const uint DXGI_FORMAT_B8G8R8A8_UNORM = 87;
        private const uint DXGI_FORMAT_B8G8R8A8_UNORM_SRGB = 91;

        private const uint D3D11_USAGE_STAGING = 3;
        private const uint D3D11_CPU_ACCESS_READ = 0x20000;
        private const uint D3D11_MAP_READ = 1;

        private const int S_OK = 0;
        private static readonly int DXGI_ERROR_WAIT_TIMEOUT = unchecked((int)0x887A0027);
        private static readonly int DXGI_ERROR_ACCESS_LOST = unchecked((int)0x887A0026);
        private static readonly int DXGI_ERROR_NOT_CURRENTLY_AVAILABLE = unchecked((int)0x887A0022);

        private static readonly Guid IID_IDXGIDevice = new("54ec77fa-1377-44e6-8c32-88fd5f44c84c");
        private static readonly Guid IID_IDXGIOutput1 = new("00cddea8-939b-4b83-a340-a685226666cc");
        private static readonly Guid IID_ID3D11Texture2D = new("6f15aaf2-d208-4e89-9ab4-489535d34f9c");

        // ---- 每个输出（显示器）一套复制会话 ----
        private sealed class Output
        {
            public IntPtr Output1;      // IDXGIOutput1*
            public IntPtr Dup;          // IDXGIOutputDuplication*
            public int Left, Top, Right, Bottom;   // 该输出在虚拟桌面上的物理像素矩形
            public bool DuplicateFailed;
            /// <summary>已经等过 DWM 一轮（用来决定首帧要不要用长超时 + 长等待）。</summary>
            public bool Primed;
        }

        private static readonly List<Output> _outputs = new();
        private static IntPtr _device;      // ID3D11Device*
        private static IntPtr _ctx;         // ID3D11DeviceContext*（immediate）
        private static IntPtr _staging;     // ID3D11Texture2D*（STAGING，CPU 可读）
        private static uint _stgW, _stgH, _stgFormat;

        private static bool _inited;
        private static bool _failed;        // 永久失败：已回退 GDI

        // 体检计数（5 秒窗口，由 IslandBackdrop 的 10s 体检行读走）：
        //   Frame = 真的拿到帧 / Timeout = 桌面没变化 / Blank = 拿到但整幅纯黑（初始化空帧）
        private static int _nFrame, _nTimeout, _nBlank;

        /// <summary>体检用：近一窗口内的帧 / 无新帧 / 空白帧次数。</summary>
        public static (int Frame, int Timeout, int Blank) TakeStats()
        {
            var r = (_nFrame, _nTimeout, _nBlank);
            _nFrame = _nTimeout = _nBlank = 0;
            return r;
        }


        /// <summary>抓屏后端是否可用（初始化成功且没有永久失败）。</summary>
        public static bool Available => !_failed;

        /// <summary>
        /// 本次会话里是否真的产出过帧。
        ///
        /// 有些机器（本机 Intel HD 530 实测）能建起 Desktop Duplication，但
        /// <c>AcquireNextFrame</c> 永远 WAIT_TIMEOUT —— 连新会话首帧的 300ms 都等不到内容。
        /// 这时它给不出「桌面变没变」的任何信息，上层若还按"无新帧 = 桌面静止"处理，
        /// 就会一路等到 3 秒超时才回落 GDI，背景刷新直接变成 3 秒一次。
        /// </summary>
        public static bool HasProduced => _everFrame;

        private static volatile bool _everFrame;

        /// <summary>诊断用：当前用的输出矩形与最近一次结果。</summary>
        public static string DebugState
        {
            get
            {
                if (_failed) return "DXGI: 已禁用（回退 GDI）";
                if (!_inited) return "DXGI: 未初始化";
                var o = _outputs.Count > 0 ? _outputs[0] : null;
                return $"DXGI: 输出 {_outputs.Count} 个"
                    + (o == null ? "" : $"，首个=({o.Left},{o.Top})-({o.Right},{o.Bottom})")
                    + $"，staging={_stgW}x{_stgH} fmt={_stgFormat}";
            }
        }

        // ================= vtable 直调 =================

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static IntPtr Slot(IntPtr p, int i) => ((IntPtr*)(*(IntPtr*)p))[i];

        private static int QI(IntPtr p, in Guid iid, out IntPtr result) => Marshal.QueryInterface(p, in iid, out result);

        private static int DxgiAdapter_GetAdapter(IntPtr dxgiDevice, out IntPtr adapter)
        {
            fixed (IntPtr* pa = &adapter)
                return ((delegate* unmanaged<IntPtr, IntPtr*, int>)Slot(dxgiDevice, 7))(dxgiDevice, pa);
        }

        private static int Adapter_EnumOutputs(IntPtr adapter, uint i, out IntPtr output)
        {
            fixed (IntPtr* po = &output)
                return ((delegate* unmanaged<IntPtr, uint, IntPtr*, int>)Slot(adapter, 7))(adapter, i, po);
        }

        private static int Output_GetDesc(IntPtr output, OutputDesc* desc)
            => ((delegate* unmanaged<IntPtr, OutputDesc*, int>)Slot(output, 7))(output, desc);

        private static int Output1_DuplicateOutput(IntPtr output1, IntPtr device, out IntPtr dup)
        {
            fixed (IntPtr* pd = &dup)
                return ((delegate* unmanaged<IntPtr, IntPtr, IntPtr*, int>)Slot(output1, 22))(output1, device, pd);
        }

        private static int Dup_AcquireNextFrame(IntPtr dup, uint timeoutMs, FrameInfo* info, out IntPtr resource)
        {
            fixed (IntPtr* pr = &resource)
            {
                // 句柄为空说明会话已经销毁（ACCESS_LOST 分支会置零）：这里必须挡住，
                // 否则直接解引用空指针取 vtable → 原生访问违例 0xc0000005，托管层 catch 不住。
                if (dup == IntPtr.Zero) { resource = IntPtr.Zero; return DXGI_ERROR_ACCESS_LOST; }
                return ((delegate* unmanaged<IntPtr, uint, FrameInfo*, IntPtr*, int>)Slot(dup, 8))(dup, timeoutMs, info, pr);
            }
        }

        // ⚠️ IDXGIOutputDuplication 的 vtable 顺序（dxgi1_2.h）：
        //    IUnknown 0-2 → IDXGIObject 3-6 → GetDesc 7 → AcquireNextFrame 8 →
        //    GetFrameDirtyRects 9 → GetFrameMoveRects 10 → GetFramePointerShape 11 →
        //    MapDesktopSurface 12 → UnMapDesktopSurface 13 → **ReleaseFrame 14**。
        //    这里原来写 11（= GetFramePointerShape）：参数全错，轻则返回 E_INVALIDARG、
        //    帧一直占着（下一次 Acquire 直接 INVALID_CALL，背板再也不更新），
        //    重则写坏调用方给的"必需大小"指针 —— 原生访问违例 0xc0000005，进程直接消失。
        private static int Dup_ReleaseFrame(IntPtr dup)
            => dup == IntPtr.Zero ? 0 : ((delegate* unmanaged<IntPtr, int>)Slot(dup, 14))(dup);

        private static int Device_CreateTexture2D(IntPtr device, Tex2DDesc* desc, out IntPtr tex)
        {
            fixed (IntPtr* pt = &tex)
                return ((delegate* unmanaged<IntPtr, Tex2DDesc*, IntPtr, IntPtr*, int>)Slot(device, 5))(
                    device, desc, IntPtr.Zero, pt);
        }

        private static void Texture2D_GetDesc(IntPtr tex, Tex2DDesc* desc)
            => ((delegate* unmanaged<IntPtr, Tex2DDesc*, void>)Slot(tex, 10))(tex, desc);

        // ⚠️ box 必须给：不给（nullptr）表示「整幅子资源」，而目标 staging 只有一条高，
        //    尺寸对不上 CopySubresourceRegion 会静默失败 —— 表现就是「抓回来全黑」。
        private static void Ctx_CopySubresourceRegion(IntPtr ctx, IntPtr dst, uint dstSub, uint dx, uint dy, uint dz,
            IntPtr src, uint srcSub, Box* box)
            => ((delegate* unmanaged<IntPtr, IntPtr, uint, uint, uint, uint, IntPtr, uint, Box*, void>)Slot(ctx, 46))(
                ctx, dst, dstSub, dx, dy, dz, src, srcSub, box);

        private static int Ctx_Map(IntPtr ctx, IntPtr res, uint sub, uint mapType, uint flags, MappedSub* mapped)
            => ((delegate* unmanaged<IntPtr, IntPtr, uint, uint, uint, MappedSub*, int>)Slot(ctx, 14))(
                ctx, res, sub, mapType, flags, mapped);

        private static void Ctx_Unmap(IntPtr ctx, IntPtr res, uint sub)
            => ((delegate* unmanaged<IntPtr, IntPtr, uint, void>)Slot(ctx, 15))(ctx, res, sub);

        [DllImport("dxgi.dll", ExactSpelling = true)]
        private static extern int CreateDXGIFactory1(in Guid riid, out IntPtr factory);

        private static readonly Guid IID_IDXGIFactory1 = new("770aae78-f26f-4dba-a829-253c83d1b387");

        [StructLayout(LayoutKind.Sequential)]
        private unsafe struct AdapterDesc
        {
            public fixed byte Description[256];
            public uint VendorId, DeviceId, SubSysId, Revision;
            public UIntPtr DedicatedVideoMemory, DedicatedSystemMemory, SharedSystemMemory;
            public ulong Luid;
        }

        [DllImport("d3d11.dll", ExactSpelling = true)]
        private static extern int D3D11CreateDevice(
            IntPtr adapter, int driverType, IntPtr software, uint flags,
            IntPtr featureLevels, uint numFeatureLevels, uint sdkVersion,
            out IntPtr device, out int featureLevel, out IntPtr context);

        // ================= 结构 =================

        // DXGI_OUTPUT_DESC：DeviceName[32] (64B) + RECT(16B) + BOOL(4B) + ROTATION(4B) + HMONITOR(8B) = 96B
        [StructLayout(LayoutKind.Sequential)]
        private struct OutputDesc
        {
            public ulong Name0, Name1, Name2, Name3, Name4, Name5, Name6, Name7;
            public int Left, Top, Right, Bottom;
            public int Attached;
            public int Rotation;
            public IntPtr Monitor;
        }

        // DXGI_OUTDUPL_FRAME_INFO：只有大小有意义，字段一律不读（用行首三个 long + 填充保证够大）
        [StructLayout(LayoutKind.Sequential)]
        private struct FrameInfo
        {
            public long LastPresentTime;
            public long LastMouseUpdateTime;
            public uint AccumulatedFrames;
            public int RectsCoalesced;
            public int ProtectedContentMaskedOut;
            public int PointerX;
            public int PointerY;
            public int PointerVisible;
            public uint TotalMetadataBufferSize;
            public uint PointerShapeBufferSize;
            public long Pad0;
            public long Pad1;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct Tex2DDesc
        {
            public uint Width, Height, MipLevels, ArraySize;
            public uint Format;
            public uint SampleCount, SampleQuality;
            public uint Usage, BindFlags, CPUAccessFlags, MiscFlags;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct Box
        {
            public uint Left, Top, Front, Right, Bottom, Back;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MappedSub
        {
            public void* pData;
            public uint RowPitch;
            public uint DepthPitch;
        }

        // ================= 初始化 =================

        private static bool EnsureInit()
        {
            if (_inited) return true;
            if (_failed) return false;

            try
            {
                IntPtr device = IntPtr.Zero, ctx = IntPtr.Zero;
                int fl;
                int hr = D3D11CreateDevice(IntPtr.Zero, D3D_DRIVER_TYPE_HARDWARE, IntPtr.Zero, 0,
                    IntPtr.Zero, 0, 7, out device, out fl, out ctx);
                if (hr < 0)
                {
                    // 硬件设备建不起来（独显被占、虚拟机等）→ 退 WARP 软件设备，功能一样、慢一点
                    hr = D3D11CreateDevice(IntPtr.Zero, D3D_DRIVER_TYPE_WARP, IntPtr.Zero, 0,
                        IntPtr.Zero, 0, 7, out device, out fl, out ctx);
                }
                if (hr < 0 || device == IntPtr.Zero || ctx == IntPtr.Zero)
                {
                    Fail($"D3D11CreateDevice 失败 hr=0x{hr:X8}");
                    return false;
                }
                _device = device;
                _ctx = ctx;

                if (QI(device, IID_IDXGIDevice, out IntPtr dxgiDevice) < 0 || dxgiDevice == IntPtr.Zero)
                {
                    Fail("ID3D11Device → IDXGIDevice 失败");
                    return false;
                }

                try
                {
                    if (DxgiAdapter_GetAdapter(dxgiDevice, out IntPtr adapter) < 0 || adapter == IntPtr.Zero)
                    {
                        Fail("IDXGIDevice.GetAdapter 失败");
                        return false;
                    }
                    try
                    {
                        for (uint i = 0; i < 8; i++)
                        {
                            if (Adapter_EnumOutputs(adapter, i, out IntPtr output) < 0 || output == IntPtr.Zero) break;
                            var o = new Output { Output1 = IntPtr.Zero };
                            try
                            {
                                OutputDesc d;
                                if (Output_GetDesc(output, &d) >= 0)
                                {
                                    o.Left = d.Left; o.Top = d.Top; o.Right = d.Right; o.Bottom = d.Bottom;
                                }
                                // 旋转屏直接放弃（复制出来的坐标系是旋转前的，换算容易错）
                                if (d.Rotation != 1 /* DXGI_MODE_ROTATION_IDENTITY */)
                                {
                                    Logger.Info($"[DXGI] 输出 {i} 带旋转（{d.Rotation}），该输出回退 GDI");
                                    o.DuplicateFailed = true;
                                }
                                else if (QI(output, IID_IDXGIOutput1, out IntPtr output1) >= 0
                                         && output1 != IntPtr.Zero)
                                {
                                    o.Output1 = output1;
                                }
                                else
                                {
                                    o.DuplicateFailed = true;
                                }
                            }
                            finally
                            {
                                Marshal.Release(output);
                            }
                            _outputs.Add(o);
                        }
                    }
                    finally
                    {
                        Marshal.Release(adapter);
                    }
                }
                finally
                {
                    Marshal.Release(dxgiDevice);
                }

                if (_outputs.Count == 0)
                {
                    Fail("枚举不到任何输出");
                    return false;
                }

                // 记录机器上的所有 adapter：桌面复制绑在哪块 GPU 上，出问题时这一行最关键
                var names = new List<string>();
                try
                {
                    if (CreateDXGIFactory1(IID_IDXGIFactory1, out IntPtr fac) >= 0 && fac != IntPtr.Zero)
                    {
                        for (uint ai = 0; ai < 8; ai++)
                        {
                            IntPtr ad = IntPtr.Zero;
                            if (((delegate* unmanaged<IntPtr, uint, IntPtr*, int>)Slot(fac, 7))(fac, ai, &ad) < 0
                                || ad == IntPtr.Zero) break;
                            try
                            {
                                AdapterDesc d = default;
                                if (((delegate* unmanaged<IntPtr, AdapterDesc*, int>)Slot(ad, 8))(ad, &d) >= 0)
                                {
                                    string name = "";
                                    for (int k = 0; k < 127; k++)
                                    {
                                        byte ch = d.Description[k * 2];
                                        if (ch == 0) break;
                                        name += (char)ch;
                                    }
                                    names.Add($"{name}(0x{d.VendorId:X4})");
                                }
                            }
                            finally { Marshal.Release(ad); }
                        }
                        Marshal.Release(fac);
                    }
                }
                catch (Exception ex) { Logger.Warn("[DXGI] adapter 枚举异常：" + ex.Message); }

                _inited = true;
                Logger.Info($"[DXGI] adapter: {(names.Count == 0 ? "无" : string.Join(" / ", names))}");
                Logger.Info($"[DXGI] 后端就绪：{_outputs.Count} 个输出，"
                    + string.Join(" ", _outputs.Select(o => $"({o.Left},{o.Top})-({o.Right},{o.Bottom})")));
                return true;
            }
            catch (Exception ex)
            {
                Fail("初始化异常：" + ex.Message);
                return false;
            }
        }

        private static void Fail(string why)
        {
            _failed = true;
            Logger.Warn("[DXGI] " + why + "，抓屏回退 GDI");
            Release();
        }

        /// <summary>彻底释放（进程退出 / 重新初始化前）。</summary>
        public static void Release()
        {
            foreach (var o in _outputs)
            {
                if (o.Dup != IntPtr.Zero) { Marshal.Release(o.Dup); o.Dup = IntPtr.Zero; }
                if (o.Output1 != IntPtr.Zero) { Marshal.Release(o.Output1); o.Output1 = IntPtr.Zero; }
            }
            _outputs.Clear();
            if (_staging != IntPtr.Zero) { Marshal.Release(_staging); _staging = IntPtr.Zero; }
            _stgW = _stgH = 0; _stgFormat = 0;
            if (_ctx != IntPtr.Zero) { Marshal.Release(_ctx); _ctx = IntPtr.Zero; }
            if (_device != IntPtr.Zero) { Marshal.Release(_device); _device = IntPtr.Zero; }
            _inited = false;
        }

        /// <summary>
        /// 用户切换抓屏后端 / 手动重试 DXGI 时调用：释放现有 D3D 与复制会话，并清掉「永久失败」标记，
        /// 下一次 TryGrab 重新建一遍。
        /// ⚠️ 只能在抓屏线程上调用：这里释放的正是 TryGrab 正在用的对象，
        ///    从 UI 线程释放会与进行中的抓取撞上，后果同样是原生访问违例。
        /// </summary>
        public static void Reinitialize()
        {
            Release();
            _failed = false;
            _everFrame = false;
        }

        // ================= 抓取 =================

        /// <summary>抓取结果。NoNewFrame 必须与 Unsupported 区分：前者要沿用上一帧，后者才回退 GDI。</summary>
        public enum GrabResult
        {
            /// <summary>拿到了新画面，已写入 dst。</summary>
            Ok,
            /// <summary>桌面自上次以来没变化 —— 调用方应复用上一帧，不要回退 GDI 白跑一次。</summary>
            NoNewFrame,
            /// <summary>这块区域/这台机器走不了 DXGI，调用方应回退 GDI。</summary>
            Unsupported,
        }

        /// <summary>
        /// 抓取屏幕矩形 (x,y,w,h)（虚拟桌面物理像素），缩放写入 dst（dstW×dstH，BGRA8888，行距 dstPitch）。
        /// 只在 IslandBackdrop 的后台线程调用。
        /// </summary>
        public static GrabResult TryGrab(int x, int y, int w, int h, IntPtr dst, int dstPitch, int dstW, int dstH)
        {
            if (_failed || w < 1 || h < 1) return GrabResult.Unsupported;
            if (!EnsureInit()) return GrabResult.Unsupported;

            // 找包含请求矩形的输出：岛在屏幕顶部中央，正常只会命中一个
            Output? target = null;
            for (int i = 0; i < _outputs.Count; i++)
            {
                var o = _outputs[i];
                if (o.DuplicateFailed) continue;
                if (x >= o.Left && x + w <= o.Right && y >= o.Top && y + h <= o.Bottom) { target = o; break; }
            }
            if (target == null) return GrabResult.Unsupported;   // 跨屏 / 副屏 / 屏幕外：交给 GDI

            // 该输出还没有复制会话 → 建一个（失败就长期跳过这个输出）
            if (target.Dup == IntPtr.Zero)
            {
                if (target.Output1 == IntPtr.Zero)
                {
                    target.DuplicateFailed = true;
                    return GrabResult.Unsupported;
                }
                int hrd = Output1_DuplicateOutput(target.Output1, _device, out IntPtr dup);
                if (hrd < 0 || dup == IntPtr.Zero)
                {
                    target.DuplicateFailed = true;
                    Logger.Warn($"[DXGI] DuplicateOutput 失败 hr=0x{hrd:X8}"
                        + (hrd == DXGI_ERROR_NOT_CURRENTLY_AVAILABLE ? "（该输出不支持复制）" : "")
                        + "，该输出回退 GDI");
                    return GrabResult.Unsupported;
                }
                target.Dup = dup;
                // 新建会话后先让 DWM 跑完一轮：实测紧接着 Acquire 拿到的第一帧经常是空的
                // （AccumulatedFrames=0 / LastPresentTime=0），等一会儿再要成功率明显更高。
                // ⚠️ 只做一次。放在每次抓取里会把抓屏线程拖慢 180ms，背板刷新周期直接翻倍。
                Thread.Sleep(180);
                // ⚠️ 这里**不能**把 Primed 置 true：置了之后下面的 Acquire 就用 timeout=0（纯非阻塞），
                //    而新会话第一次往往还没有可复制的帧 → 立刻 WAIT_TIMEOUT → 上层判定「桌面静止」，
                //    其实一帧都没抓到过。这时又因为「DXGI 从没成功过」，每轮都回落 GDI 全屏回读。
                //    改成第一次用 300ms 真正等一帧，拿到之后再切成非阻塞轮询。
            }

            // ⚠️ 会话句柄先快照到局部：下面 AcquireNextFrame 与 ReleaseFrame 必须落在**同一个**
            //    对象上。直接读字段 target.Dup 有两个坑：ACCESS_LOST 分支会把字段置零；
            //    万一有第二条抓屏线程并发改它，finally 就会拿零指针去取 vtable ——
            //    原生访问违例 0xc0000005，托管层 catch 不住，进程直接消失。
            IntPtr session = target.Dup;
            if (session == IntPtr.Zero) return GrabResult.Unsupported;

            // 双保险：即便等过一轮，拿到的仍可能是空帧（整幅纯黑 / alpha=0）。
            // 黑帧先落进 dst 兜底，再要一帧覆盖它；第二帧还是黑就认了
            // （用户桌面本来就是纯黑时不该无限重试）。
            bool wrote = false;
            for (int attempt = 0; attempt < 2; attempt++)
            {
                FrameInfo info = default;
                // ⚠️ 超时必须极短：AcquireNextFrame 是**阻塞**的，桌面静止时会一直等到超时。
                //    之前写 300ms，静止桌面下抓屏线程每轮卡 300ms，加上 250ms 节拍，
                //    背板刷新周期变成 550ms —— 观感就是「帧率很低、卡卡的」。
                //    只有建会话后的第一帧需要等得久一点（DWM 还没合成到可复制状态）。
                // primed 之后用 0（纯非阻塞轮询）：DXGI 会把「自上次 ReleaseFrame 以来的最新帧」
                // 留着，所以只要有变化就一定拿得到，不需要靠超时去等。
                uint timeout = target.Primed ? 0u : 300u;
                int hr = Dup_AcquireNextFrame(session, timeout, &info, out IntPtr res);
                if (hr == DXGI_ERROR_ACCESS_LOST)
                {
                    // 显示器切换 / 全屏游戏 / UAC 提权：销毁会话，下次调用重建。
                    // 只在字段仍指向本次句柄时才置零 —— 否则会把别人刚建好的新会话抹掉。
                    if (target.Dup == session) target.Dup = IntPtr.Zero;
                    Marshal.Release(session);
                    return wrote ? GrabResult.Ok : GrabResult.NoNewFrame;
                }
                if (hr == DXGI_ERROR_WAIT_TIMEOUT)
                {
                    _nTimeout++;
                    break;                                   // 桌面没变化：用手上已有的
                }
                if (hr < 0 || res == IntPtr.Zero) break;

                // 真的拿到一帧了 → 之后的轮询可以走非阻塞（timeout=0）
                target.Primed = true;

                bool good = false;
                try
                {
                    if (QI(res, IID_ID3D11Texture2D, out IntPtr tex) < 0 || tex == IntPtr.Zero)
                        return wrote ? GrabResult.Ok : GrabResult.Unsupported;
                    try
                    {
                        Tex2DDesc sd = default;
                        Texture2D_GetDesc(tex, &sd);
                        if (sd.Format != DXGI_FORMAT_B8G8R8A8_UNORM && sd.Format != DXGI_FORMAT_B8G8R8A8_UNORM_SRGB)
                            return wrote ? GrabResult.Ok : GrabResult.Unsupported;   // HDR/10bit → GDI

                        // 源矩形（输出内坐标）夹一下，越界就只拷能拷到的部分
                        int sx = x - target.Left, sy = y - target.Top;
                        int cw2 = w, ch2 = h;
                        if (sx < 0) { cw2 += sx; sx = 0; }
                        if (sy < 0) { ch2 += sy; sy = 0; }
                        if (sx + cw2 > (int)sd.Width) cw2 = (int)sd.Width - sx;
                        if (sy + ch2 > (int)sd.Height) ch2 = (int)sd.Height - sy;
                        if (cw2 <= 0 || ch2 <= 0) return wrote ? GrabResult.Ok : GrabResult.Unsupported;

                        // ⚠️ 只建「需要的那一条」的 staging、只拷这一条。
                        //    整幅 1920×1080 的话每次要搬 8MB + Map 8MB，
                        //    而玻璃其实只用到屏幕上方二百来行 —— 白白多花十倍的内存带宽，
                        //    抓屏线程占着 CPU，渲染帧率就被拖下来了。
                        if (_staging == IntPtr.Zero || _stgW != (uint)cw2 || _stgH != (uint)ch2 || _stgFormat != sd.Format)
                        {
                            if (_staging != IntPtr.Zero) { Marshal.Release(_staging); _staging = IntPtr.Zero; }
                            var desc = new Tex2DDesc
                            {
                                Width = (uint)cw2,
                                Height = (uint)ch2,
                                MipLevels = 1,
                                ArraySize = 1,
                                Format = sd.Format,
                                SampleCount = 1,
                                SampleQuality = 0,
                                Usage = D3D11_USAGE_STAGING,
                                BindFlags = 0,
                                CPUAccessFlags = D3D11_CPU_ACCESS_READ,
                                MiscFlags = 0,
                            };
                            if (Device_CreateTexture2D(_device, &desc, out IntPtr stg) < 0 || stg == IntPtr.Zero)
                                return wrote ? GrabResult.Ok : GrabResult.Unsupported;
                            _staging = stg;
                            _stgW = (uint)cw2; _stgH = (uint)ch2; _stgFormat = sd.Format;
                        }

                        var box = new Box
                        {
                            Left = (uint)sx, Top = (uint)sy, Front = 0,
                            Right = (uint)(sx + cw2), Bottom = (uint)(sy + ch2), Back = 1,
                        };
                        Ctx_CopySubresourceRegion(_ctx, _staging, 0, 0, 0, 0, tex, 0, &box);

                        MappedSub mapped = default;
                        if (Ctx_Map(_ctx, _staging, 0, D3D11_MAP_READ, 0, &mapped) < 0 || mapped.pData == null)
                            return wrote ? GrabResult.Ok : GrabResult.Unsupported;
                        try
                        {
                            good = !LooksBlank((byte*)mapped.pData, (int)mapped.RowPitch, cw2, ch2);
                            if (!good) _nBlank++;
                            if (good || !wrote)
                            {
                                // staging 里就是这条矩形本身，源区域从 (0,0) 起
                                Blit((byte*)mapped.pData, (int)mapped.RowPitch, cw2, ch2,
                                    0, 0, cw2, ch2,
                                    (byte*)dst, dstPitch, dstW, dstH);
                                wrote = true;
                            }
                        }
                        finally
                        {
                            Ctx_Unmap(_ctx, _staging, 0);
                        }
                    }
                    finally
                    {
                        Marshal.Release(tex);
                    }
                }
                finally
                {
                    Dup_ReleaseFrame(session);
                    Marshal.Release(res);
                }

                if (good) break;   // 拿到有效画面，收工
            }

            if (wrote) { _nFrame++; _everFrame = true; }
            return wrote ? GrabResult.Ok : GrabResult.NoNewFrame;
        }

        /// <summary>
        /// 判断这一帧是不是「空白帧」（桌面复制初始化时的那种整幅纯黑）。
        /// 只抽 8×8 个点：真实桌面上不可能 64 个采样点全是 (0,0,0)。
        /// </summary>
        private static bool LooksBlank(byte* data, int pitch, int w, int h)
        {
            if (w < 8 || h < 8) return false;
            for (int j = 0; j < 8; j++)
            {
                byte* row = data + (j * (h - 1) / 7) * pitch;
                for (int i = 0; i < 8; i++)
                {
                    byte* q = row + (i * (w - 1) / 7) * 4;
                    if (q[0] != 0 || q[1] != 0 || q[2] != 0) return false;
                }
            }
            return true;
        }

        /// <summary>
        /// 把复制帧里的 (sx,sy,w,h) 搬到 dst。常见情形是 1:1（抓屏统一原分辨率），走逐行 memcpy；
        /// 需要缩小时用最近邻（亚克力档会降到半分辨率，宁可快一点，模糊交给渲染层）。
        /// 请求矩形越过输出边界时按边缘像素延展，不留黑边（岛贴屏幕顶部时会发生）。
        /// </summary>
        private static void Blit(byte* src, int srcPitch, int srcW, int srcH,
                                 int sx, int sy, int w, int h,
                                 byte* dst, int dstPitch, int dw, int dh)
        {
            if (w == dw && h == dh)
            {
                for (int j = 0; j < dh; j++)
                {
                    int ys = sy + j;
                    if (ys < 0) ys = 0; else if (ys >= srcH) ys = srcH - 1;
                    byte* srow = src + ys * srcPitch;
                    byte* drow = dst + j * dstPitch;
                    int xs = sx;
                    if (xs >= 0 && xs + w <= srcW)
                    {
                        Buffer.MemoryCopy(srow + xs * 4, drow, dw * 4, w * 4);
                    }
                    else
                    {
                        for (int i = 0; i < dw; i++)
                        {
                            int xi = sx + i;
                            if (xi < 0) xi = 0; else if (xi >= srcW) xi = srcW - 1;
                            *(int*)(drow + i * 4) = *(int*)(srow + xi * 4);
                        }
                    }
                }
                return;
            }

            // 缩放（玻璃质量档 1/2 降采样时走这里）：双线性。
            // 用最近邻会把壁纸里的斜线、窗口标题字降成锯齿轮廓，而这条路径本来就是降采样，
            // 多几次乘法换干净的轮廓很值。
            float rx = (float)w / dw, ry = (float)h / dh;
            for (int j = 0; j < dh; j++)
            {
                float fy = sy + (j + 0.5f) * ry - 0.5f;
                int y0 = (int)MathF.Floor(fy);
                float ty = fy - y0;
                int y1 = y0 + 1;
                if (y0 < 0) y0 = 0; else if (y0 >= srcH) y0 = srcH - 1;
                if (y1 < 0) y1 = 0; else if (y1 >= srcH) y1 = srcH - 1;
                byte* r0 = src + y0 * srcPitch;
                byte* r1 = src + y1 * srcPitch;
                byte* drow = dst + j * dstPitch;
                for (int i = 0; i < dw; i++)
                {
                    float fx = sx + (i + 0.5f) * rx - 0.5f;
                    int x0 = (int)MathF.Floor(fx);
                    float tx = fx - x0;
                    int x1 = x0 + 1;
                    if (x0 < 0) x0 = 0; else if (x0 >= srcW) x0 = srcW - 1;
                    if (x1 < 0) x1 = 0; else if (x1 >= srcW) x1 = srcW - 1;
                    for (int c = 0; c < 4; c++)
                    {
                        float v = (r0[x0 * 4 + c] * (1f - tx) + r0[x1 * 4 + c] * tx) * (1f - ty)
                                + (r1[x0 * 4 + c] * (1f - tx) + r1[x1 * 4 + c] * tx) * ty;
                        drow[i * 4 + c] = (byte)(v + 0.5f);
                    }
                }
            }
        }
    }
}
