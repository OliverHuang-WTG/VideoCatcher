using System.IO;
using System.Net.Http.Headers;
using System.Security.Cryptography;

namespace VideoCatcher;

static class FileNamer
{
    const int MaxNameLength = 150;

    // Names CDNs and players give every stream; keeping them would turn every download into "index (n)".
    static readonly HashSet<string> GenericNames = new(
        ["index", "master", "playlist", "manifest", "chunklist", "prog_index", "videoplayback"],
        StringComparer.OrdinalIgnoreCase);

    // Windows rejects these as file names regardless of extension (CON, NUL.txt, ...).
    static readonly HashSet<string> ReservedNames = new(
        ["CON", "PRN", "AUX", "NUL", .. Enumerable.Range(1, 9).SelectMany(i => new[] { $"COM{i}", $"LPT{i}" })],
        StringComparer.OrdinalIgnoreCase);

    // Base name (no extension) from the server's Content-Disposition, else the last URL path segment,
    // else 8 random letters.
    public static string SourceName(Uri url, string? contentDisposition)
    {
        var fileName = ContentDispositionHeaderValue.TryParse(contentDisposition, out var header)
            ? header.FileNameStar ?? header.FileName?.Trim('"')
            : null;
        if (string.IsNullOrEmpty(fileName)) fileName = Uri.UnescapeDataString(url.AbsolutePath.Split('/')[^1]);

        var name = Sanitize(Path.GetFileNameWithoutExtension(fileName));
        return name.Length == 0 || GenericNames.Contains(name)
            ? RandomNumberGenerator.GetString("abcdefghijklmnopqrstuvwxyz", 8)
            : name;
    }

    // Returns a full path in folder that isTaken rejects for neither the plain nor a " (n)" variant.
    public static string Build(string name, string folder, string ext, Func<string, bool> isTaken)
    {
        var path = Path.Combine(folder, name + ext);
        for (var i = 1; isTaken(path); i++) path = Path.Combine(folder, $"{name} ({i}){ext}");
        return path;
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
