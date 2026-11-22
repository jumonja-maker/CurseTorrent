using System.Net;
using System.Runtime.CompilerServices;
using MonoTorrent;
using MonoTorrent.BEncoding;
using MonoTorrent.Client;
using MonoTorrent.PieceWriter;

[assembly: InternalsVisibleTo("CurseTorrent.Tests")]

namespace CurseTorrent.Core;

/// <summary>Owns the engine and serializes user commands; the UI only receives immutable snapshots.</summary>
public sealed class TorrentService : ITorrentService
{
    private sealed class SessionDocument
    {
        public int Version { get; set; } = 1;
        public List<SavedTorrent> Torrents { get; set; } = [];
    }

    private sealed class SavedTorrent
    {
        public string Id { get; set; } = "";
        public string Source { get; set; } = "";
        public string Name { get; set; } = "";
        public string SaveDirectory { get; set; } = "";
        public bool ShouldRun { get; set; }
        public Dictionary<string, FilePriority> Priorities { get; set; } = [];
        public long UploadedBytes { get; set; }
    }

    private sealed class Entry(SavedTorrent saved)
    {
        public SavedTorrent Saved { get; } = saved;
        public TorrentManager? Manager { get; set; }
        public string? Error { get; set; }
        public bool Removed { get; set; }
        public long UploadBaseline { get; set; } = saved.UploadedBytes;
        public Task? HashOperation { get; set; }
        public Task? Recheck { get; set; }
        public MagnetLink? PendingMagnet { get; set; }
        public CancellationTokenSource? MetadataCancellation { get; set; }
        public Task<ReadOnlyMemory<byte>>? MetadataOperation { get; set; }
    }

    private readonly string _dataDirectory;
    private readonly bool _offlineMode;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _entriesLock = new();
    private readonly object _pathLock = new();
    private readonly Dictionary<string, (Entry Owner, ITorrentManagerFile File)> _reservedPaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<ITorrentManagerFile> _approvedFiles = new(ReferenceEqualityComparer.Instance);
    private readonly List<Entry> _entries = [];
    private readonly List<string> _warnings = [];
    private readonly List<Task> _backgroundTasks = [];
    private readonly CancellationTokenSource _lifetime = new();
    private ClientEngine? _engine;
    private FileStream? _instanceLock;
    private bool _initialized;
    private bool _stopping;
    private bool _disposed;
    private string SessionPath => Path.Combine(_dataDirectory, "session.json");
    private string SettingsPath => Path.Combine(_dataDirectory, "settings.json");
    public AppSettings Settings { get; private set; } = new();
    public IReadOnlyList<string> StartupWarnings => _warnings.AsReadOnly();

    public TorrentService(string? dataDirectory = null)
        : this(dataDirectory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CurseTorrent"), false) { }

    public TorrentService(string dataDirectory, bool offlineMode)
    {
        _dataDirectory = Path.GetFullPath(dataDirectory);
        _offlineMode = offlineMode;
    }

    public async Task InitializeAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_initialized) return;
            ObjectDisposedException.ThrowIf(_stopping || _disposed, this);
            Directory.CreateDirectory(_dataDirectory);
            _instanceLock = new FileStream(Path.Combine(_dataDirectory, "session.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            try
            {
                Settings = await AtomicJsonFile.ReadAsync<AppSettings>(SettingsPath, _warnings).ConfigureAwait(false) ?? new();
                try { Settings = ValidateSettings(Settings); }
                catch (ArgumentException ex) { _warnings.Add($"Настройки сброшены: {ex.Message}"); Settings = new(); }
                var document = await AtomicJsonFile.ReadAsync<SessionDocument>(SessionPath, _warnings).ConfigureAwait(false) ?? new();
                if (document.Version != 1) throw new InvalidDataException("Эта версия CurseTorrent не поддерживает формат сохранённой сессии. Файлы оставлены без изменений.");
                if (document.Torrents is null || document.Torrents.Count > 10000) throw new InvalidDataException("Недопустимый список загрузок в сессии.");
                _engine = new ClientEngine(BuildEngineSettings(Settings), Factories.Default.WithPieceWriterCreator(
                    maximumFiles => new GuardedPieceWriter(new DiskWriter(maximumFiles), ValidatePayloadAccess)));
                foreach (var saved in document.Torrents)
                {
                    if (saved is null || string.IsNullOrWhiteSpace(saved.Id) || CopyEntries().Any(x => x.Saved.Id == saved.Id))
                    { _warnings.Add("Пропущена повреждённая или повторяющаяся запись сессии."); continue; }
                    saved.Priorities ??= [];
                    var entry = new Entry(saved);
                    lock (_entriesLock) _entries.Add(entry);
                    try { await LoadManagerAsync(entry).ConfigureAwait(false); }
                    catch (Exception ex) { entry.Error = ex.Message; _warnings.Add($"{saved.Name}: {ex.Message}"); }
                    if (!Settings.ResumeOnStartup) saved.ShouldRun = false;
                }
                _initialized = true;
                foreach (var entry in CopyEntries().Where(x => (x.Manager != null || x.PendingMagnet != null) && x.Error == null && x.Saved.ShouldRun))
                {
                    try { await StartEntryAsync(entry).ConfigureAwait(false); }
                    catch (Exception ex) { entry.Error = ex.Message; _warnings.Add($"{entry.Saved.Name}: {ex.Message}"); }
                }
                _backgroundTasks.Add(CheckpointLoopAsync());
            }
            catch
            {
                _engine?.Dispose(); _engine = null;
                _instanceLock.Dispose(); _instanceLock = null;
                throw;
            }
        }
        finally { _gate.Release(); }
    }

