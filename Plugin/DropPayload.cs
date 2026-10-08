using System.Text;
using ComTypes = System.Runtime.InteropServices.ComTypes;

namespace NotchPeninsula.Plugins;

internal static class DropPayload
{
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

internal static class DragOutState
{
    private static int _depth;
    private static IntPtr _sourceHwnd;

    public static bool IsDragging => Volatile.Read(ref _depth) > 0;

    public static void Enter(IntPtr sourceHwnd)
    {
        _sourceHwnd = sourceHwnd;
        Interlocked.Increment(ref _depth);
    }

    public static void Exit()
    {
        if (Interlocked.Decrement(ref _depth) <= 0) _sourceHwnd = IntPtr.Zero;
    }

    public static bool IsSelfDrop(IntPtr targetHwnd)
        => IsDragging && _sourceHwnd == targetHwnd;
}
