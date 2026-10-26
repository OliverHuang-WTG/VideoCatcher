using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Web.WebView2.Core;

namespace VideoCatcher;

public partial class MainWindow : Window
{
    const string HomePage = "https://www.bing.com";
    const double TabWidth = 240;

    readonly ProxyRelay _relay = new(Settings.Current.UpstreamProxy);
    readonly Downloader _downloader;
    readonly Task<CoreWebView2Environment> _env;
    readonly ObservableCollection<BrowserTab> _tabs = [];
    WindowState _stateBeforeFullScreen;
    bool _fullScreen;

    public MainWindow()
    {
        InitializeComponent();
        TabStrip.ItemsSource = _tabs;
        _downloader = new Downloader(_relay);
        _downloader.ActiveChanged += UpdateDownloads;
        DownloadList.ItemsSource = _downloader.Tasks;
        UpdateProxyButton();

        // All tabs share one environment, so one profile (logins persist) and one proxy argument.
        var userData = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VideoCatcher", "WebView2");
        _env = CoreWebView2Environment.CreateAsync(null, userData,
            new CoreWebView2EnvironmentOptions($"--proxy-server=http://127.0.0.1:{_relay.Port}"));

        Loaded += (_, _) => _ = NewTab(HomePage);
    }

    BrowserTab? Current => TabStrip.SelectedItem as BrowserTab;

    // url == null leaves the page blank so it can be handed to NewWindowRequested.
    internal async Task<CoreWebView2> NewTab(string? url)
    {
        var tab = new BrowserTab(this);
        Views.Children.Add(tab.View);
        _tabs.Add(tab);
        TabStrip.SelectedItem = tab;
        LayoutTabs();
        await tab.InitAsync(await _env, url);
        return tab.View.CoreWebView2;
    }

    internal void CloseTab(BrowserTab tab)
    {
        var index = _tabs.IndexOf(tab);
        _tabs.Remove(tab);
        Views.Children.Remove(tab.View);
        tab.View.Dispose();
        if (_tabs.Count == 0)
        {
            _ = NewTab(HomePage);
            return;
        }
        // ListBox leaves nothing selected when the selected item is removed.
        TabStrip.SelectedItem ??= _tabs[Math.Min(index, _tabs.Count - 1)];
        LayoutTabs();
    }

    internal void OnTabChanged(BrowserTab tab)
    {
        if (tab == Current) ShowTab(tab);
    }

    internal void SetFullScreen(bool on)
    {
        _fullScreen = on;
        VideoPopup.IsOpen = DownloadPopup.IsOpen = false;
        TitleBar.Visibility = Toolbar.Visibility = on ? Visibility.Collapsed : Visibility.Visible;
        if (on)
        {
            _stateBeforeFullScreen = WindowState;
            // Re-maximizing after removing the border is what makes the window cover the taskbar.
            WindowState = WindowState.Normal;
            WindowStyle = WindowStyle.None;
            WindowState = WindowState.Maximized;
        }
        else
        {
            WindowStyle = WindowStyle.SingleBorderWindow;
            WindowState = _stateBeforeFullScreen;
        }
    }

    // Fixed-width tabs that shrink evenly once they no longer fit, leaving room for the new-tab button.
    void LayoutTabs()
    {
        var available = TitleBar.ColumnDefinitions[0].ActualWidth - 56;
        TabStrip.Width = Math.Max(0, Math.Min(_tabs.Count * TabWidth, available));
    }

    void ShowTab(BrowserTab tab)
    {
        foreach (UIElement view in Views.Children)
            view.Visibility = view == tab.View ? Visibility.Visible : Visibility.Collapsed;
        VideoList.ItemsSource = tab.Media;
        VideoEmpty.Visibility = tab.Media.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        SetBadge(VideoBadge, VideoBadgeText, tab.Media.Count);

        var core = tab.View.CoreWebView2;
        var source = core?.Source ?? "";
        if (!Address.IsKeyboardFocusWithin) Address.Text = source;
        AddressIcon.Text = source.StartsWith("https://") ? "" : "";
        BackButton.IsEnabled = core?.CanGoBack == true;
        ForwardButton.IsEnabled = core?.CanGoForward == true;
        Title = string.IsNullOrEmpty(core?.DocumentTitle) ? "VideoCatcher" : $"{core.DocumentTitle} - VideoCatcher";
    }

