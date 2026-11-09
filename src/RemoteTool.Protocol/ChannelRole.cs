namespace RemoteTool.Protocol;

/// <summary>
/// 連線角色。同一埠上三條 TLS 連線各綁一種角色（第七節）。
/// Control 中斷撤銷整個工作階段；Video、File 中斷各自重連。
/// </summary>
public enum ChannelRole : byte
{
    /// <summary>握手、登入、心跳、鍵鼠、文字剪貼簿、工作階段狀態。</summary>
    Control = 1,

    /// <summary>H.264 影像與游標。</summary>
    Video = 2,

    /// <summary>雙向檔案傳輸（限速）。</summary>
    File = 3,
}
