using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using RemoteTool.HostGuard.Core;
using RemoteTool.Windows;

namespace RemoteTool.HostGuard;

/// <summary>
/// 本機 IPC 伺服器（第三節）：
/// <list type="bullet">
/// <item>Named Pipe 的 ACL 只允許目前使用者，並拒絕網路存取；名稱含隨機值並以 FirstPipeInstance 防搶註。</item>
/// <item>連線端的程序 ID 必須是本 HostGuard 啟動的 Host.exe，且在同一工作階段。</item>
/// <item>只接受白名單訊息；其他訊息丟棄並記錄。</item>
/// </list>
/// 待 GUARD-04 實機驗證。
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class GuardPipeServer
{
    private readonly string _pipeName;
    private readonly HostSupervisor _supervisor;
    private readonly GuardLoop _loop;
    private readonly IGuardLog _log;

    public GuardPipeServer(string pipeName, HostSupervisor supervisor, GuardLoop loop, IGuardLog log)
    {
        _pipeName = pipeName;
        _supervisor = supervisor;
        _loop = loop;
        _log = log;
    }

    public static string NewPipeName() => $"RemoteTool.HostGuard.{DesktopState.CurrentSessionId}.{Guid.NewGuid():N}";

    public async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            NamedPipeServerStream pipe;
            try
            {
                pipe = CreatePipe();
            }
            catch (IOException ex)
            {
                _log.Warn($"無法建立 IPC 管道（可能遭搶註）：{ex.Message}");
                await Task.Delay(TimeSpan.FromSeconds(1), ct).ConfigureAwait(false);
                continue;
            }

            await using (pipe.ConfigureAwait(false))
            {
                try
                {
                    await pipe.WaitForConnectionAsync(ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                if (!await IsAuthorizedClientAsync(pipe, ct).ConfigureAwait(false))
                    continue; // 關閉管道，拒絕連線

                long instance = _supervisor.CurrentInstance;
                _log.Info($"Host.exe 已連上 IPC（實例 {instance}）");
                try
                {
                    while (true)
                    {
                        var frame = await GuardCodec.ReadFrameAsync(pipe, ct).ConfigureAwait(false);
                        if (frame is null)
                            break;
                        var message = GuardCodec.Decode(frame, out var reason);
                        if (message is null)
                        {
                            _log.Warn($"丟棄 IPC 訊息：{reason}");
                            continue;
                        }
                        _loop.Post(instance, message.Value);
                    }
                }
                catch (InvalidDataException ex)
                {
                    _log.Warn($"IPC 格式錯誤，中斷連線：{ex.Message}");
                }
                catch (IOException)
                {
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                finally
                {
                    // IPC 中斷視同 Host 失去控制能力：丟棄佇列並釋放
                    _loop.HostGone(instance);
                    _log.Info($"IPC 中斷（實例 {instance}）");
                }
            }
        }
    }

    private NamedPipeServerStream CreatePipe()
    {
        var security = new PipeSecurity();
        var user = WindowsIdentity.GetCurrent().User!;
        security.SetOwner(user);
        security.AddAccessRule(new PipeAccessRule(user, PipeAccessRights.ReadWrite | PipeAccessRights.CreateNewInstance,
            AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid, null),
            PipeAccessRights.FullControl, AccessControlType.Deny));

        return NamedPipeServerStreamAcl.Create(_pipeName, PipeDirection.In, maxNumberOfServerInstances: 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.FirstPipeInstance,
            inBufferSize: 0, outBufferSize: 0, security);
    }

    private async Task<bool> IsAuthorizedClientAsync(NamedPipeServerStream pipe, CancellationToken ct)
    {
        if (!Win32.GetNamedPipeClientProcessId(pipe.SafePipeHandle, out uint clientPid))
        {
            _log.Warn("無法取得 IPC 連線端程序 ID，拒絕");
            return false;
        }

        // Host.exe 剛啟動時，Supervisor 可能尚未記錄 PID；稍候再比對
        for (int i = 0; i < 20 && _supervisor.CurrentProcessId == 0; i++)
            await Task.Delay(50, ct).ConfigureAwait(false);

        if (clientPid != (uint)_supervisor.CurrentProcessId)
        {
            _log.Warn($"拒絕 IPC 連線：PID {clientPid} 不是 HostGuard 啟動的 Host.exe（{_supervisor.CurrentProcessId}）");
            return false;
        }
        if (!DesktopState.TryGetProcessSession((int)clientPid, out int session) || session != DesktopState.CurrentSessionId)
        {
            _log.Warn($"拒絕 IPC 連線：PID {clientPid} 不在同一工作階段");
            return false;
        }
        return true;
    }
}
