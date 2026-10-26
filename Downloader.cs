using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace VideoCatcher;

sealed class Downloader(ProxyRelay relay, MediaFetcher fetcher)
{
    const int MaxConcurrent = 3;

    // PotPlayer plays these containers as-is; everything else (m3u8, mpd, m4s, ...) is remuxed to mp4.
    static readonly string[] KeepExts = [".mp4", ".mkv", ".webm", ".flv", ".ts", ".mov", ".avi", ".wmv", ".m4v"];

    static readonly string Ffmpeg = File.Exists(Path.Combine(AppContext.BaseDirectory, "ffmpeg.exe"))
        ? Path.Combine(AppContext.BaseDirectory, "ffmpeg.exe")
        : "ffmpeg";

    static readonly Regex DurationRegex = new(@"Duration: (\d+:\d+:\d+\.\d+)");

    // Finished downloads, newest first, so they're still listed after a restart.
    static readonly string HistoryPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "VideoCatcher", "downloads.json");

    readonly SemaphoreSlim _slots = new(MaxConcurrent);

    // Target paths of running downloads; the files don't exist on disk until they finish.
    readonly HashSet<string> _reserved = new(StringComparer.OrdinalIgnoreCase);

    public ObservableCollection<DownloadTask> Tasks { get; } = File.Exists(HistoryPath)
        ? new(JsonSerializer.Deserialize<List<DownloadRecord>>(File.ReadAllText(HistoryPath))!
            .Select(record => new DownloadTask(record)))
        : [];

    public bool HasActive => Tasks.Any(t => t.IsActive);

    public event Action? ActiveChanged;

    public void Start(MediaItem item)
    {
        var task = new DownloadTask(item);
        Tasks.Insert(0, task);
        _ = RunAsync(task);
    }

    public void CancelAll()
    {
        foreach (var task in Tasks) task.Cts.Cancel();
    }

    // Only removes the list entry; the downloaded file stays on disk.
    public void Remove(DownloadTask task)
    {
        Tasks.Remove(task);
        if (task.Record is not null) SaveHistory();
    }

    void SaveHistory()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(HistoryPath)!);
        File.WriteAllText(HistoryPath, JsonSerializer.Serialize(Tasks.Select(t => t.Record).OfType<DownloadRecord>()));
    }

    async Task RunAsync(DownloadTask task)
    {
        SetActive(task, true);
        // Next to the target so the final move stays on one drive; removed whatever happens.
        var workDir = Path.Combine(Settings.Current.SaveFolder, $".videocatcher-{Guid.NewGuid():N}");
        try
        {
            await _slots.WaitAsync(task.Cts.Token);
            try
            {
                var item = task.Item!;
                var ext = OutputExt(item);
                task.Status = "下载中";
                var inputs = await FetchAsync(task, workDir);
                if (item.Kind is MediaKind.Video or MediaKind.Audio && inputs is [var local]
                    && string.Equals(Path.GetExtension(local), ext, StringComparison.OrdinalIgnoreCase))
                {
                    // Already a container PotPlayer plays; nothing to remux.
                    var target = Reserve(task, ext);
                    try
                    {
                        File.Move(local, target);
                        Complete(task, target);
                    }
                    finally
                    {
                        _reserved.Remove(target);
                    }
                    return;
                }
                // -c copy fails when the container can't hold the codec (e.g. mp4 + some subtitle/audio formats);
                // mkv accepts nearly everything and PotPlayer plays it.
                if (!await RemuxAsync(task, inputs, ext) && ext != ".mkv" && !task.Cts.IsCancellationRequested)
                    await RemuxAsync(task, inputs, ".mkv");
            }
            finally
            {
                _slots.Release();
            }
        }
        catch (OperationCanceledException)
        {
            task.Status = "已取消";
        }
        catch (Exception e)
        {
            task.Status = $"失败：{e.Message}";
        }
        finally
        {
            if (Directory.Exists(workDir)) Directory.Delete(workDir, recursive: true);
            SetActive(task, false);
        }
    }

    // Local copies fetched over parallel connections, or null when ffmpeg has to read the URL itself:
    // live HLS, DASH, encryption other than AES-128, and servers without byte ranges.
    // The fetches run on the thread pool; Progress<T> still reports back on the UI thread.
    async Task<string[]?> FetchAsync(DownloadTask task, string workDir)
    {
        var item = task.Item!;
        var ct = task.Cts.Token;
        var watch = Stopwatch.StartNew();
        long bytes = 0;

        if (item.Kind is MediaKind.Video or MediaKind.Audio)
        {
            if (await fetcher.RangedLengthAsync(item, ct) is not { } length) return null;
            var path = Path.Combine(CreateWorkDir(workDir), "source" + Path.GetExtension(item.Url.AbsolutePath));
            var progress = new Progress<(long Bytes, int Segments)>(p =>
            {
                bytes += p.Bytes;
                task.Progress = bytes * 100.0 / length;
                var received = $"{MediaItem.FormatSize(bytes)} / {MediaItem.FormatSize(length)}";
                task.Status = $"下载中 {received} · {Speed(bytes, watch)}";
            });
            await Task.Run(() => fetcher.DownloadRangesAsync(item, length, path, progress, ct), ct);
            return [path];
        }

        if (item.Kind != MediaKind.Hls) return null;
        var source = await fetcher.LoadHlsAsync(item, null, ct);
        (string Name, HlsPlaylist Playlist)[] playlists = source.Audio is { } audio
            ? [("video", source.Video), ("audio", audio)]
            : [("video", source.Video)];
        if (playlists.Any(p => p.Playlist.IsLive || p.Playlist.HasUnsupportedEncryption)) return null;

        var total = playlists.Sum(p => p.Playlist.Segments.Count);
        var done = 0;
        var segmentProgress = new Progress<(long Bytes, int Segments)>(p =>
        {
            bytes += p.Bytes;
            done += p.Segments;
            task.Progress = done * 100.0 / total;
            task.Status = $"下载中 {done}/{total} 片 · {Speed(bytes, watch)}";
        });
        var files = new List<string>();
        foreach (var (name, playlist) in playlists)
        {
            var path = Path.Combine(CreateWorkDir(workDir), name + SegmentExt(playlist));
            await Task.Run(() => fetcher.DownloadPlaylistAsync(item, playlist, path, segmentProgress, ct), ct);
            files.Add(path);
        }
        return [.. files];
    }

    // inputs == null: ffmpeg downloads the captured URL itself; otherwise it only remuxes local files.
    async Task<bool> RemuxAsync(DownloadTask task, string[]? inputs, string ext)
    {
        var target = Reserve(task, ext);
        var part = Path.ChangeExtension(target, ".part" + ext);
        var label = inputs is null ? "下载中" : "合并中";
        task.Progress = 0;
        task.Status = label;
        try
        {
            var psi = new ProcessStartInfo(Ffmpeg)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardErrorEncoding = Encoding.UTF8,
            };
            foreach (var arg in Arguments(task.Item!, inputs, part)) psi.ArgumentList.Add(arg);

            using var process = Process.Start(psi)!;
            using var cancel = task.Cts.Token.Register(() =>
            {
                if (!process.HasExited) process.Kill();
            });

            var duration = TimeSpan.Zero;
            var lastError = "";
            async Task ReadErrorsAsync()
            {
                while (await process.StandardError.ReadLineAsync() is { } line)
                {
                    if (DurationRegex.Match(line) is { Success: true } match)
                        duration = TimeSpan.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
                    else if (line.Length > 0)
                        lastError = line.Trim();
                }
            }

            var errors = ReadErrorsAsync();
            while (await process.StandardOutput.ReadLineAsync() is { } line)
            {
                if (line.StartsWith("out_time_us=") && long.TryParse(line[12..], out var us) && duration > TimeSpan.Zero)
                    task.Progress = Math.Min(100, us / 10.0 / duration.TotalMilliseconds);
                else if (line.StartsWith("total_size=") && long.TryParse(line[11..], out var size))
                    task.Status = $"{label} {MediaItem.FormatSize(size)}";
            }
            await errors;
            await process.WaitForExitAsync();

            if (process.ExitCode == 0)
            {
                File.Move(part, target);
                Complete(task, target);
                return true;
            }

            File.Delete(part);
            task.Status = task.Cts.IsCancellationRequested ? "已取消" : $"失败：{lastError}";
            return false;
        }
        finally
        {
            _reserved.Remove(target);
        }
    }

    string Reserve(DownloadTask task, string ext)
    {
        var target = FileNamer.Build(task.Item!.Name, Settings.Current.SaveFolder, ext,
            path => _reserved.Contains(path) || File.Exists(path));
        _reserved.Add(target);
        task.FileName = Path.GetFileName(target);
        return target;
    }

    void Complete(DownloadTask task, string target)
    {
        task.Complete(new DownloadRecord(target, new FileInfo(target).Length, DateTime.Now));
        SaveHistory();
    }

    void SetActive(DownloadTask task, bool active)
    {
        task.IsActive = active;
        ActiveChanged?.Invoke();
    }

    static string CreateWorkDir(string workDir)
    {
        Directory.CreateDirectory(workDir).Attributes |= FileAttributes.Hidden;
        return workDir;
    }

    static string OutputExt(MediaItem item)
    {
        if (item.Kind == MediaKind.Audio) return ".m4a";
        var ext = Path.GetExtension(item.Url.AbsolutePath).ToLowerInvariant();
        return item.Kind == MediaKind.Video && KeepExts.Contains(ext) ? ext : ".mp4";
    }

    // The extension only steers ffmpeg's probing: fMP4 segments join into an MP4, packed-audio renditions keep
    // their audio format, everything else is MPEG-TS.
    static string SegmentExt(HlsPlaylist playlist)
    {
        if (playlist.HasMap) return ".mp4";
        var ext = Path.GetExtension(playlist.Segments.FirstOrDefault()?.Url.AbsolutePath ?? "").ToLowerInvariant();
        return ext is ".aac" or ".mp3" or ".ac3" or ".ec3" ? ext : ".ts";
    }

    static string Speed(long bytes, Stopwatch watch) =>
        MediaItem.FormatSize((long)(bytes / Math.Max(watch.Elapsed.TotalSeconds, 0.001))) + "/s";

    IEnumerable<string> Arguments(MediaItem item, string[]? inputs, string output)
    {
        // With separate video and audio files, ffmpeg's default stream selection takes the video from one and the
        // audio from the other.
        string[] input = inputs is null
            ?
            [
                // ffmpeg's hls/dash demuxers forward http_proxy, headers and user_agent to every segment request.
                "-http_proxy", $"http://127.0.0.1:{relay.Port}",
                "-user_agent", item.UserAgent,
                "-headers",
                $"Referer: {item.Referer}\r\n" + (item.Cookie.Length > 0 ? $"Cookie: {item.Cookie}\r\n" : ""),
                "-i", item.Url.AbsoluteUri,
            ]
            : [.. inputs.SelectMany(path => new[] { "-i", path })];
        return ["-hide_banner", "-nostdin", "-y", .. input, "-c", "copy", "-progress", "pipe:1", "-nostats", output];
    }
}

