using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Web.WebView2.Core;

namespace VideoCatcher;

public partial class MainWindow : Window
{
    const string HomePage = "https://www.bing.com";

    readonly ProxyRelay _relay = new(Settings.Current.ProxyUri);
    readonly Downloader _downloader;
    readonly Task<CoreWebView2Environment> _env;
    WindowState _stateBeforeFullScreen;
    GridLength _sideWidth;

    public MainWindow()
    {
        InitializeComponent();
        _downloader = new Downloader(_relay);
        DownloadList.ItemsSource = _downloader.Tasks;

        // All tabs share one environment, so one profile (logins persist) and one proxy argument.
        var userData = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VideoCatcher", "WebView2");
        _env = CoreWebView2Environment.CreateAsync(null, userData,
            new CoreWebView2EnvironmentOptions($"--proxy-server=http://127.0.0.1:{_relay.Port}"));

        Loaded += (_, _) => _ = NewTab(HomePage);
    }

    BrowserTab? Current => (Tabs.SelectedItem as TabItem)?.Tag as BrowserTab;

    // url == null leaves the page blank so it can be handed to NewWindowRequested.
    internal async Task<CoreWebView2> NewTab(string? url)
    {
        var tab = new BrowserTab(this);
        Views.Children.Add(tab.View);
        Tabs.Items.Add(tab.Tab);
        Tabs.SelectedItem = tab.Tab;
        await tab.InitAsync(await _env, url);
        return tab.View.CoreWebView2;
    }

    internal void CloseTab(BrowserTab tab)
    {
        Tabs.Items.Remove(tab.Tab);
        Views.Children.Remove(tab.View);
        tab.View.Dispose();
        if (Tabs.Items.Count == 0) _ = NewTab(HomePage);
    }

    internal void OnTabChanged(BrowserTab tab)
    {
        if (tab == Current) ShowTab(tab);
    }

    internal void SetFullScreen(bool on)
    {
        Toolbar.Visibility = Tabs.Visibility = Splitter.Visibility = on ? Visibility.Collapsed : Visibility.Visible;
        if (on)
        {
            _sideWidth = SideColumn.Width;
            SideColumn.Width = new GridLength(0);
            _stateBeforeFullScreen = WindowState;
            // Re-maximizing after removing the border is what makes the window cover the taskbar.
            WindowState = WindowState.Normal;
            WindowStyle = WindowStyle.None;
            WindowState = WindowState.Maximized;
        }
        else
        {
            SideColumn.Width = _sideWidth;
            WindowStyle = WindowStyle.SingleBorderWindow;
            WindowState = _stateBeforeFullScreen;
        }
    }

    void ShowTab(BrowserTab tab)
    {
        foreach (UIElement view in Views.Children)
            view.Visibility = view == tab.View ? Visibility.Visible : Visibility.Collapsed;
        MediaList.ItemsSource = tab.Media;

        var core = tab.View.CoreWebView2;
        if (!Address.IsKeyboardFocusWithin) Address.Text = core?.Source ?? "";
        BackButton.IsEnabled = core?.CanGoBack == true;
        ForwardButton.IsEnabled = core?.CanGoForward == true;
        Title = string.IsNullOrEmpty(core?.DocumentTitle) ? "VideoCatcher" : $"{core.DocumentTitle} - VideoCatcher";
    }

    void Tabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (Current is { } tab) ShowTab(tab);
    }

    void Address_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || Current is not { } tab) return;
        var text = Address.Text.Trim();
        if (!text.Contains("://")) text = "https://" + text;
        if (Uri.TryCreate(text, UriKind.Absolute, out var uri)) tab.View.CoreWebView2?.Navigate(uri.AbsoluteUri);
    }

    void Back_Click(object sender, RoutedEventArgs e) => Current?.View.GoBack();

    void Forward_Click(object sender, RoutedEventArgs e) => Current?.View.GoForward();

    void Reload_Click(object sender, RoutedEventArgs e) => Current?.View.Reload();

    void NewTab_Click(object sender, RoutedEventArgs e) => _ = NewTab(HomePage);

    void Settings_Click(object sender, RoutedEventArgs e) =>
        new SettingsWindow(_relay, _downloader) { Owner = this }.ShowDialog();

    void Download_Click(object sender, RoutedEventArgs e) =>
        _downloader.Start((MediaItem)((FrameworkElement)sender).DataContext);

    void Cancel_Click(object sender, RoutedEventArgs e) =>
        ((DownloadTask)((FrameworkElement)sender).DataContext).Cts.Cancel();

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
