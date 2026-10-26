using System.IO;
using System.Windows;
using Microsoft.Win32;

namespace VideoCatcher;

public partial class SettingsWindow : Window
{
    readonly ProxyRelay _relay;
    readonly Downloader _downloader;

    internal SettingsWindow(ProxyRelay relay, Downloader downloader)
    {
        InitializeComponent();
        _relay = relay;
        _downloader = downloader;
        var settings = Settings.Current;
        SaveFolderBox.Text = settings.SaveFolder;
        ProxyBox.Text = settings.Proxy;
        MinSizeBox.Text = settings.MinSizeMB.ToString();
    }

    void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { InitialDirectory = SaveFolderBox.Text };
        if (dialog.ShowDialog(this) == true) SaveFolderBox.Text = dialog.FolderName;
    }

    void Save_Click(object sender, RoutedEventArgs e)
    {
        var folder = SaveFolderBox.Text.Trim();
        var proxy = ProxyBox.Text.Trim();
        if (!Directory.Exists(folder))
        {
            Warn("保存目录不存在");
            return;
        }
        if (proxy.Length > 0 &&
            !(Uri.TryCreate(proxy, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "socks5" && uri.Port > 0))
        {
            Warn("代理格式应为 http://host:port 或 socks5://host:port");
            return;
        }
        if (!int.TryParse(MinSizeBox.Text, out var minSize) || minSize < 0)
        {
            Warn("最小文件大小应为非负整数");
            return;
        }

        var settings = Settings.Current;
        var proxyChanged = proxy != settings.Proxy;
        // Switching drops every relayed connection, including the ones ffmpeg is downloading through.
        if (proxyChanged && _downloader.HasActive &&
            MessageBox.Show(this, "切换代理会中断正在进行的下载，继续？", Title,
                MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;

        settings.SaveFolder = folder;
        settings.Proxy = proxy;
        settings.MinSizeMB = minSize;
        settings.Save();
        if (proxyChanged) _relay.SetUpstream(settings.ProxyUri);
        DialogResult = true;
    }

    void Warn(string message) => MessageBox.Show(this, message, Title, MessageBoxButton.OK, MessageBoxImage.Warning);
}
