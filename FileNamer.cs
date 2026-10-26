using System.IO;
using System.Text.RegularExpressions;

namespace VideoCatcher;

static class FileNamer
{
    const int MaxNameLength = 150;

    // Suffixes sites append to the title that og:site_name doesn't cover.
    static readonly string[] KnownSuffixes = ["_哔哩哔哩_bilibili", " - YouTube"];

    // Windows rejects these as file names regardless of extension (CON, NUL.txt, ...).
    static readonly HashSet<string> ReservedNames = new(
        ["CON", "PRN", "AUX", "NUL", .. Enumerable.Range(1, 9).SelectMany(i => new[] { $"COM{i}", $"LPT{i}" })],
        StringComparer.OrdinalIgnoreCase);

    // Returns a full path in folder that isTaken rejects for neither the plain nor a " (n)" variant.
    public static string Build(MediaItem item, string folder, string ext, Func<string, bool> isTaken)
    {
        var name = Sanitize(StripSiteSuffix(item.Title.Trim(), item.Site.Trim()));
        if (name.Length == 0) name = $"{item.PageUrl.Host}_{DateTime.Now:yyyyMMdd_HHmmss}";

        var path = Path.Combine(folder, name + ext);
        for (var i = 1; isTaken(path); i++) path = Path.Combine(folder, $"{name} ({i}){ext}");
        return path;
    }

    static string StripSiteSuffix(string title, string site)
    {
        foreach (var suffix in KnownSuffixes)
            if (title.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) return title[..^suffix.Length];

        // "Title - Site", "Title | Site", "Title_Site". A bare site name (home page) has no separator and is kept.
        return site.Length == 0
            ? title
            : Regex.Replace(title, $@"(?<=\S)\s*[-_|–—]+\s*{Regex.Escape(site)}$", "", RegexOptions.IgnoreCase);
    }

    static string Sanitize(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var result = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim();
        if (result.Length > MaxNameLength)
            result = result[..(char.IsHighSurrogate(result[MaxNameLength - 1]) ? MaxNameLength - 1 : MaxNameLength)];
        result = result.TrimEnd('.', ' ');
        return ReservedNames.Contains(result.Split('.')[0]) ? "_" + result : result;
    }
}
