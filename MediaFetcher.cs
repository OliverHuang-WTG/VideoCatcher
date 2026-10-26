using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;

namespace VideoCatcher;

// The HTTP side of downloading, over several connections at once and through the proxy relay so the toolbar switch
// applies. ffmpeg's own HTTP input fetches one segment at a time, which is what made downloads slow.
sealed class MediaFetcher(ProxyRelay relay)
{
    const int Connections = 8;
    const int Attempts = 5;

    // Per request, and for ranged downloads per read, so a stalled connection is retried instead of hanging.
    static readonly TimeSpan StallTimeout = TimeSpan.FromSeconds(60);

    readonly HttpClient _http = new(new SocketsHttpHandler
    {
        Proxy = new WebProxy($"http://127.0.0.1:{relay.Port}"),
        UseCookies = false, // the captured Cookie header is sent as is
        AutomaticDecompression = DecompressionMethods.All,
    })
    {
        Timeout = Timeout.InfiniteTimeSpan,
    };

    // text: the playlist body when the browser already has it; otherwise it's fetched.
    public async Task<HlsSource> LoadHlsAsync(MediaItem item, string? text, CancellationToken ct)
    {
        var playlist = HlsPlaylist.Parse(text ?? await GetStringAsync(item, item.Url, ct), item.Url);
        if (!playlist.IsMaster) return new HlsSource(playlist, null, null, []);

        var variant = playlist.Variants.MaxBy(v => v.Bandwidth)!;
        var video = HlsPlaylist.Parse(await GetStringAsync(item, variant.Url, ct), variant.Url);
        var audio = variant.AudioUrl is { } audioUrl
            ? HlsPlaylist.Parse(await GetStringAsync(item, audioUrl, ct), audioUrl)
            : null;
        return new HlsSource(video, audio, variant, playlist.Children);
    }

    // Exact when every segment is a byte range; otherwise bitrate × duration, or the first segment's size ×
    // segment count for a playlist that came without a bitrate.
    public async Task<long?> EstimateSizeAsync(MediaItem item, HlsSource source, CancellationToken ct)
    {
        if (source.Video.IsLive) return null;
        if (source.Video.ByteSize is { } exact) return exact + (source.Audio?.ByteSize ?? 0);
        if (source.Variant is { } variant)
            return (long)((variant.AverageBandwidth ?? variant.Bandwidth) * source.Video.Duration.TotalSeconds / 8);
        if (source.Video.Segments.FirstOrDefault() is not { } first) return null;
        return (await ProbeAsync(item, first.Url, ct)).Length * source.Video.Segments.Count;
    }

    // Total length when the server serves byte ranges, which DownloadRangesAsync needs.
    public async Task<long?> RangedLengthAsync(MediaItem item, CancellationToken ct) =>
        await ProbeAsync(item, item.Url, ct) is (true, { } length) ? length : null;

    // Segments download Connections at a time into numbered files, then concatenate in order: TS segments form a
    // valid TS stream, and an fMP4 init section followed by its fragments a valid fragmented MP4.
    public async Task DownloadPlaylistAsync(MediaItem item, HlsPlaylist playlist, string output,
        IProgress<(long Bytes, int Segments)> progress, CancellationToken ct)
    {
        var segments = playlist.Segments;
        var parts = Directory.CreateDirectory(output + ".parts").FullName;
        var keys = new ConcurrentDictionary<Uri, Task<byte[]>>();
        await Parallel.ForEachAsync(Enumerable.Range(0, segments.Count),
            new ParallelOptions { MaxDegreeOfParallelism = Connections, CancellationToken = ct },
            async (i, token) =>
            {
                var segment = segments[i];
                var data = await GetBytesAsync(item, segment.Url, segment.Offset, segment.Length, token);
                if (segment.Key is { } key)
                {
                    using var aes = Aes.Create();
                    aes.Key = await keys.GetOrAdd(key.Url, url => GetBytesAsync(item, url, null, null, token));
                    data = aes.DecryptCbc(data, key.Iv ?? SequenceIv(segment.Sequence));
                }
                await File.WriteAllBytesAsync(Path.Combine(parts, i.ToString()), data, token);
                progress.Report((data.Length, 1));
            });

        await using var file = File.Create(output);
        HlsMap? written = null;
        for (var i = 0; i < segments.Count; i++)
        {
            if (segments[i].Map is { } map && map != written)
            {
                await file.WriteAsync(await GetBytesAsync(item, map.Url, map.Offset, map.Length, ct), ct);
                written = map;
            }
            await using var part = File.OpenRead(Path.Combine(parts, i.ToString()));
            await part.CopyToAsync(file, ct);
        }
    }

