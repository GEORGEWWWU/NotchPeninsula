using System.Text;
using ComTypes = System.Runtime.InteropServices.ComTypes;

namespace NotchPeninsula.Plugins;

/// <summary>
/// 从 OLE 的 IDataObject 里取出「用户拖进来的文件」。
///
/// 拖进来的文件在 OLE 里是 <c>CF_HDROP</c> 格式（<c>TYMED_HGLOBAL</c> 的 STGMEDIUM），
/// 它的 <c>unionmember</c> 就是一个 HDROP 句柄 —— 和 <c>WM_DROPFILES</c> 的 <c>wParam</c> 完全同源，
/// 所以两条拖入路径（OLE 的 IDropTarget 与传统的 WM_DROPFILES）可以共用同一套读取逻辑。
///
/// 插件窗口（<see cref="PluginWindow"/>）与灵动岛本体（IslandDropTarget）都走这里，
/// 保证「什么算一次合法拖入」的判断处处一致。
/// </summary>
internal static class DropPayload
{
    /// <summary>
    /// 取出拖入的全部路径。不是文件拖入（网页文字、位图流等）时返回空列表。
    ///
    /// 只认文件系统上的真实路径：邮件附件、压缩包内条目这类「虚拟文件」在这里拿不到，
    /// 会退化成空的 —— 调用方据此拒绝这次拖放即可。
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
            // 先问一句「有没有这个格式」，没有就立刻收手 —— 不用真把数据搬出来
            if (dataObj.QueryGetData(ref format) != 0) return new List<string>();

            dataObj.GetData(ref format, out ComTypes.STGMEDIUM medium);
            try
            {
                return ReadDropPaths(medium.unionmember);
            }
            finally
            {
                // 这份内存由 STGMEDIUM 持有，漏了就是泄漏
                Win32.ReleaseStgMedium(ref medium);
            }
        }
        catch (Exception ex)
        {
            Logger.Error("[DropPayload] 解析拖入内容失败", ex);
            return new List<string>();
        }
    }

    /// <summary>从 HDROP 句柄里读出全部路径。Windows 那句「先问长度再取内容」的两段式照抄即可。</summary>
    public static List<string> ReadDropPaths(IntPtr hDrop)
    {
        var files = new List<string>();
        if (hDrop == IntPtr.Zero) return files;

        try
        {
            uint count = Win32.DragQueryFile(hDrop, 0xFFFFFFFFu, null, 0); // 0xFFFFFFFF = 问条目数量
            for (uint i = 0; i < count; i++)
            {
                uint len = Win32.DragQueryFile(hDrop, i, null, 0);          // 先问长度（不含结尾 '\0'）
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
/// 记录「当前是否正由宿主自己发起一次拖出」，用来拒绝<b>拖到自己身上</b>。
///
/// <para>
/// 为什么需要它：拖出时鼠标就按在发起者（岛体或某个插件窗口）上，OLE 拿光标底下的窗口去问
/// 「你收不收」时，第一个问到的往往就是发起者自己。不拒绝的话，用户从列表里往外一拖、
/// 手一抖原地松开，文件就被「拖回自己这里」又加了一遍，看着像莫名其妙复制了一份。
/// </para>
///
/// <para>
/// 用 HWND 比对而不是一刀切禁止：插件窗口之间互相拖是<b>合法且有用</b>的
/// （把一个窗口里的条目拖到另一个窗口），只有「自己拖给自己」才必须挡掉。
/// </para>
/// </summary>
internal static class DragOutState
{
    private static int _depth;
    private static IntPtr _sourceHwnd;

    /// <summary>当前是否有拖出正在进行。</summary>
    public static bool IsDragging => Volatile.Read(ref _depth) > 0;

    /// <summary>拖放开始前调用，记下发起方的窗口句柄。必须与 <see cref="Exit"/> 配对（放 finally 里）。</summary>
    public static void Enter(IntPtr sourceHwnd)
    {
        _sourceHwnd = sourceHwnd;
        Interlocked.Increment(ref _depth);
    }

    public static void Exit()
    {
        if (Interlocked.Decrement(ref _depth) <= 0) _sourceHwnd = IntPtr.Zero;
    }

    /// <summary>这次落在 targetHwnd 上的拖入，是不是发起者自己。是的话目标应当拒绝。</summary>
    public static bool IsSelfDrop(IntPtr targetHwnd)
        => IsDragging && _sourceHwnd == targetHwnd;
}
