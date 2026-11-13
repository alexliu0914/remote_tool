using System.IO.Pipes;
using RemoteTool.Connection;
using RemoteTool.Protocol;

namespace RemoteTool.Host;

/// <summary>
/// 連到 HostGuard 的本機 Named Pipe（只寫入），把已驗證的鍵鼠記錄交給 HostGuard 注入。
/// Pipe 中斷時停止遠端控制並記錄（PLAN §3：HostGuard 故障時 Host 立即停止遠端控制並提示）。
/// </summary>
internal sealed class GuardClient : IInputSink, IDisposable
{
    private readonly NamedPipeClientStream _pipe;
    private readonly Action<string> _log;
    private readonly byte[] _buffer = new byte[GuardRecord.Size];
    private readonly object _gate = new();
    private bool _broken;

    public GuardClient(string pipeName, Action<string> log)
    {
        _log = log;
        _pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.Out, PipeOptions.CurrentUserOnly);
        _pipe.Connect(5000);
    }

    public bool Connected => !_broken && _pipe.IsConnected;

    public void Send(GuardRecord record)
    {
        lock (_gate)
        {
            if (_broken) return;
            try
            {
                record.WriteTo(_buffer);
                _pipe.Write(_buffer);
                _pipe.Flush();
            }
            catch (IOException ex)
            {
                _broken = true;
                _log($"與 HostGuard 的連線中斷（{ex.Message}），遠端鍵鼠已停止");
            }
        }
    }

    public void Dispose() => _pipe.Dispose();
}
