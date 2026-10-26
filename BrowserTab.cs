using System.Collections.ObjectModel;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace VideoCatcher;

sealed class BrowserTab
{
    const string PageInfoScript = """
        ({
            title: document.querySelector('meta[property="og:title"]')?.content || document.title,
            site: document.querySelector('meta[property="og:site_name"]')?.content || ''
        })
        """;

    readonly MainWindow _owner;
    readonly TextBlock _title = new()
    {
        Text = "新标签",
        MaxWidth = 160,
        TextTrimming = TextTrimming.CharacterEllipsis,
        VerticalAlignment = VerticalAlignment.Center,
    };
    readonly HashSet<string> _seen = [];
    bool _hasManifest;

    public BrowserTab(MainWindow owner)
    {
        _owner = owner;
        var close = new Button
        {
            Content = "×",
            Margin = new Thickness(6, 0, 0, 0),
            Padding = new Thickness(4, 0, 4, 0),
            BorderThickness = new Thickness(0),
            Background = Brushes.Transparent,
        };
        close.Click += (_, _) => _owner.CloseTab(this);
        Tab = new TabItem
        {
            Header = new StackPanel { Orientation = Orientation.Horizontal, Children = { _title, close } },
            Tag = this,
        };
        Tab.MouseDown += (_, e) =>
        {
            if (e.ChangedButton == MouseButton.Middle) _owner.CloseTab(this);
        };
    }

    public TabItem Tab { get; }

    public WebView2 View { get; } = new();

    public ObservableCollection<MediaItem> Media { get; } = [];

    public async Task InitAsync(CoreWebView2Environment env, string? url)
    {
        await View.EnsureCoreWebView2Async(env);
        var core = View.CoreWebView2;
        core.DocumentTitleChanged += (_, _) =>
        {
            _title.Text = core.DocumentTitle;
            _owner.OnTabChanged(this);
        };
        core.SourceChanged += (_, _) => _owner.OnTabChanged(this);
        core.HistoryChanged += (_, _) => _owner.OnTabChanged(this);
        core.NavigationStarting += (_, _) =>
        {
            Media.Clear();
            _seen.Clear();
            _hasManifest = false;
        };
        // Handing over a real CoreWebView2 (instead of just the URL) keeps window.opener, which OAuth popups need.
        core.NewWindowRequested += async (_, e) =>
        {
            var deferral = e.GetDeferral();
            e.NewWindow = await _owner.NewTab(null);
            deferral.Complete();
        };
        core.ContainsFullScreenElementChanged += (_, _) => _owner.SetFullScreen(core.ContainsFullScreenElement);
        core.WebResourceResponseReceived += OnResponseReceived;
        if (url is not null) core.Navigate(url);
    }

    async void OnResponseReceived(object? sender, CoreWebView2WebResourceResponseReceivedEventArgs e)
    {
        if (e.Response.StatusCode is < 200 or >= 300) return;
        var url = new Uri(e.Request.Uri);
        if (url.Scheme is not ("http" or "https")) return;
        // Range requests for the same file differ only in query/headers; one entry per path.
        var key = url.GetLeftPart(UriPartial.Path);
        if (_seen.Contains(key)) return;

        var size = MediaSniffer.TotalSize(
            GetHeader(e.Response.Headers, "Content-Range"), GetHeader(e.Response.Headers, "Content-Length"));
        var kind = MediaSniffer.Classify(url, GetHeader(e.Response.Headers, "Content-Type"), size,
            Settings.Current.MinSizeMB * 1024L * 1024, _hasManifest);
        if (kind is null) return;
        _seen.Add(key);
        if (kind == MediaKind.Manifest) _hasManifest = true;

        var core = View.CoreWebView2;
        var cookies = await core.CookieManager.GetCookiesAsync(url.AbsoluteUri);
        using var page = JsonDocument.Parse(await core.ExecuteScriptAsync(PageInfoScript));
        Media.Add(new MediaItem
        {
            Url = url,
            Kind = kind.Value,
            Size = size,
            Referer = GetHeader(e.Request.Headers, "Referer") ?? core.Source,
            UserAgent = GetHeader(e.Request.Headers, "User-Agent") ?? core.Settings.UserAgent,
            Cookie = string.Join("; ", cookies.Select(c => $"{c.Name}={c.Value}")),
            Title = page.RootElement.GetProperty("title").GetString() ?? "",
            Site = page.RootElement.GetProperty("site").GetString() ?? "",
            PageUrl = new Uri(core.Source),
        });
    }

    static string? GetHeader(IEnumerable<KeyValuePair<string, string>> headers, string name) =>
        headers.FirstOrDefault(h => string.Equals(h.Key, name, StringComparison.OrdinalIgnoreCase)).Value;
}
