using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace VideoCatcher;

// url: what the tab opens once loaded; null leaves it blank for NewWindowRequested to fill.
sealed class BrowserTab(MainWindow owner, string? url, string title) : INotifyPropertyChanged
{
    // Failures where retrying a typed host over plain http can help (Chromium's HTTPS-first fallback).
    // DNS failures are excluded: http would fail the same way.
    static readonly HashSet<CoreWebView2WebErrorStatus> HttpFallbackErrors =
    [
        CoreWebView2WebErrorStatus.CertificateCommonNameIsIncorrect,
        CoreWebView2WebErrorStatus.CertificateExpired,
        CoreWebView2WebErrorStatus.ClientCertificateContainsErrors,
        CoreWebView2WebErrorStatus.CertificateRevoked,
        CoreWebView2WebErrorStatus.CertificateIsInvalid,
        CoreWebView2WebErrorStatus.ServerUnreachable,
        CoreWebView2WebErrorStatus.Timeout,
        CoreWebView2WebErrorStatus.ErrorHttpInvalidServerResponse,
        CoreWebView2WebErrorStatus.ConnectionAborted,
        CoreWebView2WebErrorStatus.ConnectionReset,
        CoreWebView2WebErrorStatus.Disconnected,
        CoreWebView2WebErrorStatus.CannotConnect,
    ];

    public const string BlankTitle = "新标签";

    readonly HashSet<string> _seen = [];

    // Variant and rendition playlists of a captured master: the master's entry already downloads them.
    readonly HashSet<string> _children = [];
    bool _hasManifest;
    string _title = title;
    ImageSource? _favicon;
    Task<CoreWebView2>? _loading;

    // A typed host navigates to https first; if that navigation fails, _httpFallback is tried once.
    string? _httpsAttempt;
    ulong? _httpsNavigationId;
    string? _httpFallback;

    public WebView2 View { get; } = new();

    public ObservableCollection<MediaItem> Media { get; } = [];

    public string Title { get => _title; private set => Set(ref _title, value); }

    public ImageSource? Favicon { get => _favicon; private set => Set(ref _favicon, value); }

    // The favicon as PNG, kept so adding the page to favorites can store it.
    public byte[]? FaviconPng { get; private set; }

    // The page's address, or the one it will open when a restored tab hasn't loaded yet.
    public string Url => View.CoreWebView2?.Source ?? url ?? "about:blank";

    public event PropertyChangedEventHandler? PropertyChanged;

    // Restored tabs show their saved title and only create their WebView2 when first selected.
    public Task<CoreWebView2> LoadAsync(Task<CoreWebView2Environment> env) => _loading ??= InitAsync(env);

    async Task<CoreWebView2> InitAsync(Task<CoreWebView2Environment> env)
    {
        await View.EnsureCoreWebView2Async(await env);
        var core = View.CoreWebView2;
        core.DocumentTitleChanged += (_, _) =>
        {
            Title = core.Source == "about:blank" || core.DocumentTitle.Length == 0 ? BlankTitle : core.DocumentTitle;
            owner.OnTabChanged(this);
        };
        core.FaviconChanged += async (_, _) => (FaviconPng, Favicon) = await LoadFaviconAsync(core);
        core.SourceChanged += (_, _) => owner.OnTabChanged(this);
        core.HistoryChanged += (_, _) => owner.OnTabChanged(this);
        core.NavigationStarting += (_, e) =>
        {
            if (e.Uri == _httpsAttempt)
            {
                _httpsNavigationId = e.NavigationId;
                _httpsAttempt = null;
            }
            Media.Clear();
            _seen.Clear();
            _children.Clear();
            _hasManifest = false;
            owner.OnTabChanged(this);
        };
        // Matched by id: a navigation cancelled by the new one completes after the new one starts.
        core.NavigationCompleted += (_, e) =>
        {
            if (e.NavigationId != _httpsNavigationId) return;
            _httpsNavigationId = null;
            if (!e.IsSuccess && HttpFallbackErrors.Contains(e.WebErrorStatus)) core.Navigate(_httpFallback!);
        };
        // Handing over a real CoreWebView2 (instead of just the URL) keeps window.opener, which OAuth popups need.
        core.NewWindowRequested += async (_, e) =>
        {
            var deferral = e.GetDeferral();
            e.NewWindow = await owner.NewTab(null);
            deferral.Complete();
        };
        core.ContainsFullScreenElementChanged += (_, _) => owner.SetFullScreen(core.ContainsFullScreenElement);
        core.WebResourceResponseReceived += OnResponseReceived;
        if (url is not null) core.Navigate(url);
        return core;
    }

