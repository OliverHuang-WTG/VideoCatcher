using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using Microsoft.Web.WebView2.Core;

namespace VideoCatcher;

public partial class MainWindow : Window
{
    const string HomePage = "about:blank";
    const double TabWidth = 240;
    const double SidebarMinWidth = 260;
    const double SidebarMaxWidth = 600;

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
        Fetcher = new MediaFetcher(_relay);
        _downloader = new Downloader(_relay, Fetcher);
        _downloader.ActiveChanged += UpdateDownloads;
        DownloadList.ItemsSource = _downloader.Tasks;
        UpdateDownloads();
        UpdateProxyButton();
        ApplySidebar();

        // All tabs share one environment, so one profile (logins persist) and one proxy argument.
        var userData = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VideoCatcher", "WebView2");
        _env = CoreWebView2Environment.CreateAsync(null, userData,
            new CoreWebView2EnvironmentOptions($"--proxy-server=http://127.0.0.1:{_relay.Port}")
            {
                // Edge's thin overlay scrollbars instead of Chromium's classic ones.
                ScrollBarStyle = CoreWebView2ScrollbarStyle.FluentOverlay,
            });

        Loaded += (_, _) => _ = NewTab(HomePage);
    }

    // Shared by tabs (reading captured playlists) and the downloader.
    internal MediaFetcher Fetcher { get; }

    BrowserTab? Current => TabStrip.SelectedItem as BrowserTab;

    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    // Windows 11 doesn't round custom-chrome windows by default. Windows 10 rejects the attribute and stays square;
    // maximized and fullscreen windows are never rounded.
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        const int DWMWA_WINDOW_CORNER_PREFERENCE = 33, DWMWCP_ROUND = 2;
        var preference = DWMWCP_ROUND;
        DwmSetWindowAttribute(new WindowInteropHelper(this).Handle, DWMWA_WINDOW_CORNER_PREFERENCE,
            ref preference, sizeof(int));
    }

    // url == null leaves the page blank so it can be handed to NewWindowRequested.
    internal async Task<CoreWebView2> NewTab(string? url)
    {
        var tab = new BrowserTab(this);
        Views.Children.Add(tab.View);
        _tabs.Add(tab);
        TabStrip.SelectedItem = tab;
        LayoutTabs();
        await tab.InitAsync(await _env, url);
        // Like Edge, a new blank tab is ready for typing.
        if (url == HomePage) Address.Focus();
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
        TitleBar.Visibility = Toolbar.Visibility = on ? Visibility.Collapsed : Visibility.Visible;
        ApplySidebar();
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

    // Expanded: resizable panel. Collapsed: icon rail sized to its content. Fullscreen: hidden.
    void ApplySidebar()
    {
        var expanded = Settings.Current.SidebarExpanded;
        var panel = expanded && !_fullScreen;
        Sidebar.Visibility = _fullScreen ? Visibility.Collapsed : Visibility.Visible;
        SidebarPanel.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
        SidebarRail.Visibility = expanded ? Visibility.Collapsed : Visibility.Visible;
        SidebarSplitter.Visibility = panel ? Visibility.Visible : Visibility.Collapsed;
        SidebarColumn.MinWidth = panel ? SidebarMinWidth : 0;
        SidebarColumn.MaxWidth = panel ? SidebarMaxWidth : double.PositiveInfinity;
        SidebarColumn.Width = panel ? new GridLength(Settings.Current.SidebarWidth) : GridLength.Auto;
    }

    void SetSidebarExpanded(bool expanded)
    {
        Settings.Current.SidebarExpanded = expanded;
        Settings.Current.Save();
        ApplySidebar();
    }

    void ShowTab(BrowserTab tab)
    {
        foreach (UIElement view in Views.Children)
            view.Visibility = view == tab.View ? Visibility.Visible : Visibility.Collapsed;
        VideoList.ItemsSource = tab.Media;
        var count = tab.Media.Count;
        VideoHeader.Text = count > 0 ? $"当前页视频 · {count}" : "当前页视频";
        VideoEmpty.Visibility = count == 0 ? Visibility.Visible : Visibility.Collapsed;
        RailBadge.Visibility = count > 0 ? Visibility.Visible : Visibility.Collapsed;
        RailBadgeText.Text = count > 99 ? "99+" : count.ToString();

        var core = tab.View.CoreWebView2;
        var source = core?.Source is { } url and not HomePage ? url : "";
        if (!Address.IsKeyboardFocusWithin) Address.Text = source;
        AddressIcon.Text = source.StartsWith("https://") ? "" : "";
        BackButton.IsEnabled = core?.CanGoBack == true;
        ForwardButton.IsEnabled = core?.CanGoForward == true;
        Title = tab.Title == BrowserTab.BlankTitle ? "VideoCatcher" : $"{tab.Title} - VideoCatcher";
    }

    void UpdateDownloads() =>
        DownloadEmpty.Visibility = _downloader.Tasks.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

    void UpdateProxyButton()
    {
        var settings = Settings.Current;
        var on = settings.UpstreamProxy is not null;
        ProxyButton.IsChecked = on;
        if (settings.Proxy.Length == 0)
        {
            ProxyLabel.Text = "设置代理";
            ProxyButton.ToolTip = "未设置代理，点击设置";
            return;
        }
        ProxyLabel.Text = on ? "代理已开" : "代理已关";
        // Host and port only: the address may carry credentials.
        var proxy = new Uri(settings.Proxy);
        var action = on ? "关闭" : "开启";
        ProxyButton.ToolTip = $"{proxy.Scheme}://{proxy.Host}:{proxy.Port}，点击{action}";
    }

    void OpenSettings()
    {
        new SettingsWindow(_relay, _downloader) { Owner = this }.ShowDialog();
        UpdateProxyButton();
    }

    void OpenDownload(DownloadTask task)
    {
        if (ExistingFile(task) is { } path) Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }

    // Null when the download isn't finished, or the file was deleted since (the status then says so).
    static string? ExistingFile(DownloadTask task)
    {
        if (task.Record is not { } record) return null;
        if (File.Exists(record.FilePath)) return record.FilePath;
        task.Status = "文件已移除";
        return null;
    }

    static DownloadTask TaskOf(object sender) => (DownloadTask)((FrameworkElement)sender).DataContext;

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
            Address.Text = tab.View.CoreWebView2?.Source is { } url and not HomePage ? url : "";
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

    void ToggleSidebar_Click(object sender, RoutedEventArgs e) => SetSidebarExpanded(!Settings.Current.SidebarExpanded);

    void SidebarSplitter_DragCompleted(object sender, DragCompletedEventArgs e)
    {
        Settings.Current.SidebarWidth = SidebarColumn.ActualWidth;
        Settings.Current.Save();
    }

    // The new download appears at the top of the downloads section, so make sure the sidebar shows it.
    void Download_Click(object sender, RoutedEventArgs e)
    {
        _downloader.Start((MediaItem)((FrameworkElement)sender).DataContext);
        if (!Settings.Current.SidebarExpanded) SetSidebarExpanded(true);
    }

    void Cancel_Click(object sender, RoutedEventArgs e) => TaskOf(sender).Cts.Cancel();

    void DownloadList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if ((e.OriginalSource as FrameworkElement)?.DataContext is DownloadTask task) OpenDownload(task);
    }

    void OpenDownload_Click(object sender, RoutedEventArgs e) => OpenDownload(TaskOf(sender));

    void ShowInFolder_Click(object sender, RoutedEventArgs e)
    {
        if (ExistingFile(TaskOf(sender)) is { } path) Process.Start("explorer.exe", $"/select,\"{path}\"");
    }

    void RemoveDownload_Click(object sender, RoutedEventArgs e)
    {
        _downloader.Remove(TaskOf(sender));
        UpdateDownloads();
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
