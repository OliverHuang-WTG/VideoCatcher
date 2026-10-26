using System.IO;

namespace VideoCatcher;

enum MediaKind { Manifest, Video, Audio }

sealed class MediaItem
{
    public required Uri Url { get; init; }
    public required MediaKind Kind { get; init; }
    public long? Size { get; init; }
    public required string Referer { get; init; }
    public required string UserAgent { get; init; }
    public required string Cookie { get; init; }
    public required string Title { get; init; }
    public required string Site { get; init; }
    public required Uri PageUrl { get; init; }

    public string TypeText => Path.GetExtension(Url.AbsolutePath) is { Length: > 1 } ext ? ext[1..] : Kind.ToString();

    public string SizeText => Size is { } size ? FormatSize(size) : "";

    public static string FormatSize(long bytes) => bytes >= 1L << 30
        ? $"{bytes / (double)(1L << 30):0.00} GB"
        : $"{bytes / (double)(1L << 20):0.0} MB";
}

static class MediaSniffer
{
    static readonly string[] VideoExts = [".mp4", ".flv", ".webm", ".mkv", ".mov", ".m4v", ".avi", ".wmv", ".ts", ".m4s"];

    // HLS/DASH fragments. Once the page's manifest is captured they are noise; without one (e.g. bilibili's
    // whole-file .m4s streams) they are the only thing to download.
    static readonly string[] SegmentExts = [".ts", ".m4s"];

    public static MediaKind? Classify(Uri url, string? contentType, long? size, long minSize, bool hasManifest)
    {
        var ext = Path.GetExtension(url.AbsolutePath).ToLowerInvariant();
        var type = contentType?.ToLowerInvariant() ?? "";
        if (ext is ".m3u8" or ".mpd" || type.Contains("mpegurl") || type.Contains("dash+xml"))
            return MediaKind.Manifest;
        if (hasManifest && (SegmentExts.Contains(ext) || type.Contains("mp2t")))
            return null;
        // Live streams are usually chunked with no length, so an unknown size passes.
        if (size < minSize)
            return null;
        if (type.StartsWith("video/") || VideoExts.Contains(ext))
            return MediaKind.Video;
        if (type.StartsWith("audio/"))
            return MediaKind.Audio;
        return null;
    }

    // "bytes 0-1023/52428800" gives the full size even for a small range probe.
    public static long? TotalSize(string? contentRange, string? contentLength)
    {
        var slash = contentRange?.LastIndexOf('/') ?? -1;
        if (slash >= 0 && long.TryParse(contentRange![(slash + 1)..], out var total)) return total;
        return long.TryParse(contentLength, out var length) ? length : null;
    }
}
