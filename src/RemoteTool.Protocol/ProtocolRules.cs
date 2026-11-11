namespace RemoteTool.Protocol;

/// <summary>連線的哪一端。</summary>
public enum Endpoint
{
    Controller,
    Host,
}

/// <summary>
/// Protocol V1 規則：每種訊息允許出現的連線角色與方向。
/// 違反即為協定錯誤（回 Error 後關閉）。Hello 與 Error 在角色確定前即可出現。
/// </summary>
public static class ProtocolRules
{
    private const byte C2H = 1, H2C = 2, Both = C2H | H2C;

    private static readonly Dictionary<MessageType, (ChannelKind[] Roles, byte Direction)> Table = new()
    {
        [MessageType.Hello] = (All, Both),
        [MessageType.Error] = (All, Both),

        [MessageType.LoginRequest] = ([ChannelKind.Control], C2H),
        [MessageType.LoginResult] = ([ChannelKind.Control], H2C),
        [MessageType.SessionTokenGrant] = ([ChannelKind.Control], H2C),
        [MessageType.SessionTokenRequest] = ([ChannelKind.Control], C2H),
        [MessageType.Heartbeat] = ([ChannelKind.Control], Both),
        [MessageType.SessionStateChanged] = ([ChannelKind.Control], H2C),
        [MessageType.PauseControl] = ([ChannelKind.Control], H2C),
        [MessageType.ResumeControl] = ([ChannelKind.Control], Both),
        [MessageType.KeyEvent] = ([ChannelKind.Control], C2H),
        [MessageType.MouseMoveEvent] = ([ChannelKind.Control], C2H),
        [MessageType.MouseButtonEvent] = ([ChannelKind.Control], C2H),
        [MessageType.MouseWheelEvent] = ([ChannelKind.Control], C2H),
        [MessageType.InputHeartbeat] = ([ChannelKind.Control], C2H),
        [MessageType.ReleaseAllInput] = ([ChannelKind.Control], C2H),
        [MessageType.ClipboardText] = ([ChannelKind.Control], Both),

        [MessageType.SubConnectChallenge] = (Sub, H2C),
        [MessageType.SubConnectAuth] = (Sub, C2H),
        [MessageType.SubConnectResult] = (Sub, H2C),

        [MessageType.VideoFrame] = ([ChannelKind.Video], H2C),
        [MessageType.CursorShape] = ([ChannelKind.Video], H2C),
        [MessageType.KeyframeRequest] = ([ChannelKind.Video], C2H),
        [MessageType.VideoTileUpdate] = ([ChannelKind.Video], H2C),

        [MessageType.FileListRequest] = ([ChannelKind.File], Both),
        [MessageType.FileListReply] = ([ChannelKind.File], Both),
        [MessageType.FileChunk] = ([ChannelKind.File], Both),
        [MessageType.FileChunkAck] = ([ChannelKind.File], Both),
        [MessageType.FileTransferCancel] = ([ChannelKind.File], Both),
        [MessageType.FileTransferResult] = ([ChannelKind.File], Both),
    };

    private static ChannelKind[] All => [ChannelKind.Control, ChannelKind.Video, ChannelKind.File];
    private static ChannelKind[] Sub => [ChannelKind.Video, ChannelKind.File];

    /// <summary>此值是否為 V1 定義的訊息類型。</summary>
    public static bool IsKnown(MessageType type) => Table.ContainsKey(type);

    /// <summary>
    /// <paramref name="sender"/> 能否在角色為 <paramref name="role"/> 的連線上送出此類型。
    /// <paramref name="role"/> 為 null 表示尚未完成 Hello，此時只允許 Hello 與 Error。
    /// </summary>
    public static bool IsAllowed(MessageType type, ChannelKind? role, Endpoint sender)
    {
        if (!Table.TryGetValue(type, out var rule)) return false;
        if (role is null) return type is MessageType.Hello or MessageType.Error;
        byte dir = sender == Endpoint.Controller ? C2H : H2C;
        return (rule.Direction & dir) != 0 && Array.IndexOf(rule.Roles, role.Value) >= 0;
    }
}
