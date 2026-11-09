namespace RemoteTool.Protocol;

/// <summary>
/// 協定常數。標為「初始值」者依規劃書為 Spike 後可調整的數值。
/// </summary>
public static class ProtocolConstants
{
    /// <summary>協定版本。握手時不相容就明確提示更新。</summary>
    public const int ProtocolVersion = 1;

    /// <summary>被控端監聽埠（三條連線共用）。</summary>
    public const int DefaultPort = 52100;

    /// <summary>連線心跳間隔（第七節）。</summary>
    public static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(2);

    /// <summary>心跳逾時：6 秒無回應即斷線。</summary>
    public static readonly TimeSpan HeartbeatTimeout = TimeSpan.FromSeconds(6);

    /// <summary>有按鍵按住時，控制端送輸入心跳的間隔（初始值）。</summary>
    public static readonly TimeSpan InputHeartbeatInterval = TimeSpan.FromMilliseconds(250);

    /// <summary>被控端超過此時間沒收到輸入心跳就全部釋放（初始值）。</summary>
    public static readonly TimeSpan InputHeartbeatTimeout = TimeSpan.FromSeconds(1);

    /// <summary>Session Token 發出後須在此時間內用於開子連線。</summary>
    public static readonly TimeSpan SessionTokenLifetime = TimeSpan.FromSeconds(30);

    /// <summary>恢復前三項判定須連續成立的時間（初始值）。</summary>
    public static readonly TimeSpan ResumeStableDuration = TimeSpan.FromSeconds(1);

    /// <summary>握手（TLS + Hello）逾時。</summary>
    public static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(10);

    /// <summary>Video 連線單一封包上限（一張編碼後的畫面）。超過時由上層分塊（M1）。</summary>
    public const int MaxVideoPayloadSize = 4 * 1024 * 1024;

    /// <summary>Session Token 位元組數（256 位元）。</summary>
    public const int SessionTokenSize = 32;

    /// <summary>子連線 nonce 位元組數。</summary>
    public const int NonceSize = 32;

    /// <summary>帳號名稱長度上限。</summary>
    public const int MaxUsernameLength = 64;

    /// <summary>密碼長度上限（防止以超長密碼耗用 Argon2 資源）。</summary>
    public const int MaxPasswordLength = 256;

    /// <summary>密碼長度下限（第六節）。</summary>
    public const int MinPasswordLength = 10;

    /// <summary>單次「貼上 Unicode 文字」的字元上限。</summary>
    public const int MaxUnicodeTextLength = 4096;

    /// <summary>文字剪貼簿單次上限（1 MB）。</summary>
    public const int MaxClipboardBytes = 1024 * 1024;
}
