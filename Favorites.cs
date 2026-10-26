using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Media;

namespace VideoCatcher;

sealed class Favorite
{
    ImageSource? _image;

    public required string Title { get; init; }
    public required string Url { get; init; }

    // The site's favicon (base64 PNG) captured when the page was added, so the bar shows it without loading the page.
    public string? Icon { get; init; }

    [JsonIgnore]
    public ImageSource? Image => _image ??= Icon is null ? null : BrowserTab.ToImage(Convert.FromBase64String(Icon));
}

static class Favorites
{
    static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "VideoCatcher", "favorites.json");

    public static ObservableCollection<Favorite> Items { get; } = File.Exists(FilePath)
        ? new(JsonSerializer.Deserialize<List<Favorite>>(File.ReadAllText(FilePath))!)
        : [];

    public static Favorite? Find(string url) => Items.FirstOrDefault(f => f.Url == url);

    public static void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(Items));
    }
}
