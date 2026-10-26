using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace VideoCatcher;

sealed class Settings
{
    static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "VideoCatcher", "settings.json");

    public static Settings Current { get; } = File.Exists(FilePath)
        ? JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath))!
        : new Settings();

    public string SaveFolder { get; set; } = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);

    // Empty = direct; otherwise http://[user:pass@]host:port or socks5://[user:pass@]host:port.
    public string Proxy { get; set; } = "";

    // Toolbar switch; keeps Proxy so turning it back on needs no retyping.
    public bool ProxyEnabled { get; set; } = true;

    public int MinSizeMB { get; set; } = 1;

    [JsonIgnore]
    public Uri? UpstreamProxy => ProxyEnabled && Proxy.Length > 0 ? new Uri(Proxy) : null;

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }
}