    // The file is preallocated and each connection writes its own slice in place, resuming where it stopped
    // when a connection drops.
    public async Task DownloadRangesAsync(MediaItem item, long length, string output,
        IProgress<(long Bytes, int Segments)> progress, CancellationToken ct)
    {
        await using (var file = File.Create(output)) file.SetLength(length);
        var slice = (length + Connections - 1) / Connections;
        await Parallel.ForEachAsync(Enumerable.Range(0, Connections),
            new ParallelOptions { MaxDegreeOfParallelism = Connections, CancellationToken = ct },
            async (i, token) =>
            {
                var position = i * slice;
                var end = Math.Min(length, position + slice);
                for (var attempt = 1; position < end; attempt++)
                {
                    try
                    {
                        using var stall = CancellationTokenSource.CreateLinkedTokenSource(token);
                        stall.CancelAfter(StallTimeout);
                        using var response = await _http.SendAsync(Request(item, item.Url, position, end - position),
                            HttpCompletionOption.ResponseHeadersRead, stall.Token);
                        // A server that answers a range with the whole file would overwrite the other slices.
                        if (response.StatusCode != HttpStatusCode.PartialContent)
                            throw new HttpRequestException($"Range request returned {(int)response.StatusCode}.", null,
                                response.StatusCode);
                        await using var stream = await response.Content.ReadAsStreamAsync(stall.Token);
                        await using var file =
                            new FileStream(output, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
                        file.Position = position;
                        var buffer = new byte[256 * 1024];
                        int read;
                        while (position < end && (read = await stream.ReadAsync(buffer, stall.Token)) > 0)
                        {
                            await file.WriteAsync(buffer.AsMemory(0, read), token);
                            position += read;
                            progress.Report((read, 0));
                            stall.CancelAfter(StallTimeout);
                        }
                        if (position < end) throw new IOException("Connection closed before the range was complete.");
                    }
                    catch (Exception e) when (IsTransient(e, token) && attempt < Attempts)
                    {
                        await Task.Delay(TimeSpan.FromSeconds(attempt), token);
                    }
                }
            });
    }

    async Task<(bool Ranges, long? Length)> ProbeAsync(MediaItem item, Uri url, CancellationToken ct)
    {
        using var response =
            await _http.SendAsync(Request(item, url, 0, 1), HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        return response.StatusCode == HttpStatusCode.PartialContent
            ? (true, response.Content.Headers.ContentRange?.Length)
            : (false, response.Content.Headers.ContentLength);
    }

    async Task<string> GetStringAsync(MediaItem item, Uri url, CancellationToken ct) =>
        Encoding.UTF8.GetString(await GetBytesAsync(item, url, null, null, ct));

    async Task<byte[]> GetBytesAsync(MediaItem item, Uri url, long? offset, long? length, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                using var stall = CancellationTokenSource.CreateLinkedTokenSource(ct);
                stall.CancelAfter(StallTimeout);
                using var response = await _http.SendAsync(Request(item, url, offset, length), stall.Token);
                response.EnsureSuccessStatusCode();
                return await response.Content.ReadAsByteArrayAsync(stall.Token);
            }
            catch (Exception e) when (IsTransient(e, ct) && attempt < Attempts)
            {
                await Task.Delay(TimeSpan.FromSeconds(attempt), ct);
            }
        }
    }

    // Same headers the browser sent, so segment hosts that check Referer or cookies accept us.
    static HttpRequestMessage Request(MediaItem item, Uri url, long? offset, long? length)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("User-Agent", item.UserAgent);
        request.Headers.TryAddWithoutValidation("Referer", item.Referer);
        if (item.Cookie.Length > 0) request.Headers.TryAddWithoutValidation("Cookie", item.Cookie);
        if (offset is { } from) request.Headers.Range = new RangeHeaderValue(from, from + length - 1);
        return request;
    }

    // Client errors won't change on retry; dropped connections, timeouts and server errors might.
    static bool IsTransient(Exception e, CancellationToken userCancel) => !userCancel.IsCancellationRequested && e switch
    {
        HttpRequestException { StatusCode: { } status } =>
            status >= HttpStatusCode.InternalServerError || status == HttpStatusCode.TooManyRequests,
        HttpRequestException or IOException or OperationCanceledException => true,
        _ => false,
    };

    // Without an explicit IV, HLS uses the segment's media sequence number as a 128-bit big-endian integer.
    static byte[] SequenceIv(long sequence)
    {
        var iv = new byte[16];
        BinaryPrimitives.WriteInt64BigEndian(iv.AsSpan(8), sequence);
        return iv;
    }
}
