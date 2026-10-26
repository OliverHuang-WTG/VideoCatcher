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

    public int MinSizeMB { get; set; } = 1;

    [JsonIgnore]
    public Uri? ProxyUri => Proxy.Length == 0 ? null : new Uri(Proxy);

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }
}