    void UpdateDownloads()
    {
        SetBadge(DownloadBadge, DownloadBadgeText, _downloader.Tasks.Count(t => t.IsActive));
        DownloadEmpty.Visibility = _downloader.Tasks.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    void UpdateProxyButton()
    {
        var settings = Settings.Current;
        ProxyButton.IsChecked = settings.UpstreamProxy is not null;
        if (settings.Proxy.Length == 0)
        {
            ProxyButton.ToolTip = "未设置代理，点击设置";
            return;
        }
        // Host and port only: the address may carry credentials.
        var proxy = new Uri(settings.Proxy);
        var state = settings.ProxyEnabled ? "开启" : "关闭";
        ProxyButton.ToolTip = $"代理已{state}：{proxy.Scheme}://{proxy.Host}:{proxy.Port}";
    }

    static void SetBadge(Border badge, TextBlock text, int count)
    {
        badge.Visibility = count > 0 ? Visibility.Visible : Visibility.Collapsed;
        text.Text = count > 99 ? "99+" : count.ToString();
    }

    void OpenSettings()
    {
        new SettingsWindow(_relay, _downloader) { Owner = this }.ShowDialog();
        UpdateProxyButton();
    }

    void Window_StateChanged(object? sender, EventArgs e)
    {
        // A maximized WindowChrome window extends past the screen by its resize border; pad the content back in.
        Root.Margin = WindowState == WindowState.Maximized && !_fullScreen
            ? SystemParameters.WindowResizeBorderThickness
            : default;
        MaximizeButton.Content = WindowState == WindowState.Maximized ? "" : "";
    }

    void TitleBar_SizeChanged(object sender, SizeChangedEventArgs e) => LayoutTabs();

    void TabStrip_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (Current is { } tab) ShowTab(tab);
    }

    void Tab_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Middle) CloseTab((BrowserTab)((FrameworkElement)sender).DataContext);
    }

    void CloseTab_Click(object sender, RoutedEventArgs e) =>
        CloseTab((BrowserTab)((FrameworkElement)sender).DataContext);

    void NewTab_Click(object sender, RoutedEventArgs e) => _ = NewTab(HomePage);

    void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    void Maximize_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    void Close_Click(object sender, RoutedEventArgs e) => Close();

    // Edge selects the whole URL on the first click so typing replaces it.
    void Address_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (Address.IsKeyboardFocusWithin) return;
        Address.Focus();
        Address.SelectAll();
        e.Handled = true;
    }

    void Address_KeyDown(object sender, KeyEventArgs e)
    {
        if (Current is not { } tab) return;
        if (e.Key == Key.Enter)
        {
            tab.Navigate(Address.Text);
            tab.View.Focus();
        }
        else if (e.Key == Key.Escape)
        {
            Address.Text = tab.View.CoreWebView2?.Source ?? "";
            tab.View.Focus();
        }
    }

    void Back_Click(object sender, RoutedEventArgs e) => Current?.View.GoBack();

    void Forward_Click(object sender, RoutedEventArgs e) => Current?.View.GoForward();

    void Reload_Click(object sender, RoutedEventArgs e) => Current?.View.Reload();

    void Proxy_Click(object sender, RoutedEventArgs e)
    {
        var settings = Settings.Current;
        if (settings.Proxy.Length == 0)
        {
            UpdateProxyButton();
            OpenSettings();
            return;
        }
        // Switching drops every relayed connection, including the ones ffmpeg is downloading through.
        if (_downloader.HasActive &&
            MessageBox.Show(this, "切换代理会中断正在进行的下载，继续？", "VideoCatcher",
                MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            UpdateProxyButton();
            return;
        }
        settings.ProxyEnabled = !settings.ProxyEnabled;
        settings.Save();
        _relay.SetUpstream(settings.UpstreamProxy);
        UpdateProxyButton();
    }

    void Settings_Click(object sender, RoutedEventArgs e) => OpenSettings();

    // Like Edge, starting a download swaps the video list for the downloads flyout.
    void Download_Click(object sender, RoutedEventArgs e)
    {
        _downloader.Start((MediaItem)((FrameworkElement)sender).DataContext);
        VideoPopup.IsOpen = false;
        DownloadPopup.IsOpen = true;
    }

    void Cancel_Click(object sender, RoutedEventArgs e) =>
        ((DownloadTask)((FrameworkElement)sender).DataContext).Cts.Cancel();

    void DownloadList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if ((e.OriginalSource as FrameworkElement)?.DataContext is not DownloadTask { FilePath: { } path } task) return;
        if (File.Exists(path)) Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        else task.Status = "文件已移除";
    }

    void OpenFolder_Click(object sender, RoutedEventArgs e) =>
        Process.Start("explorer.exe", $"\"{Settings.Current.SaveFolder}\"");

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        var ctrl = Keyboard.Modifiers == ModifierKeys.Control;
        switch (e.Key)
        {
            case Key.T when ctrl:
                _ = NewTab(HomePage);
                break;
            case Key.W when ctrl:
                if (Current is { } tab) CloseTab(tab);
                break;
            case Key.L when ctrl:
                Address.Focus();
                Address.SelectAll();
                break;
            case Key.F5:
                Current?.View.Reload();
                break;
            default:
                base.OnPreviewKeyDown(e);
                return;
        }
        e.Handled = true;
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (_downloader.HasActive &&
            MessageBox.Show(this, "还有下载未完成，退出会中断下载。确定退出？", "VideoCatcher",
                MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            e.Cancel = true;
            return;
        }
        // ffmpeg is a separate process and would outlive the window otherwise.
        _downloader.CancelAll();
        base.OnClosing(e);
    }
}