    public async Task<TorrentPreview> InspectAsync(string source)
    {
        source = TorrentInput.Normalize(source);
        if (TorrentInput.IsMagnet(source))
        {
            var magnet = MagnetLink.Parse(source);
            return new(source, magnet.Name ?? magnet.InfoHashes.V1OrV2.ToHex(), magnet.Size ?? 0, true, []);
        }
        TorrentInput.CheckMetadataFile(source);
        var torrent = await Torrent.LoadAsync(source).ConfigureAwait(false);
        TorrentInput.ValidateMetadata(torrent);
        return new(source, torrent.Name, torrent.Size, false, torrent.Files.Select(x => new TorrentFilePreview(x.Path, x.Length)).ToArray());
    }

    public async Task<string> AddAsync(AddTorrentRequest request)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            RequireReady();
            var preview = await InspectAsync(request.Source).ConfigureAwait(false);
            string destination = ValidateDestination(request.SaveDirectory);
            var priorities = request.FilePriorities?.ToDictionary(x => x.Key, x => x.Value) ?? [];
            if (priorities.Values.Any(p => !Enum.IsDefined(p))) throw new ArgumentException("Недопустимый приоритет файла.");
            if (!preview.IsMagnet && preview.Files.All(x => priorities.GetValueOrDefault(x.Path, FilePriority.Normal) == FilePriority.Skip))
                throw new ArgumentException("Выберите хотя бы один файл для загрузки.");
            string source = preview.Source;
            InfoHashes hashes;
            if (preview.IsMagnet) hashes = MagnetLink.Parse(source).InfoHashes;
            else hashes = (await Torrent.LoadAsync(source).ConfigureAwait(false)).InfoHashes;
            if (CopyEntries().Any(x => x.Saved.Id == hashes.V1?.ToHex() || x.Saved.Id == hashes.V2?.ToHex() ||
                (x.Manager != null && HashesOverlap(x.Manager.InfoHashes, hashes)) ||
                (x.PendingMagnet != null && HashesOverlap(x.PendingMagnet.InfoHashes, hashes))))
                throw new InvalidOperationException("Эта раздача уже добавлена в CurseTorrent.");
            if (!preview.IsMagnet)
            {
                string imports = Path.Combine(_dataDirectory, "torrents");
                Directory.CreateDirectory(imports);
                string imported = Path.Combine(imports, hashes.V1OrV2.ToHex() + ".torrent");
                // Store our own immutable source: moving/deleting the original must not break resume.
                if (!Path.GetFullPath(source).Equals(imported, StringComparison.OrdinalIgnoreCase))
                {
                    byte[] data = await File.ReadAllBytesAsync(source).ConfigureAwait(false);
                    if (data.Length > TorrentInput.MaximumMetadataBytes) throw new ArgumentException("Torrent-файл слишком велик.");
                    var stable = Torrent.Load(data);
                    TorrentInput.ValidateMetadata(stable);
                    if (stable.InfoHashes != hashes) throw new IOException("Torrent-файл изменился во время добавления. Повторите попытку.");
                    string temp = imported + ".tmp";
                    await File.WriteAllBytesAsync(temp, data).ConfigureAwait(false);
                    File.Move(temp, imported, true);
                }
                source = imported;
            }
            Directory.CreateDirectory(destination);
            var entry = new Entry(new SavedTorrent
            {
                Id = hashes.V1OrV2.ToHex(), Source = source, Name = preview.Name,
                SaveDirectory = destination, ShouldRun = request.StartImmediately, Priorities = priorities
            });
            lock (_entriesLock) _entries.Add(entry);
            try
            {
                await LoadManagerAsync(entry).ConfigureAwait(false);
                await SaveSessionAsync().ConfigureAwait(false);
            }
            catch
            {
                lock (_entriesLock) _entries.Remove(entry);
                entry.Removed = true;
                if (entry.Manager != null) await _engine!.RemoveAsync(entry.Manager).ConfigureAwait(false);
                ReleasePaths(entry);
                throw;
            }
            if (request.StartImmediately)
            {
                try { await StartEntryAsync(entry).ConfigureAwait(false); }
                catch (Exception ex) { entry.Error = ex.Message; throw; }
            }
            return entry.Saved.Id;
        }
        finally { _gate.Release(); }
    }

    private async Task LoadManagerAsync(Entry entry)
    {
        string source = TorrentInput.Normalize(entry.Saved.Source);
        string destination = ValidateDestination(entry.Saved.SaveDirectory);
        Torrent? metadata = null;
        if (TorrentInput.IsMagnet(source))
        {
            var magnet = MagnetLink.Parse(source);
            if (!(magnet.InfoHashes.V1?.ToHex() == entry.Saved.Id || magnet.InfoHashes.V2?.ToHex() == entry.Saved.Id))
                throw new InvalidDataException("Хеш сохранённой magnet-ссылки не совпадает с записью сессии.");
            string cached = Path.Combine(_dataDirectory, "torrents", entry.Saved.Id + ".torrent");
            if (!File.Exists(cached))
            {
                // Resolve metadata without starting payload IO. In MonoTorrent 3.0.2
                // creation of zero-length files bypasses IPieceWriter, so a normal
                // magnet manager must never start before complete path validation.
                entry.PendingMagnet = magnet;
                return;
            }
            source = cached;
        }
        TorrentInput.CheckMetadataFile(source);
        metadata = await Torrent.LoadAsync(source).ConfigureAwait(false);
        TorrentInput.ValidateMetadata(metadata);
        var hashes = metadata.InfoHashes;
        if (!(hashes.V1?.ToHex() == entry.Saved.Id || hashes.V2?.ToHex() == entry.Saved.Id))
            throw new InvalidDataException("Хеш сохранённой раздачи не совпадает с torrent-файлом.");
        var torrentSettings = new TorrentSettingsBuilder
        {
            CreateContainingDirectory = true,
            AllowDht = !_offlineMode,
            AllowPeerExchange = !_offlineMode,
            MaximumConnections = Math.Min(Settings.MaximumConnections, 100)
        }.ToSettings();
        var manager = await _engine!.AddAsync(source, destination, torrentSettings).ConfigureAwait(false);
        entry.Manager = manager;
        try
        {
            await ApplyMetadataAsync(entry).ConfigureAwait(false);
            entry.Saved.Source = source;
            entry.PendingMagnet = null;
        }
        catch
        {
            await _engine!.RemoveAsync(manager).ConfigureAwait(false);
            ReleasePaths(entry);
            entry.Manager = null;
            throw;
        }
    }

    private async Task ApplyMetadataAsync(Entry entry)
    {
        var manager = entry.Manager!;
        ValidateAndReservePaths(entry);
        foreach (var file in manager.Files)
        {
            if (entry.Saved.Priorities.TryGetValue(file.Path, out var priority))
            {
                if (!Enum.IsDefined(priority)) throw new InvalidDataException("Недопустимый приоритет в сессии.");
                await manager.SetFilePriorityAsync(file, ToEnginePriority(priority)).ConfigureAwait(false);
            }
        }
        entry.Saved.Name = manager.Name;
    }

    private async Task ObserveMetadataDownloadAsync(Entry entry, Task<ReadOnlyMemory<byte>> operation)
    {
        try
        {
            ReadOnlyMemory<byte> metadata = await operation.ConfigureAwait(false);
            await _gate.WaitAsync(_lifetime.Token).ConfigureAwait(false);
            try
            {
                if (_stopping || entry.Removed || entry.MetadataOperation != operation || entry.MetadataCancellation!.IsCancellationRequested) return;
                try
                {
                    if (metadata.Length > TorrentInput.MaximumMetadataBytes) throw new InvalidDataException("Метаданные раздачи превышают 16 МиБ.");
                    byte[] bytes = metadata.ToArray();
                    var torrent = Torrent.Load(bytes);
                    TorrentInput.ValidateMetadata(torrent);
                    // Metadata exchange carries the info dictionary. Keep web seeds from
                    // the original magnet when converting it into our durable .torrent.
                    if (entry.PendingMagnet!.Webseeds.Count > 0)
                    {
                        var dictionary = BEncodedValue.Decode<BEncodedDictionary>(bytes);
                        dictionary["url-list"] = new BEncodedList(entry.PendingMagnet.Webseeds.Select(x => (BEncodedValue)new BEncodedString(x)));
                        bytes = dictionary.Encode();
                    }
                    string imports = Path.Combine(_dataDirectory, "torrents");
                    Directory.CreateDirectory(imports);
                    string imported = Path.Combine(imports, entry.Saved.Id + ".torrent");
                    string temp = imported + ".tmp";
                    await File.WriteAllBytesAsync(temp, bytes).ConfigureAwait(false);
                    File.Move(temp, imported, true);
                    await LoadManagerAsync(entry).ConfigureAwait(false);
                    await SaveSessionAsync().ConfigureAwait(false);
                    if (entry.Saved.ShouldRun) await StartEntryAsync(entry).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    entry.Error = ex.Message;
                    entry.Saved.ShouldRun = false;
                    await StopEntryAsync(entry).ConfigureAwait(false);
                    await SaveSessionAsync().ConfigureAwait(false);
                }
            }
            finally { _gate.Release(); }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (entry.MetadataOperation == operation && !entry.Removed) entry.Error = ex.Message;
            Log(ex);
        }
    }

    public IReadOnlyList<TorrentSnapshot> GetSnapshots(string? includeFilesForId = null)
    {
        return CopyEntries().Select(entry =>
        {
            var saved = entry.Saved;
            var manager = entry.Manager;
            if (manager == null)
                return new TorrentSnapshot(saved.Id, saved.Name, saved.SaveDirectory,
                    entry.Error != null ? DownloadState.Error : saved.ShouldRun ? DownloadState.Metadata : DownloadState.Paused, 0, 0, 0, 0,
                    saved.UploadedBytes, 0, 0, 0, 0, null, entry.Error, false, []);
            bool metadata = manager.HasMetadata;
            long selectedBytes = metadata ? manager.Files.Where(x => x.Priority != Priority.DoNotDownload).Sum(x => x.Length) : 0;
            double progress = metadata ? manager.PartialProgress : 0;
            long down = manager.Monitor.DownloadRate;
            long completed = metadata ? manager.Files.Where(x => x.Priority != Priority.DoNotDownload).Sum(x => x.BytesDownloaded()) : 0;
            TimeSpan? remaining = down > 0 && selectedBytes > completed
                ? TimeSpan.FromSeconds(Math.Min((selectedBytes - completed) / (double)down, TimeSpan.MaxValue.TotalSeconds - 1)) : null;
            string? error = entry.Error ?? manager.Error?.Exception.Message;
            var files = saved.Id == includeFilesForId && metadata
                ? manager.Files.Select(x => new TorrentFileSnapshot(x.Path, x.Length, x.BitField.PercentComplete, FromEnginePriority(x.Priority))).ToArray()
                : Array.Empty<TorrentFileSnapshot>();
            return new TorrentSnapshot(saved.Id, manager.Name, metadata ? manager.ContainingDirectory : saved.SaveDirectory,
                error != null ? DownloadState.Error : entry.HashOperation is { IsCompleted: false } && manager.State == TorrentState.Stopped
                    ? DownloadState.Checking : MapState(manager.State, saved.ShouldRun),
                progress, manager.Torrent?.Size ?? 0, selectedBytes, completed,
                entry.UploadBaseline + manager.Monitor.DataBytesSent, down, manager.Monitor.UploadRate,
                manager.OpenConnections, manager.Peers.Seeds, remaining, error, metadata, files);
        }).ToArray();
    }

    public async Task ResumeAsync(string id)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            RequireReady(); var entry = FindEntry(id);
            if (entry.Manager == null && entry.PendingMagnet == null) await LoadManagerAsync(entry).ConfigureAwait(false);
            if (entry.Manager?.HasMetadata == true)
            {
                await ApplyMetadataAsync(entry).ConfigureAwait(false);
                if (entry.Manager.Files.All(x => x.Priority == Priority.DoNotDownload)) throw new InvalidOperationException("Сначала выберите файлы для загрузки.");
            }
            entry.Error = null;
            entry.Saved.ShouldRun = true;
            await SaveSessionAsync().ConfigureAwait(false);
            if (entry.HashOperation is not { IsCompleted: false }) await StartEntryAsync(entry).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    public async Task PauseAsync(string id)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            RequireReady(); var entry = FindEntry(id);
            entry.Saved.ShouldRun = false;
            await StopEntryAsync(entry).ConfigureAwait(false);
            await SaveSessionAsync().ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    public async Task RecheckAsync(string id)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            RequireReady(); var entry = FindEntry(id);
            if (entry.Manager?.HasMetadata != true) throw new InvalidOperationException("Сначала дождитесь получения списка файлов.");
            if (entry.HashOperation is { IsCompleted: false }) return;
            await StopEntryAsync(entry).ConfigureAwait(false);
            // 3.0.2 keeps HashChecked=true across an explicit recheck unless it is reset.
            // Without this, an interrupted check can write inconsistent fast-resume data.
            await entry.Manager.SetNeedsHashCheckAsync().ConfigureAwait(false);
            entry.Error = null;
            entry.HashOperation = entry.Manager.HashCheckAsync(autoStart: false);
            entry.Recheck = ObserveRecheckAsync(entry, entry.HashOperation);
            _backgroundTasks.Add(entry.Recheck);
        }
        finally { _gate.Release(); }
    }

    private async Task ObserveRecheckAsync(Entry entry, Task hashOperation)
    {
        try
        {
            await hashOperation.ConfigureAwait(false);
            await _gate.WaitAsync(_lifetime.Token).ConfigureAwait(false);
            try
            {
                if (_stopping || entry.Removed || entry.HashOperation != hashOperation) return;
                if (entry.Saved.ShouldRun && entry.Manager!.State != TorrentState.Error) await StartEntryAsync(entry).ConfigureAwait(false);
                await SaveSessionAsync().ConfigureAwait(false);
            }
            finally { _gate.Release(); }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception ex) { entry.Error = ex.Message; Log(ex); }
    }

    public async Task RemoveAsync(string id)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            RequireReady(); var entry = FindEntry(id);
            await StopEntryAsync(entry).ConfigureAwait(false);
            // Write the new manifest before unregistering; failures leave a recoverable paused task.
            entry.Removed = true;
            try { await SaveSessionAsync().ConfigureAwait(false); }
            catch { entry.Removed = false; entry.Saved.ShouldRun = false; throw; }
            if (entry.Manager != null) await _engine!.RemoveAsync(entry.Manager).ConfigureAwait(false);
            ReleasePaths(entry);
            lock (_entriesLock) _entries.Remove(entry);
            // Source metadata and resume cache are harmless and intentionally retained. Payload is never deleted.
        }
        finally { _gate.Release(); }
    }

    public async Task SetFilePriorityAsync(string id, string path, FilePriority priority)
    {
        if (!Enum.IsDefined(priority)) throw new ArgumentException("Недопустимый приоритет файла.");
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            RequireReady(); var entry = FindEntry(id);
            if (entry.Manager?.HasMetadata != true) throw new InvalidOperationException("Метаданные ещё не получены.");
            var file = entry.Manager.Files.FirstOrDefault(x => x.Path == path) ?? throw new ArgumentException("Файл не найден в раздаче.");
            await entry.Manager.SetFilePriorityAsync(file, ToEnginePriority(priority)).ConfigureAwait(false);
            entry.Saved.Priorities[path] = priority;
            if (entry.Manager.Files.All(x => x.Priority == Priority.DoNotDownload))
            {
                entry.Saved.ShouldRun = false;
                await StopEntryAsync(entry).ConfigureAwait(false);
            }
            await SaveSessionAsync().ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    public async Task UpdateSettingsAsync(AppSettings settings)
    {
        settings = ValidateSettings(settings);
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            RequireReady();
            var old = Settings;
            await _engine!.UpdateSettingsAsync(BuildEngineSettings(settings)).ConfigureAwait(false);
            try { await AtomicJsonFile.WriteAsync(SettingsPath, settings).ConfigureAwait(false); }
            catch { await _engine.UpdateSettingsAsync(BuildEngineSettings(old)).ConfigureAwait(false); throw; }
            Settings = settings;
            foreach (var entry in CopyEntries().Where(x => x.Manager != null))
                await entry.Manager!.UpdateSettingsAsync(new TorrentSettingsBuilder(entry.Manager.Settings)
                    { MaximumConnections = Math.Min(settings.MaximumConnections, 100) }.ToSettings()).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    internal async Task AddPeerAsync(string id, Uri endpoint)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            RequireReady();
            var manager = FindEntry(id).Manager ?? throw new InvalidOperationException("Метаданные ещё не получены; используйте трекер для поиска пиров.");
            await manager.AddPeerAsync(new PeerInfo(endpoint)).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    private async Task StartEntryAsync(Entry entry)
    {
        if (entry.Manager == null)
        {
            if (entry.PendingMagnet == null) throw new InvalidOperationException("Не удалось открыть источник раздачи.");
            if (entry.MetadataOperation is { IsCompleted: false })
            {
                if (!entry.MetadataCancellation!.IsCancellationRequested) return;
                try { await entry.MetadataOperation.ConfigureAwait(false); }
                catch (OperationCanceledException) { }
            }
            entry.MetadataCancellation?.Dispose();
            entry.MetadataCancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            entry.MetadataOperation = _engine!.DownloadMetadataAsync(entry.PendingMagnet, entry.MetadataCancellation.Token);
            _backgroundTasks.Add(ObserveMetadataDownloadAsync(entry, entry.MetadataOperation));
            return;
        }
        if (entry.Manager.State is TorrentState.Starting or TorrentState.Downloading or TorrentState.Seeding or TorrentState.Metadata or TorrentState.FetchingHashes or TorrentState.Hashing) return;
        if (entry.Manager.State == TorrentState.Error) await StopEntryAsync(entry).ConfigureAwait(false);
        await entry.Manager.StartAsync().ConfigureAwait(false);
    }

    private static async Task StopEntryAsync(Entry entry)
    {
        if (entry.Manager == null && entry.MetadataCancellation != null)
        {
            await entry.MetadataCancellation.CancelAsync().ConfigureAwait(false);
            if (entry.MetadataOperation != null)
                try { await entry.MetadataOperation.ConfigureAwait(false); }
                catch (OperationCanceledException) { }
                catch (Exception ex) { entry.Error ??= ex.Message; }
        }
        if (entry.Manager != null && (entry.Manager.State != TorrentState.Stopped || entry.HashOperation is { IsCompleted: false }))
        {
            // MonoTorrent resets its transfer monitor during Stop. Preserve the observed
            // payload total without counting it twice in states which do not reset it.
            long before = entry.Manager.Monitor.DataBytesSent;
            await entry.Manager.StopAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            // Always enqueue Stop after a newly queued hash check, even before its state
            // becomes Hashing. Await only the raw engine task, never the gated observer.
            if (entry.HashOperation != null) await entry.HashOperation.ConfigureAwait(false);
            long after = entry.Manager.Monitor.DataBytesSent;
            if (after < before) entry.UploadBaseline += before - after;
        }
    }

    private async Task SaveSessionAsync()
    {
        var entries = CopyEntries().Where(x => !x.Removed).ToArray();
        foreach (var entry in entries)
            if (entry.Manager != null) entry.Saved.UploadedBytes = entry.UploadBaseline + entry.Manager.Monitor.DataBytesSent;
        await AtomicJsonFile.WriteAsync(SessionPath, new SessionDocument { Torrents = entries.Select(x => x.Saved).ToList() }).ConfigureAwait(false);
    }

    private async Task CheckpointLoopAsync()
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
            while (await timer.WaitForNextTickAsync(_lifetime.Token).ConfigureAwait(false))
            {
                await _gate.WaitAsync(_lifetime.Token).ConfigureAwait(false);
                try { if (!_stopping) await SaveSessionAsync().ConfigureAwait(false); }
                catch (Exception ex) { Log(ex); }
                finally { _gate.Release(); }
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
    }

    public async Task ShutdownAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        Exception? failure = null;
        try
        {
            if (_disposed) return;
            _stopping = true;
            await _lifetime.CancelAsync().ConfigureAwait(false);
            if (_engine != null)
            {
                // Stop in parallel; one slow tracker must not delay every other torrent.
                var stops = CopyEntries().Select(async x =>
                {
                    try { await StopEntryAsync(x).ConfigureAwait(false); }
                    catch (Exception ex) { Log(ex); Interlocked.CompareExchange(ref failure, ex, null); }
                });
                await Task.WhenAll(stops).ConfigureAwait(false);
                try { await SaveSessionAsync().ConfigureAwait(false); }
                catch (Exception ex) { failure ??= ex; Log(ex); }
                _engine.Dispose(); _engine = null;
            }
            _instanceLock?.Dispose(); _instanceLock = null;
            _disposed = true;
        }
        finally { _gate.Release(); }
        await Task.WhenAll(_backgroundTasks).ConfigureAwait(false);
        if (failure != null) throw new IOException("Не удалось полностью сохранить сессию. Скачанные файлы остаются на диске; при запуске может потребоваться перепроверка.", failure);
    }

    public async ValueTask DisposeAsync() => await ShutdownAsync().ConfigureAwait(false);

    private EngineSettings BuildEngineSettings(AppSettings settings) => new EngineSettingsBuilder
    {
        CacheDirectory = Path.Combine(_dataDirectory, "cache"),
        AutoSaveLoadDhtCache = !_offlineMode,
        AutoSaveLoadFastResume = true,
        AutoSaveLoadMagnetLinkMetadata = true,
        FastResumeMode = FastResumeMode.Accurate,
        AllowLocalPeerDiscovery = !_offlineMode,
        AllowPortForwarding = !_offlineMode && settings.EnablePortForwarding,
        MaximumConnections = settings.MaximumConnections,
        MaximumHalfOpenConnections = 8,
        MaximumOpenFiles = 64,
        DiskCacheBytes = 8 * 1024 * 1024,
        MaximumDownloadRate = checked(settings.DownloadLimitKiB * 1024),
        MaximumUploadRate = checked(settings.UploadLimitKiB * 1024),
        DhtEndPoint = _offlineMode ? null : new IPEndPoint(IPAddress.Any, settings.ListenPort),
        ListenEndPoints = new Dictionary<string, IPEndPoint>
        {
            ["ipv4"] = new(_offlineMode ? IPAddress.Loopback : IPAddress.Any, _offlineMode ? 0 : settings.ListenPort)
        },
        UsePartialFiles = true
    }.ToSettings();

    private static AppSettings ValidateSettings(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (settings.DownloadLimitKiB is < 0 or > 2097151 || settings.UploadLimitKiB is < 0 or > 2097151)
            throw new ArgumentException("Ограничения скорости: от 0 до 2 097 151 КиБ/с; 0 — без ограничения.");
        if (settings.ListenPort is < 0 or > 65535 || settings.ListenPort is > 0 and < 1024)
            throw new ArgumentException("Порт: 0 (автоматически) или от 1024 до 65535.");
        if (settings.MaximumConnections is < 10 or > 2000) throw new ArgumentException("Количество соединений: от 10 до 2000.");
        return settings with { DownloadDirectory = TorrentInput.NormalizeDirectory(settings.DownloadDirectory) };
    }

    private string ValidateDestination(string directory)
    {
        directory = TorrentInput.NormalizeDirectory(directory);
        string normalized = Path.TrimEndingDirectorySeparator(directory);
        string state = Path.TrimEndingDirectorySeparator(_dataDirectory);
        if (normalized.Equals(state, StringComparison.OrdinalIgnoreCase) || normalized.StartsWith(state + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Выберите папку загрузки вне служебной папки CurseTorrent.");
        return directory;
    }

    private static void RejectLinkedDescendants(string root, string path)
    {
        string relative = Path.GetRelativePath(root, path);
        string current = root;
        foreach (string segment in relative.Split(Path.DirectorySeparatorChar))
        {
            current = Path.Combine(current, segment);
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("В пути загрузки обнаружена символическая ссылка. Выберите отдельную папку.");
        }
    }

    private void ValidateAndReservePaths(Entry entry)
    {
        lock (_pathLock)
        {
            var manager = entry.Manager ?? throw new IOException("Раздача ещё не готова к записи.");
            TorrentInput.ValidateMetadata(manager.Torrent!);
            string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(entry.Saved.SaveDirectory)) + Path.DirectorySeparatorChar;
            string stateRoot = Path.TrimEndingDirectorySeparator(_dataDirectory) + Path.DirectorySeparatorChar;
            var candidates = new Dictionary<string, ITorrentManagerFile>(StringComparer.OrdinalIgnoreCase);
            foreach (var file in manager.Files)
            {
                foreach (string candidate in new[] { file.DownloadCompleteFullPath, file.DownloadIncompleteFullPath }.Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    string path = Path.GetFullPath(candidate);
                    if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new IOException("Путь файла выходит за пределы папки загрузки.");
                    if (path.StartsWith(stateRoot, StringComparison.OrdinalIgnoreCase) || path.Equals(Path.TrimEndingDirectorySeparator(_dataDirectory), StringComparison.OrdinalIgnoreCase))
                        throw new IOException("Файлы раздачи не могут заменять служебные файлы CurseTorrent.");
                    RejectLinkedDescendants(root, path);
                    if (candidates.TryGetValue(path, out var otherFile) && !ReferenceEquals(file, otherFile))
                        throw new IOException("Имена готовых и временных файлов этой раздачи пересекаются.");
                    if (_reservedPaths.TryGetValue(path, out var reserved) && !ReferenceEquals(reserved.File, file))
                        throw new IOException($"Другая раздача уже использует файл: {file.Path}. Выберите другую папку.");
                    candidates[path] = file;
                }
            }
            // Commit all reservations together after the complete metadata passes validation.
            foreach (var (path, file) in candidates) _reservedPaths[path] = (entry, file);
            foreach (var file in manager.Files) _approvedFiles.Add(file);
        }
    }

    private void ValidatePayloadAccess(ITorrentManagerFile file, string? targetPath)
    {
        lock (_pathLock)
        {
            if (!_approvedFiles.Contains(file))
            {
                var entries = CopyEntries();
                var entry = entries.FirstOrDefault(x => !x.Removed && x.Manager?.Files.Any(f => ReferenceEquals(f, file)) == true);
                if (entry == null)
                {
                    // Fast-resume loading can queue a partial-file rename inside AddAsync,
                    // before that method returns its manager to us. Entries are pre-registered
                    // and the engine's public manager is already visible at this point.
                    var manager = _engine?.Torrents.FirstOrDefault(x => x.Files.Any(f => ReferenceEquals(f, file)));
                    entry = manager == null ? null : entries.FirstOrDefault(x => !x.Removed &&
                        (x.Saved.Id == manager.InfoHashes.V1?.ToHex() || x.Saved.Id == manager.InfoHashes.V2?.ToHex()));
                    if (entry == null) throw new IOException("Запись незарегистрированного файла запрещена.");
                    entry.Manager = manager;
                }
                ValidateAndReservePaths(entry);
            }
            string path = Path.GetFullPath(targetPath ?? file.FullPath);
            if (!_reservedPaths.TryGetValue(path, out var reserved) || !ReferenceEquals(reserved.File, file))
                throw new IOException("Путь файла не принадлежит этой раздаче.");
        }
    }

    private void ReleasePaths(Entry entry)
    {
        lock (_pathLock)
        {
            foreach (string path in _reservedPaths.Where(x => ReferenceEquals(x.Value.Owner, entry)).Select(x => x.Key).ToArray())
            {
                _approvedFiles.Remove(_reservedPaths[path].File);
                _reservedPaths.Remove(path);
            }
        }
    }

    private Entry[] CopyEntries() { lock (_entriesLock) return _entries.ToArray(); }
    private Entry FindEntry(string id) => CopyEntries().FirstOrDefault(x => x.Saved.Id == id && !x.Removed) ?? throw new KeyNotFoundException("Загрузка уже удалена.");
    private void RequireReady() { ObjectDisposedException.ThrowIf(_stopping || _disposed, this); if (!_initialized) throw new InvalidOperationException("Клиент ещё запускается."); }
    private static bool HashesOverlap(InfoHashes a, InfoHashes b) => (a.V1 != null && b.Contains(a.V1)) || (a.V2 != null && b.Contains(a.V2));
    private static Priority ToEnginePriority(FilePriority p) => p switch { FilePriority.Skip => Priority.DoNotDownload, FilePriority.Low => Priority.Low, FilePriority.High => Priority.High, _ => Priority.Normal };
    private static FilePriority FromEnginePriority(Priority p) => p switch { Priority.DoNotDownload => FilePriority.Skip, Priority.Low => FilePriority.Low, Priority.High or Priority.Highest or Priority.Immediate => FilePriority.High, _ => FilePriority.Normal };
    private static DownloadState MapState(TorrentState s, bool running) => s switch
    {
        TorrentState.Downloading => DownloadState.Downloading, TorrentState.Seeding => DownloadState.Seeding,
        TorrentState.Metadata or TorrentState.FetchingHashes => DownloadState.Metadata,
        TorrentState.Hashing => DownloadState.Checking, TorrentState.Starting => DownloadState.Waiting,
        TorrentState.Paused or TorrentState.HashingPaused => DownloadState.Paused,
        TorrentState.Stopped => running ? DownloadState.Stopped : DownloadState.Paused,
        TorrentState.Stopping => DownloadState.Stopping, TorrentState.Error => DownloadState.Error, _ => DownloadState.Waiting
    };

    private void Log(Exception exception)
    {
        try
        {
            string path = Path.Combine(_dataDirectory, "diagnostics.log");
            if (File.Exists(path) && new FileInfo(path).Length > 1024 * 1024) File.Move(path, path + ".old", true);
            File.AppendAllText(path, $"{DateTimeOffset.Now:O} {exception}\n");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
