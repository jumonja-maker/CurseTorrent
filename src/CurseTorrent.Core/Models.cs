namespace CurseTorrent.Core;

public enum DownloadState { Stopped, Waiting, Metadata, Checking, Downloading, Seeding, Paused, Stopping, Error }
public enum FilePriority { Skip, Low, Normal, High }

public sealed record AppSettings
{
    public string DownloadDirectory { get; init; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "CurseTorrent");
    public int DownloadLimitKiB { get; init; }
    public int UploadLimitKiB { get; init; }
    public int ListenPort { get; init; } = 55123;
    public int MaximumConnections { get; init; } = 150;
    public bool ResumeOnStartup { get; init; } = true;
    public bool CloseToTray { get; init; } = true;
    public bool EnablePortForwarding { get; init; }
}

public sealed record TorrentFilePreview(string Path, long SizeBytes);
public sealed record TorrentPreview(string Source, string Name, long SizeBytes, bool IsMagnet, IReadOnlyList<TorrentFilePreview> Files);
public sealed record AddTorrentRequest(string Source, string SaveDirectory, bool StartImmediately = true, IReadOnlyDictionary<string, FilePriority>? FilePriorities = null, bool AllowExistingFiles = false);
public sealed record TorrentFileSnapshot(string Path, long SizeBytes, double Progress, FilePriority Priority);
public sealed record TorrentSnapshot(
    string Id, string Name, string SaveDirectory, DownloadState State, double Progress,
    long SizeBytes, long SelectedSizeBytes, long DownloadedBytes, long UploadedBytes,
    long DownloadBytesPerSecond, long UploadBytesPerSecond, int ConnectedPeers, int Seeds,
    TimeSpan? EstimatedRemaining, string? ErrorMessage, bool HasMetadata,
    IReadOnlyList<TorrentFileSnapshot> Files);

public interface ITorrentService : IAsyncDisposable
{
    AppSettings Settings { get; }
    IReadOnlyList<string> StartupWarnings { get; }
    Task InitializeAsync();
    Task<TorrentPreview> InspectAsync(string source);
    Task<string> AddAsync(AddTorrentRequest request);
    IReadOnlyList<TorrentSnapshot> GetSnapshots(string? includeFilesForId = null);
    Task ResumeAsync(string id);
    Task PauseAsync(string id);
    Task RecheckAsync(string id);
    Task RemoveAsync(string id);
    Task SetFilePriorityAsync(string id, string path, FilePriority priority);
    Task UpdateSettingsAsync(AppSettings settings);
    Task ShutdownAsync();
}
