using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;

namespace RemoteTool.Controller;

/// <summary>
/// 接收從本地檔案總管拖入畫面區的檔案與資料夾（CF_HDROP）。G0-04 驗證項目；實際傳輸於 M3 實作。
/// </summary>
[ComVisible(true)]
internal sealed class FileDropTarget(IntPtr hwnd, Action<IReadOnlyList<string>, NativeMethods.POINT> onDrop) : IDropTarget
{
    private const short CF_HDROP = 15;
    private const int DROPEFFECT_NONE = 0, DROPEFFECT_COPY = 1;
    private bool _acceptable;

    public int DragEnter(IDataObject data, int keyState, NativeMethods.POINT pt, ref int effect)
    {
        _acceptable = HasFiles(data);
        effect = _acceptable ? DROPEFFECT_COPY : DROPEFFECT_NONE;
        return 0;
    }

    public int DragOver(int keyState, NativeMethods.POINT pt, ref int effect)
    {
        effect = _acceptable ? DROPEFFECT_COPY : DROPEFFECT_NONE;
        return 0;
    }

    public int DragLeave()
    {
        _acceptable = false;
        return 0;
    }

    public int Drop(IDataObject data, int keyState, NativeMethods.POINT pt, ref int effect)
    {
        var files = ReadFiles(data);
        effect = files.Count > 0 ? DROPEFFECT_COPY : DROPEFFECT_NONE;
        if (files.Count > 0)
        {
            var client = pt;
            NativeMethods.ScreenToClient(hwnd, ref client);
            onDrop(files, client);
        }
        return 0;
    }

    private static FORMATETC HDropFormat() => new()
    {
        cfFormat = CF_HDROP, dwAspect = DVASPECT.DVASPECT_CONTENT, lindex = -1, tymed = TYMED.TYMED_HGLOBAL,
    };

    private static bool HasFiles(IDataObject data)
    {
        var format = HDropFormat();
        try { return data.QueryGetData(ref format) == 0; }
        catch (COMException) { return false; }
    }

    private static List<string> ReadFiles(IDataObject data)
    {
        var result = new List<string>();
        var format = HDropFormat();
        STGMEDIUM medium;
        try { data.GetData(ref format, out medium); }
        catch (COMException) { return result; }
        try
        {
            uint count = NativeMethods.DragQueryFile(medium.unionmember, 0xFFFFFFFF, null, 0);
            for (uint i = 0; i < count; i++)
            {
                uint len = NativeMethods.DragQueryFile(medium.unionmember, i, null, 0);
                var sb = new StringBuilder((int)len + 1);
                NativeMethods.DragQueryFile(medium.unionmember, i, sb, len + 1);
                result.Add(sb.ToString());
            }
        }
        finally
        {
            NativeMethods.ReleaseStgMedium(ref medium);
        }
        return result;
    }
}
