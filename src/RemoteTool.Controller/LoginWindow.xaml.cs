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

        var canvas = new FrameCanvas();
        RemoteWindow? remote = null;
        ControllerSession? session = null;
        try
        {
            session = await ControllerClient.ConnectAsync(new ControllerConnectOptions
            {
                Host = host,
                Port = port,
                Username = user,
                Password = pass,
                KnownHosts = new KnownHostsStore(AppPaths.KnownHostsFile),
                ConfirmNewHost = (key, fp) => Dispatcher.Invoke(() => FingerprintDialog.ConfirmNewHost(this, key, fp)),
                // 背景執行緒：先解到畫布，畫面視窗存在時再通知它更新顯示。
                VideoFrameReceived = frame =>
                {
                    if (canvas.Apply(MessageCodec.Decode<VideoTileUpdate>(frame), out var dirty))
                        Volatile.Read(ref remote)?.OnFrameCompleted(dirty);
                },
            });
        }
        catch (FingerprintMismatchException ex)
        {
            ShowStatus("主機指紋與上次不同，已中止連線（帳密未送出）。");
            FingerprintDialog.ShowMismatch(this, ex.HostKey, ex.Expected, ex.Actual);
        }
        catch (FingerprintRejectedException)
        {
            ShowStatus("未信任此主機，已中止連線（帳密未送出）。");
        }
        catch (AuthenticationFailedException ex)
        {
            ShowStatus("登入失敗：" + ex.Message);
        }
        catch (Exception ex) when (ex is SocketException or IOException or AuthenticationException
                                       or OperationCanceledException or ProtocolViolationException or InvalidDataException)
        {
            ShowStatus($"連線失敗：{ex.Message}\n請確認被控端 Host.exe 已啟動、IP 與埠正確、被控端防火牆已開放 TCP {port}。");
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
