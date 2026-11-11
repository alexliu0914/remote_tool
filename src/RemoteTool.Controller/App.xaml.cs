using System.Windows;
using System.Windows.Threading;

namespace RemoteTool.Controller;

// 控制端（你的電腦）— WPF 版（A1 起，見 D-031）。
//
// 目前完成：以 IP 建立三條 TLS 1.3 連線、首次核對主機指紋（送出帳密之前）、指紋變更即中止、
// 登入、心跳；A1：接收區塊差異畫面並以 WriteableBitmap 顯示（PoC，見 D-027、D-028）。
//
// 尚未實作：H.264 解碼與 HwndHost + D3D11 顯示（A2）、鍵鼠（B1）、剪貼簿、檔案拖拉。
public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException += OnUnhandledException;
        base.OnStartup(e);
    }

    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        MessageBox.Show($"發生未預期的錯誤：\n\n{e.Exception.Message}", "RemoteTool", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}
