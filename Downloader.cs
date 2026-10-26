using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;

namespace VideoCatcher;

sealed class Downloader(ProxyRelay relay)
{
    const int MaxConcurrent = 3;

    // PotPlayer plays these containers as-is; everything else (m3u8, mpd, m4s, ...) is remuxed to mp4.
    static readonly string[] KeepExts = [".mp4", ".mkv", ".webm", ".flv", ".ts", ".mov", ".avi", ".wmv", ".m4v"];

    static readonly string Ffmpeg = File.Exists(Path.Combine(AppContext.BaseDirectory, "ffmpeg.exe"))
        ? Path.Combine(AppContext.BaseDirectory, "ffmpeg.exe")
        : "ffmpeg";

    static readonly Regex DurationRegex = new(@"Duration: (\d+:\d+:\d+\.\d+)");

    readonly SemaphoreSlim _slots = new(MaxConcurrent);

    // Target paths of running ffmpeg processes; the files don't exist on disk until they finish.
    readonly HashSet<string> _reserved = new(StringComparer.OrdinalIgnoreCase);

    public ObservableCollection<DownloadTask> Tasks { get; } = [];

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

    async Task RunAsync(DownloadTask task)
    {
        SetActive(task, true);
        try
        {
            await _slots.WaitAsync(task.Cts.Token);
            try
            {
                var ext = OutputExt(task.Item);
                // -c copy fails when the container can't hold the codec (e.g. mp4 + some subtitle/audio formats);
                // mkv accepts nearly everything and PotPlayer plays it.
                if (!await TryRunAsync(task, ext) && ext != ".mkv" && !task.Cts.IsCancellationRequested)
                    await TryRunAsync(task, ".mkv");
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
            SetActive(task, false);
        }
    }

    void SetActive(DownloadTask task, bool active)
    {
        task.IsActive = active;
        ActiveChanged?.Invoke();
    }

    async Task<bool> TryRunAsync(DownloadTask task, string ext)
    {
        var target = FileNamer.Build(task.Item.Name, Settings.Current.SaveFolder, ext,
            path => _reserved.Contains(path) || File.Exists(path));
        var part = Path.ChangeExtension(target, ".part" + ext);
        _reserved.Add(target);
        task.FileName = Path.GetFileName(target);
        task.Progress = 0;
        task.Status = "下载中";
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
            foreach (var arg in Arguments(task.Item, part)) psi.ArgumentList.Add(arg);

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
                else if (line.StartsWith("total_size=") && long.TryParse(line[11..], out var bytes))
                    task.Status = $"下载中 {MediaItem.FormatSize(bytes)}";
            }
            await errors;
            await process.WaitForExitAsync();

            if (process.ExitCode == 0)
            {
                File.Move(part, target);
                task.FilePath = target;
                task.Progress = 100;
                task.Status = "完成";
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

    static string OutputExt(MediaItem item)
    {
        if (item.Kind == MediaKind.Audio) return ".m4a";
        var ext = Path.GetExtension(item.Url.AbsolutePath).ToLowerInvariant();
        return item.Kind == MediaKind.Video && KeepExts.Contains(ext) ? ext : ".mp4";
    }

    // ffmpeg's hls/dash demuxers forward http_proxy, headers and user_agent to every segment request.
    IEnumerable<string> Arguments(MediaItem item, string output)
    {
        var headers = $"Referer: {item.Referer}\r\n" + (item.Cookie.Length > 0 ? $"Cookie: {item.Cookie}\r\n" : "");
        return
        [
            "-hide_banner", "-nostdin", "-y",
            "-http_proxy", $"http://127.0.0.1:{relay.Port}",
            "-user_agent", item.UserAgent,
            "-headers", headers,
            "-i", item.Url.AbsoluteUri,
            "-c", "copy",
            "-progress", "pipe:1", "-nostats",
            output,
        ];
    }
}

sealed class DownloadTask(MediaItem item) : INotifyPropertyChanged
{
    string _fileName = item.Name;
    string _status = "排队中";
    double _progress;
    bool _isActive;

    public MediaItem Item { get; } = item;

    public CancellationTokenSource Cts { get; } = new();

    // Drives the cancel button and progress bar; finished items show neither.
    public bool IsActive { get => _isActive; set => Set(ref _isActive, value); }

    // Set once the file is complete; double-clicking opens it.
    public string? FilePath { get; set; }

    public string FileName { get => _fileName; set => Set(ref _fileName, value); }

    public string Status { get => _status; set => Set(ref _status, value); }

    public double Progress { get => _progress; set => Set(ref _progress, value); }

    public event PropertyChangedEventHandler? PropertyChanged;

    void Set<T>(ref T field, T value, [CallerMemberName] string name = "")
    {
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
