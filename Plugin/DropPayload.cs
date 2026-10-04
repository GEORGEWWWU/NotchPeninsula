using System.Text;
using ComTypes = System.Runtime.InteropServices.ComTypes;

namespace NotchPeninsula.Plugins;

/// <summary>
/// 从 OLE 的 IDataObject 取出用户拖进来的文件路径。
/// 拖入文件在 OLE 里是 CF_HDROP（TYMED_HGLOBAL），其 unionmember 就是 HDROP 句柄，
/// 与 WM_DROPFILES 的 wParam 同源，因此 OLE 与 WM_DROPFILES 两条路径共用这里的读取逻辑。
/// </summary>
internal static class DropPayload
{
    /// <summary>
    /// 取出拖入的全部路径；非文件拖入（网页文字、位图等）返回空列表。
    /// 只认文件系统真实路径：邮件附件、压缩包内条目等虚拟文件取不到，返回空由调用方拒绝。
    /// </summary>
    public static List<string> ReadFileDrop(ComTypes.IDataObject? dataObj)
    {
        if (dataObj == null) return new List<string>();

        var format = new ComTypes.FORMATETC
        {
            cfFormat = (short)Win32.CF_HDROP,
            ptd = IntPtr.Zero,
            dwAspect = ComTypes.DVASPECT.DVASPECT_CONTENT,
            lindex = -1,
            tymed = ComTypes.TYMED.TYMED_HGLOBAL,
        };

        try
        {
            // 先查格式是否存在，避免把数据整体搬出来
            if (dataObj.QueryGetData(ref format) != 0) return new List<string>();

            dataObj.GetData(ref format, out ComTypes.STGMEDIUM medium);
            try
            {
                return ReadDropPaths(medium.unionmember);
            }
            finally
            {
                // 这块内存归 STGMEDIUM 所有，不释放即泄漏
                Win32.ReleaseStgMedium(ref medium);
            }
        }
        catch (Exception ex)
        {
            Logger.Error("[DropPayload] 解析拖入内容失败", ex);
            return new List<string>();
        }
    }

    /// <summary>从 HDROP 读出全部路径（先问数量，再逐个先问长度后取内容）。</summary>
    public static List<string> ReadDropPaths(IntPtr hDrop)
    {
        var files = new List<string>();
        if (hDrop == IntPtr.Zero) return files;

        try
        {
            uint count = Win32.DragQueryFile(hDrop, 0xFFFFFFFFu, null, 0); // 0xFFFFFFFF = 查条目数
            for (uint i = 0; i < count; i++)
            {
                uint len = Win32.DragQueryFile(hDrop, i, null, 0);          // 长度不含结尾 '\0'
                if (len == 0) continue;

                var buffer = new StringBuilder((int)len + 1);
                if (Win32.DragQueryFile(hDrop, i, buffer, (uint)buffer.Capacity) > 0)
                    files.Add(buffer.ToString());
            }
        }
        catch (Exception ex)
        {
            Logger.Error("[DropPayload] 读取拖入的文件列表失败", ex);
        }

        return files;
    }
}

/// <summary>
/// 记录「是否正由宿主自己发起拖出」，用于拒绝拖到自己身上。
/// 拖出时光标下第一个被 OLE 问到的窗口往往就是发起者，不挡的话用户原地松手
/// 会被当作一次新拖入，看起来像莫名多复制了一份。
/// 按 HWND 比对而非一律禁止：插件窗口之间互拖是合法的，只有自己拖给自己才拒绝。
/// </summary>
internal static class DragOutState
{
    private static int _depth;
    private static IntPtr _sourceHwnd;

    /// <summary>当前是否有拖出正在进行。</summary>
    public static bool IsDragging => Volatile.Read(ref _depth) > 0;

    /// <summary>拖出开始前调用，记下发起方窗口句柄；必须与 Exit 成对（放 finally）。</summary>
    public static void Enter(IntPtr sourceHwnd)
    {
        _sourceHwnd = sourceHwnd;
        Interlocked.Increment(ref _depth);
    }

    public static void Exit()
    {
        if (Interlocked.Decrement(ref _depth) <= 0) _sourceHwnd = IntPtr.Zero;
    }

    /// <summary>落在 targetHwnd 上的这次拖入是否由发起者自己产生；是则目标应拒绝。</summary>
    public static bool IsSelfDrop(IntPtr targetHwnd)
        => IsDragging && _sourceHwnd == targetHwnd;
}
