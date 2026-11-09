using RemoteTool.HostGuard.Core;
using RemoteTool.Protocol.Messages;

namespace RemoteTool.Host.Core;

/// <summary>保護設定檔與私鑰（Windows：DPAPI 目前使用者）。</summary>
public interface ISecretProtector
{
    byte[] Protect(byte[] plaintext);
    byte[] Unprotect(byte[] protectedData);
}

/// <summary>
/// 到 HostGuard 的本機 IPC。Host.exe 本身不呼叫 SendInput，只把已驗證的事件交給 HostGuard。
/// </summary>
public interface IGuardChannel
{
    bool IsConnected { get; }

    /// <summary>送出一則訊息；IPC 已中斷時不拋例外，回傳 false。</summary>
    ValueTask<bool> SendAsync(GuardMessage message);

    /// <summary>IPC 中斷（HostGuard 崩潰或被結束）。Host 須立即停止遠端控制並提示。</summary>
    event Action? Faulted;
}

/// <summary>某一時刻的桌面狀態（「桌面可控制」判定的前兩項；第三項 DXGI 由影像來源提供）。</summary>
/// <param name="Locked">工作階段已鎖定。</param>
/// <param name="OnConsole">工作階段在主控台上（未被切換使用者）。</param>
/// <param name="InputDesktopIsDefault">OpenInputDesktop 可開啟且為 Default。</param>
/// <param name="ElevationPromptDetected">G0-07 能「可靠」辨識 UAC 提示時才為 true；否則一律 false。</param>
public readonly record struct DesktopSnapshot(
    bool Locked,
    bool OnConsole,
    bool InputDesktopIsDefault,
    bool ElevationPromptDetected = false)
{
    public static readonly DesktopSnapshot Normal = new(false, true, true);
}

/// <summary>工作階段狀態來源（Windows：WTSRegisterSessionNotification + OpenInputDesktop）。</summary>
public interface IDesktopStateSource
{
    DesktopSnapshot GetSnapshot();

    /// <summary>使用者登出、關機或切換使用者：中止連線。</summary>
    event Action<DisconnectReason>? SessionEnding;

    /// <summary>
    /// 本工作階段是否為主控台上作用中的工作階段。false 時讓出 52100 埠；true 時重新監聽。
    /// </summary>
    event Action<bool>? ConsoleActiveChanged;
}

/// <summary>一張已編碼的畫面。</summary>
public readonly record struct EncodedFrame(bool IsKeyframe, long TimestampUs, byte[] Data);

/// <summary>擷取與編碼（Windows：DXGI Desktop Duplication → Media Foundation H.264；G0-02）。</summary>
public interface IVideoSource
{
    /// <summary>DXGI 擷取是否正常產生畫面（「桌面可控制」第三項）。</summary>
    bool IsCaptureHealthy { get; }

    /// <summary>要求下一張為 IDR 關鍵幀。</summary>
    void RequestKeyframe();

    /// <summary>持續產生畫面；同時只會有一個讀取者。</summary>
    IAsyncEnumerable<EncodedFrame> ReadFramesAsync(CancellationToken cancellationToken);
}

/// <summary>記錄與稽核。任何記錄都不寫密碼、Token 或剪貼簿內容。</summary>
public interface IHostLog
{
    void Info(string message);
    void Warn(string message);

    /// <summary>稽核記錄（Windows 事件記錄）：連線時間、來源 IP、帳號、傳輸的檔案。</summary>
    void Audit(string message);
}

public sealed class NullHostLog : IHostLog
{
    public static readonly NullHostLog Instance = new();
    public void Info(string message) { }
    public void Warn(string message) { }
    public void Audit(string message) { }
}
