using RemoteTool.Protocol;

namespace RemoteTool.Connection;

/// <summary>主機指紋與上次記錄的不同：可能是中間人攻擊，或被控端重新產生了憑證。</summary>
public sealed class FingerprintMismatchException(string hostKey, string expected, string actual)
    : Exception($"主機 {hostKey} 的指紋與上次記錄不同，已中止連線（未送出帳密）。")
{
    public string HostKey { get; } = hostKey;
    public string Expected { get; } = expected;
    public string Actual { get; } = actual;
}

/// <summary>首次連線時使用者拒絕信任此指紋。</summary>
public sealed class FingerprintRejectedException(string hostKey, string fingerprint)
    : Exception($"未信任主機 {hostKey} 的指紋，已中止連線（未送出帳密）。")
{
    public string HostKey { get; } = hostKey;
    public string Fingerprint { get; } = fingerprint;
}

/// <summary>登入或子連線驗證被拒。</summary>
public sealed class AuthenticationFailedException(AuthError error, int retryAfterSeconds = 0)
    : Exception(Describe(error, retryAfterSeconds))
{
    public AuthError Error { get; } = error;
    public int RetryAfterSeconds { get; } = retryAfterSeconds;

    public static string Describe(AuthError error, int retryAfterSeconds = 0) => error switch
    {
        AuthError.BadCredentials => "帳號或密碼錯誤。",
        AuthError.TooManyAttempts => retryAfterSeconds > 0
            ? $"嘗試太頻繁，請 {retryAfterSeconds} 秒後再試。"
            : "嘗試太頻繁，請稍後再試。",
        AuthError.Busy => "被控端已有其他控制端連線中。",
        AuthError.InvalidToken => "子連線 token 無效。",
        _ => $"驗證失敗（{error}）。",
    };
}

/// <summary>對方送來 Error 封包或違反協定。</summary>
public sealed class ProtocolViolationException(string message) : Exception(message);
