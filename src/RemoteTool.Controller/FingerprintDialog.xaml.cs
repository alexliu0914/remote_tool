using System.Windows;
using System.Windows.Media;
using RemoteTool.Connection;

namespace RemoteTool.Controller;

/// <summary>首次連線的指紋確認，以及指紋不符時的警告。預設按鈕一律是「取消」。</summary>
public partial class FingerprintDialog : Window
{
    private FingerprintDialog() => InitializeComponent();

    public static bool ConfirmNewHost(Window owner, string hostKey, string fingerprint)
    {
        var d = new FingerprintDialog { Owner = owner };
        d.HeadingText.Text = $"第一次連到 {hostKey}";
        d.BodyText.Text = "請到被控端的 Host 視窗，逐字核對下面的指紋完全相同，才按「信任並連線」。\n" +
                          "指紋不同代表可能連到別台電腦或遭到攔截，請按取消。帳號密碼此時尚未送出。";
        d.ActualBox.Text = Fingerprint.ForDisplay(fingerprint);
        return d.ShowDialog() == true;
    }

    public static void ShowMismatch(Window owner, string hostKey, string expected, string actual)
    {
        var d = new FingerprintDialog { Owner = owner, Title = "警告：主機指紋不同" };
        d.HeadingText.Text = $"警告：{hostKey} 的主機指紋與上次不同！";
        d.HeadingText.Foreground = Brushes.Firebrick;
        d.BodyText.Text = "已中止連線，帳號密碼未送出。\n\n" +
                          "可能是有人在中間攔截，或被控端重新產生了憑證（例如刪除了資料夾、重灌）。\n" +
                          "確認是被控端自己換了憑證後，才在登入視窗按「移除此主機的指紋記錄」再重新連線。";
        d.ExpectedLabel.Visibility = d.ExpectedBox.Visibility = Visibility.Visible;
        d.ExpectedBox.Text = Fingerprint.ForDisplay(expected);
        d.ActualLabel.Text = "這次收到：";
        d.ActualBox.Text = Fingerprint.ForDisplay(actual);
        d.TrustButton.Visibility = Visibility.Collapsed;
        d.CancelButton.Content = "關閉";
        d.ShowDialog();
    }

    private void OnTrust(object sender, RoutedEventArgs e) => DialogResult = true;

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;
}
