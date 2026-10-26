using System.Globalization;
using System.Text.RegularExpressions;

namespace VideoCatcher;

sealed record HlsVariant(Uri Url, long Bandwidth, long? AverageBandwidth, string? Resolution, Uri? AudioUrl);

sealed record HlsKey(Uri Url, byte[]? Iv);

sealed record HlsMap(Uri Url, long? Offset, long? Length);

sealed record HlsSegment(Uri Url, double Duration, long? Offset, long? Length, long Sequence, HlsKey? Key, HlsMap? Map);

// What a captured playlist resolves to: the media playlist to download (a master's best variant), its separate
// audio if any, and every playlist the master references, so those aren't listed as videos of their own.
sealed record HlsSource(HlsPlaylist Video, HlsPlaylist? Audio, HlsVariant? Variant, IReadOnlyList<Uri> Children);

// Just enough of RFC 8216 to download: variants and audio renditions of a master playlist; segments with byte
// ranges, AES-128 keys and fMP4 init sections of a media playlist.
sealed class HlsPlaylist
{
    static readonly Regex AttributeRegex = new(@"([A-Z0-9-]+)=(""[^""]*""|[^,]*)");

    public List<HlsVariant> Variants { get; } = [];

    // Variant and rendition playlists a master references.
    public List<Uri> Children { get; } = [];

    public List<HlsSegment> Segments { get; } = [];

    public bool IsMaster => Variants.Count > 0;

    // Without EXT-X-ENDLIST the playlist keeps growing; those are left to ffmpeg.
    public bool IsLive { get; private set; } = true;

    // SAMPLE-AES and DRM key systems; left to ffmpeg, which handles or rejects them.
    public bool HasUnsupportedEncryption { get; private set; }

    public bool HasMap => Segments.Any(s => s.Map is not null);

    public TimeSpan Duration => TimeSpan.FromSeconds(Segments.Sum(s => s.Duration));

    // Exact size when every segment is a byte range.
    public long? ByteSize => Segments.Count > 0 && Segments.All(s => s.Length is not null)
        ? Segments.Sum(s => s.Length!.Value)
        : null;

    public static HlsPlaylist Parse(string text, Uri baseUrl)
    {
        var playlist = new HlsPlaylist();
        var variants = new List<(Uri Url, Dictionary<string, string> Attributes)>();
        var audioGroups = new Dictionary<string, Uri>();
        Dictionary<string, string>? streamInf = null;
        double duration = 0;
        long? rangeLength = null, rangeOffset = null;
        long nextOffset = 0; // a byte range without @offset continues where the previous one ended
        long sequence = 0;
        HlsKey? key = null;
        HlsMap? map = null;

        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;

            if (!line.StartsWith('#'))
            {
                var url = new Uri(baseUrl, line);
                if (streamInf is not null)
                {
                    variants.Add((url, streamInf));
                    playlist.Children.Add(url);
                    streamInf = null;
                    continue;
                }
                long? offset = null;
                if (rangeLength is { } length)
                {
                    offset = rangeOffset ?? nextOffset;
                    nextOffset = offset.Value + length;
                }
                playlist.Segments.Add(new HlsSegment(url, duration, offset, rangeLength, sequence++, key, map));
                duration = 0;
                rangeLength = rangeOffset = null;
                continue;
            }

            var colon = line.IndexOf(':');
            var tag = colon > 0 ? line[..colon] : line;
            var value = colon > 0 ? line[(colon + 1)..] : "";
            switch (tag)
            {
                case "#EXT-X-STREAM-INF":
                    streamInf = Attributes(value);
                    break;
                case "#EXT-X-MEDIA":
                    var media = Attributes(value);
                    if (!media.TryGetValue("URI", out var mediaUri)) break;
                    var mediaUrl = new Uri(baseUrl, mediaUri);
                    playlist.Children.Add(mediaUrl);
                    if (media.GetValueOrDefault("TYPE") == "AUDIO" && media.TryGetValue("GROUP-ID", out var group)
                        && (!audioGroups.ContainsKey(group) || media.GetValueOrDefault("DEFAULT") == "YES"))
                        audioGroups[group] = mediaUrl;
                    break;
                case "#EXT-X-MEDIA-SEQUENCE":
                    sequence = long.Parse(value, CultureInfo.InvariantCulture);
                    break;
                case "#EXTINF":
                    duration = double.Parse(value.Split(',')[0], CultureInfo.InvariantCulture);
                    break;
                case "#EXT-X-BYTERANGE":
                    (rangeLength, rangeOffset) = ByteRange(value);
                    break;
                case "#EXT-X-KEY":
                    var keyAttributes = Attributes(value);
                    var method = keyAttributes.GetValueOrDefault("METHOD", "NONE");
                    key = method == "AES-128"
                        ? new HlsKey(new Uri(baseUrl, keyAttributes["URI"]),
                            keyAttributes.TryGetValue("IV", out var iv) ? Convert.FromHexString(iv[2..]) : null)
                        : null;
                    if (method is not ("NONE" or "AES-128")) playlist.HasUnsupportedEncryption = true;
                    break;
                case "#EXT-X-MAP":
                    var mapAttributes = Attributes(value);
                    var (mapLength, mapOffset) = mapAttributes.TryGetValue("BYTERANGE", out var mapRange)
                        ? ByteRange(mapRange)
                        : (null, null);
                    map = new HlsMap(new Uri(baseUrl, mapAttributes["URI"]),
                        mapLength is null ? null : mapOffset ?? 0, mapLength);
                    break;
                case "#EXT-X-ENDLIST":
                    playlist.IsLive = false;
                    break;
            }
        }

        // Renditions may be declared after the variants that use them, so variants are built last.
        foreach (var (url, attributes) in variants)
        {
            playlist.Variants.Add(new HlsVariant(
                url,
                long.Parse(attributes.GetValueOrDefault("BANDWIDTH", "0"), CultureInfo.InvariantCulture),
                attributes.TryGetValue("AVERAGE-BANDWIDTH", out var average)
                    ? long.Parse(average, CultureInfo.InvariantCulture)
                    : null,
                attributes.GetValueOrDefault("RESOLUTION"),
                attributes.TryGetValue("AUDIO", out var audio) ? audioGroups.GetValueOrDefault(audio) : null));
        }
        return playlist;
    }

    // Last occurrence wins, so a malformed attribute list doesn't throw on duplicate names.
    static Dictionary<string, string> Attributes(string value)
    {
        var attributes = new Dictionary<string, string>();
        foreach (Match match in AttributeRegex.Matches(value))
            attributes[match.Groups[1].Value] = match.Groups[2].Value.Trim('"');
        return attributes;
    }

    // "length[@offset]"
    static (long? Length, long? Offset) ByteRange(string value)
    {
        var parts = value.Split('@');
        return (long.Parse(parts[0], CultureInfo.InvariantCulture),
            parts.Length > 1 ? long.Parse(parts[1], CultureInfo.InvariantCulture) : null);
    }
}
