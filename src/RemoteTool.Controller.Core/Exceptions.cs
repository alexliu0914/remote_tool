using RemoteTool.Protocol.Messages;

namespace RemoteTool.Controller.Core;

/// <summary>連線失敗的基底類別；訊息為可直接顯示給使用者的中文說明。</summary>
public class ConnectException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>TCP 連不到（NET-02：明確提示不可達，不誤報為驗證失敗）。</summary>
public sealed class HostUnreachableException(string address, Exception inner)
    : ConnectException($"連不到 {address}。請確認 IP、通訊埠轉發、防火牆；CGNAT 或受限 NAT 後面的主機無法直接連線。", inner);

/// <summary>主機公鑰與先前核准的不同（SEC-02：直接拒絕，需人工重新核准）。</summary>
public sealed class HostKeyChangedException(string address, string newFingerprint)
    : ConnectException($"{address} 的主機金鑰已變更，連線已拒絕。新指紋：{newFingerprint}。若確認是重新安裝造成，請先移除舊的信任記錄再重新核對。")
{
    public string NewFingerprint { get; } = newFingerprint;
}

/// <summary>使用者未確認首次連線的指紋（SEC-01）。</summary>
public sealed class FingerprintNotConfirmedException()
    : ConnectException("未確認主機指紋，未登入。");

public sealed class HostBusyException()
    : ConnectException("遠端已有其他控制者連線。");

public sealed class ProtocolVersionException(int remoteVersion)
    : ConnectException($"協定版本不相容（遠端 {remoteVersion}），請更新兩端程式。");

public sealed class LoginFailedException(LoginStatus status, TimeSpan retryAfter)
    : ConnectException(status switch
    {
        LoginStatus.InvalidCredentials => "帳號或密碼錯誤。",
        LoginStatus.Throttled => $"嘗試次數過多，請於 {Math.Ceiling(retryAfter.TotalSeconds)} 秒後再試。",
        LoginStatus.Busy => "遠端已有其他控制者連線。",
        LoginStatus.NotAllowed => "遠端目前不允許控制（例如 HostGuard 未執行）。",
        _ => "登入失敗。",
    })
{
    public LoginStatus Status { get; } = status;
    public TimeSpan RetryAfter { get; } = retryAfter;
}
