using System.IO;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Windows;
using RemoteTool.Connection;
using RemoteTool.Protocol;
using RemoteTool.Video;

namespace RemoteTool.Controller;

public partial class LoginWindow : Window
{
    private readonly ControllerSettings _settings = ControllerSettings.Load();

    public LoginWindow()
    {
        InitializeComponent();
        HostBox.Text = _settings.Host;
        PortBox.Text = _settings.Port.ToString();
        UserBox.Text = _settings.Username;
        if (_settings.Host.Length > 0 && _settings.Username.Length > 0)
            Loaded += (_, _) => PasswordBox.Focus();
    }

    private bool TryReadTarget(out string host, out int port)
    {
        host = HostBox.Text.Trim();
        port = 0;
        if (host.Length == 0)
        {
            ShowStatus("請輸入被控端 IP。");
            return false;
        }
        if (!int.TryParse(PortBox.Text.Trim(), out port) || port is < 1 or > 65535)
        {
            ShowStatus("埠必須是 1～65535 的數字。");
            return false;
        }
        return true;
    }

    private void ShowStatus(string text, bool error = true)
    {
        StatusText.Foreground = error ? System.Windows.Media.Brushes.Firebrick : System.Windows.Media.Brushes.DimGray;
        StatusText.Text = text;
    }

    private void SetBusy(bool busy)
    {
        ConnectButton.IsEnabled = ForgetButton.IsEnabled = !busy;
        HostBox.IsEnabled = PortBox.IsEnabled = UserBox.IsEnabled = PasswordBox.IsEnabled = !busy;
    }

    private void OnForget(object sender, RoutedEventArgs e)
    {
        if (!TryReadTarget(out string host, out int port)) return;
        string key = KnownHostsStore.KeyFor(host, port);
        var store = new KnownHostsStore(AppPaths.KnownHostsFile);
        if (store.Get(key) is null)
        {
            ShowStatus($"沒有 {key} 的指紋記錄。", error: false);
            return;
        }
        var answer = MessageBox.Show(this,
            $"確定要移除 {key} 的指紋記錄？\n\n只有在確認被控端自己換了憑證（例如刪除資料夾、重灌）時才這麼做。",
            "移除指紋記錄", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        if (answer != MessageBoxResult.Yes) return;
        store.Remove(key);
        ShowStatus($"已移除 {key} 的指紋記錄。下次連線會重新核對指紋。", error: false);
    }

    private async void OnConnect(object sender, RoutedEventArgs e)
    {
        if (!TryReadTarget(out string host, out int port)) return;
        string user = UserBox.Text.Trim();
        string pass = PasswordBox.Password;

        _settings.Host = host;
        _settings.Port = port;
        _settings.Username = user;
        _settings.Save();

        SetBusy(true);
        ShowStatus($"連線到 {host}:{port} …", error: false);
        DiagnosticLog.Write($"連線開始 {host}:{port}");

        var canvas = new FrameCanvas();
        RemoteWindow? remote = null;
        ControllerSession? session = null;
        try
        {
            var knownHosts = new KnownHostsStore(AppPaths.KnownHostsFile);
            string hostKey = KnownHostsStore.KeyFor(host, port);

            // 首次連線：先只取得指紋並關閉連線，再請使用者核對。
            // 核對期間沒有任何進行中的連線或計時器，使用者可以慢慢比對（R-13）。
            if (knownHosts.Get(hostKey) is null)
            {
                string fingerprint = await ControllerClient.ProbeFingerprintAsync(host, port);
                if (!FingerprintDialog.ConfirmNewHost(this, hostKey, fingerprint))
                    throw new FingerprintRejectedException(hostKey, fingerprint);
                knownHosts.Set(hostKey, fingerprint);
                DiagnosticLog.Write($"已信任 {hostKey} 的指紋");
                ShowStatus($"登入 {host}:{port} …", error: false);
            }

            session = await ControllerClient.ConnectAsync(new ControllerConnectOptions
            {
                Host = host,
                Port = port,
                Username = user,
                Password = pass,
                KnownHosts = knownHosts,
                // 上面已處理首次連線；走到這裡仍是陌生主機代表狀態異常，一律不信任。
                ConfirmNewHost = (_, _) => false,
                // 背景執行緒：先解到畫布，畫面視窗存在時再通知它更新顯示。
                VideoFrameReceived = frame =>
                {
                    if (canvas.Apply(MessageCodec.Decode<VideoTileUpdate>(frame), out var dirty))
                        Volatile.Read(ref remote)?.OnFrameCompleted(dirty);
                },
            });
            DiagnosticLog.Write($"連線成功 {hostKey}");
        }
        catch (FingerprintMismatchException ex)
        {
            DiagnosticLog.Write($"指紋不符 {ex.HostKey}");
            ShowStatus("主機指紋與上次不同，已中止連線（帳密未送出）。");
            FingerprintDialog.ShowMismatch(this, ex.HostKey, ex.Expected, ex.Actual);
        }
        catch (FingerprintRejectedException ex)
        {
            DiagnosticLog.Write($"使用者未信任 {ex.HostKey}");
            ShowStatus("未信任此主機，已中止連線（帳密未送出）。");
        }
        catch (AuthenticationFailedException ex)
        {
            DiagnosticLog.Write($"登入失敗：{ex.Error}");
            ShowStatus("登入失敗：" + ex.Message);
        }
        catch (Exception ex) when (ex is SocketException or IOException or AuthenticationException
                                       or OperationCanceledException or ProtocolViolationException or InvalidDataException)
        {
            DiagnosticLog.Write("連線失敗", ex);
            string reason = ex is OperationCanceledException ? "逾時，被控端沒有回應" : ex.Message;
            ShowStatus($"連線失敗：{reason}\n請確認被控端 Host.exe 已啟動、IP 與埠正確、被控端防火牆已開放 TCP {port}。\n" +
                       $"詳細記錄：{DiagnosticLog.FilePath}");
        }
        finally
        {
            PasswordBox.Clear();
            SetBusy(false);
        }

        if (session is null) return;

        // 視窗建立前已收到的畫面已在 canvas 中，視窗載入時會整張繪出。
        var window = new RemoteWindow(session, canvas);
        Volatile.Write(ref remote, window);
        window.Closed += (_, _) =>
        {
            Show();
            if (window.EndReason is { } reason)
                ShowStatus("連線結束：" + reason, error: true);
            else
                ShowStatus("已中斷連線。", error: false);
        };
        window.Show();
        Hide();
    }
}