sealed record DownloadRecord(string FilePath, long Size, DateTime CompletedAt);

sealed class DownloadTask : INotifyPropertyChanged
{
    string _fileName;
    string _status;
    double _progress;
    bool _isActive;
    DownloadRecord? _record;

    public DownloadTask(MediaItem item)
    {
        Item = item;
        _fileName = item.Name;
        _status = "排队中";
    }

    public DownloadTask(DownloadRecord record)
    {
        _record = record;
        _fileName = Path.GetFileName(record.FilePath);
        _status = File.Exists(record.FilePath) ? CompletedStatus(record) : "文件已移除";
    }

    // Null for entries restored from history; those never run.
    public MediaItem? Item { get; }

    public CancellationTokenSource Cts { get; } = new();

    // Drives the cancel button and progress bar; finished items show neither.
    public bool IsActive
    {
        get => _isActive;
        set
        {
            Set(ref _isActive, value);
            Raise(nameof(IsIdle));
        }
    }

    // Enables "remove from list", which makes no sense mid-download.
    public bool IsIdle => !_isActive;

    // Set once the file is on disk; opening and showing in folder need it.
    public DownloadRecord? Record
    {
        get => _record;
        private set
        {
            Set(ref _record, value);
            Raise(nameof(IsCompleted));
        }
    }

    public bool IsCompleted => _record is not null;

    public string FileName { get => _fileName; set => Set(ref _fileName, value); }

    public string Status { get => _status; set => Set(ref _status, value); }

    public double Progress { get => _progress; set => Set(ref _progress, value); }

    public event PropertyChangedEventHandler? PropertyChanged;

    public void Complete(DownloadRecord record)
    {
        Record = record;
        Progress = 100;
        Status = CompletedStatus(record);
    }

    static string CompletedStatus(DownloadRecord record) =>
        $"完成 · {MediaItem.FormatSize(record.Size)} · {record.CompletedAt:MM-dd HH:mm}";

    void Set<T>(ref T field, T value, [CallerMemberName] string name = "")
    {
        field = value;
        Raise(name);
    }

    void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
