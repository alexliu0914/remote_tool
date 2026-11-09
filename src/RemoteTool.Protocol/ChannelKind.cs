namespace RemoteTool.Protocol;

/// <summary>
/// 邏輯通道。三條 TLS 連線各綁一種角色（見規劃書第七節）。
/// Control 中斷會撤銷整個工作階段；Video / File 中斷各自獨立重連。
/// </summary>
public enum ChannelKind : byte
{
    /// <summary>版本握手、登入、心跳、鍵鼠、文字剪貼簿、螢幕清單、品質調整。</summary>
    Control = 1,

    /// <summary>H.264 影像與游標。</summary>
    Video = 2,

    /// <summary>雙向檔案傳輸（限速）。</summary>
    File = 3,
}
