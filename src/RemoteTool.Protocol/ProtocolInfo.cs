namespace RemoteTool.Protocol;

/// <summary>Protocol V1 常數。完整規格見 docs/PROTOCOL_V1.md。</summary>
public static class ProtocolInfo
{
    /// <summary>協定版本。Hello 交換時雙方必須相同，否則回 Error(VersionMismatch) 並關閉。</summary>
    public const int Version = 1;

    /// <summary>三條 TLS 連線共用的預設埠。</summary>
    public const int DefaultPort = 52100;

    /// <summary>子連線 token 長度（位元組，256 位元）。</summary>
    public const int SessionTokenSize = 32;

    /// <summary>子連線 token 有效期限（PLAN §7：發出後 30 秒內須使用）。</summary>
    public const int SessionTokenLifetimeSeconds = 30;

    /// <summary>子連線挑戰值（nonce）長度。</summary>
    public const int SubConnectNonceSize = 32;

    /// <summary>子連線證明（HMAC-SHA256）長度。</summary>
    public const int SubConnectMacSize = 32;

    /// <summary>帳號長度上限（字元）。</summary>
    public const int MaxUsernameLength = 256;

    /// <summary>密碼長度上限（字元）。</summary>
    public const int MaxPasswordLength = 1024;

    /// <summary>Hello 的 Capabilities 項數與單項長度上限。</summary>
    public const int MaxCapabilities = 32;
    public const int MaxCapabilityLength = 64;

    /// <summary>心跳間隔與逾時（PLAN §7：每 2 秒一次，6 秒無回應即斷線）。</summary>
    public const int HeartbeatIntervalSeconds = 2;
    public const int HeartbeatTimeoutSeconds = 6;
}