    // Edge-style address bar: a typed scheme is kept, host-like input tries https first, anything else is a search.
    public void Navigate(string input)
    {
        var text = input.Trim();
        if (text.Length == 0 || View.CoreWebView2 is not { } core) return;

        if ((text.Contains("://") || text.StartsWith("about:", StringComparison.OrdinalIgnoreCase))
            && Uri.TryCreate(text, UriKind.Absolute, out var typed))
        {
            core.Navigate(typed.AbsoluteUri);
        }
        else if (LooksLikeHost(text) && Uri.TryCreate("https://" + text, UriKind.Absolute, out var https))
        {
            _httpsAttempt = https.AbsoluteUri;
            _httpFallback = "http://" + text;
            core.Navigate(_httpsAttempt);
        }
        else
        {
            core.Navigate("https://www.bing.com/search?q=" + Uri.EscapeDataString(text));
        }
    }

    static bool LooksLikeHost(string text)
    {
        if (text.Any(char.IsWhiteSpace)) return false;
        var host = text.Split('/', '?', '#')[0].Split(':')[0];
        return host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || (host.Contains('.') && !host.StartsWith('.') && !host.EndsWith('.')
                && Uri.CheckHostName(host) != UriHostNameType.Unknown);
    }

    static async Task<(byte[]? Png, ImageSource? Image)> LoadFaviconAsync(CoreWebView2 core)
    {
        if (string.IsNullOrEmpty(core.FaviconUri)) return (null, null);
        using var stream = await core.GetFaviconAsync(CoreWebView2FaviconImageFormat.Png);
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer);
        if (buffer.Length == 0) return (null, null);
        var png = buffer.ToArray();
        return (png, ToImage(png));
    }

    internal static ImageSource ToImage(byte[] png)
    {
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.StreamSource = new MemoryStream(png);
        image.EndInit();
        image.Freeze();
        return image;
    }

    async void OnResponseReceived(object? sender, CoreWebView2WebResourceResponseReceivedEventArgs e)
    {
        if (e.Response.StatusCode is < 200 or >= 300) return;
        var url = new Uri(e.Request.Uri);
        if (url.Scheme is not ("http" or "https")) return;
        // Range requests for the same file differ only in query/headers; one entry per path.
        var key = url.GetLeftPart(UriPartial.Path);
        if (_seen.Contains(key) || _children.Contains(key)) return;

        var size = MediaSniffer.TotalSize(
            GetHeader(e.Response.Headers, "Content-Range"), GetHeader(e.Response.Headers, "Content-Length"));
        var kind = MediaSniffer.Classify(url, GetHeader(e.Response.Headers, "Content-Type"), size,
            Settings.Current.MinSizeMB * 1024L * 1024, _hasManifest);
        if (kind is null) return;
        _seen.Add(key);
        if (kind is MediaKind.Hls or MediaKind.Dash) _hasManifest = true;

        // Read everything from the event args before awaiting; they aren't guaranteed valid afterwards.
        var core = View.CoreWebView2;
        var name = FileNamer.SourceName(url, GetHeader(e.Response.Headers, "Content-Disposition"));
        var referer = GetHeader(e.Request.Headers, "Referer") ?? core.Source;
        var userAgent = GetHeader(e.Request.Headers, "User-Agent") ?? core.Settings.UserAgent;
        var playlist = kind == MediaKind.Hls ? e.Response.GetContentAsync() : null;
        var cookies = await core.CookieManager.GetCookiesAsync(url.AbsoluteUri);
        var item = new MediaItem
        {
            Url = url,
            Kind = kind.Value,
            Size = kind == MediaKind.Hls ? null : size, // a playlist's own length says nothing about the video
            Name = name,
            Referer = referer,
            UserAgent = userAgent,
            Cookie = string.Join("; ", cookies.Select(c => $"{c.Name}={c.Value}")),
        };
        if (playlist is not null) item = await DescribeHlsAsync(item, playlist);
        // A master captured while this one was being read may cover it.
        if (_children.Contains(key)) return;
        Media.Add(item);
        owner.OnTabChanged(this);
    }

    // Adds size, resolution and duration from the playlist, and drops entries for playlists a master references.
    async Task<MediaItem> DescribeHlsAsync(MediaItem item, Task<Stream> content)
    {
        try
        {
            using var reader = new StreamReader(await content);
            var source = await owner.Fetcher.LoadHlsAsync(item, await reader.ReadToEndAsync(), CancellationToken.None);
            foreach (var child in source.Children.Select(c => c.GetLeftPart(UriPartial.Path)))
            {
                _children.Add(child);
                foreach (var listed in Media.Where(m => m.Url.GetLeftPart(UriPartial.Path) == child).ToList())
                    Media.Remove(listed);
            }
            return item with
            {
                Size = await owner.Fetcher.EstimateSizeAsync(item, source, CancellationToken.None),
                SizeIsEstimate = true,
                Resolution = source.Variant?.Resolution,
                Duration = source.Video.IsLive ? null : source.Video.Duration,
            };
        }
        // Best effort: a playlist that can't be read or parsed is still listed, just without the details.
        catch (Exception)
        {
            return item;
        }
    }

    static string? GetHeader(IEnumerable<KeyValuePair<string, string>> headers, string name) =>
        headers.FirstOrDefault(h => string.Equals(h.Key, name, StringComparison.OrdinalIgnoreCase)).Value;

    void Set<T>(ref T field, T value, [CallerMemberName] string name = "")
    {
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
