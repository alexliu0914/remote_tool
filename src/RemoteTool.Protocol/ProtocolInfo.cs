namespace RemoteTool.Protocol;

/// <summary>協定常數。</summary>
public static class ProtocolInfo
{
    /// <summary>目前協定版本。Hello 交換時雙方必須相同，否則拒絕連線。</summary>
    public const int Version = 1;

    /// <summary>三條 TLS 連線共用的預設埠。</summary>
    public const int DefaultPort = 52100;

    /// <summary>子連線 token 長度（位元組）。</summary>
    public const int SessionTokenSize = 32;

    /// <summary>帳號長度上限（字元）。</summary>
    public const int MaxUsernameLength = 256;

    /// <summary>密碼長度上限（字元）。</summary>
    public const int MaxPasswordLength = 1024;
}
