using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using CurseTorrent.Core;
using MonoTorrent;
using MonoTorrent.BEncoding;
using MonoTorrent.Client;
using MonoTorrent.PieceWriter;
using ReusableTasks;

return await SafetySuite.Run();

static class SafetySuite
{
    static int checks;
    public static async Task<int> Run()
    {
        string root = Path.Combine(Path.GetTempPath(), "cursetorrent-file-safety-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            ParserBoundaries();
            MetadataPaths();
            await OwnershipIdentity(Path.Combine(root, "ownership"));
            await ExistingFileCheck(Path.Combine(root, "existing"), partial: false);
            await ExistingFileCheck(Path.Combine(root, "partial"), partial: true);
            await EmptyFiles(Path.Combine(root, "zero-skip"), skip: true, existing: true);
            await EmptyFiles(Path.Combine(root, "zero-absent"), skip: true, existing: false);
            await EmptyFiles(Path.Combine(root, "zero-selected"), skip: false, existing: false);
            await EmptyFiles(Path.Combine(root, "zero-conflict"), skip: false, existing: true);
            await NetworkFileProtection(Path.Combine(root, "network")).WaitAsync(TimeSpan.FromSeconds(65));
            Console.WriteLine($"PASS: {checks} file safety and bounded-decoder checks.");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        finally { try { Directory.Delete(root, true); } catch (IOException) { } }
    }

    static void Check(bool condition, string name)
    {
        if (!condition) throw new Exception("FAIL: " + name);
        checks++; Console.WriteLine("PASS " + name);
    }

    static void ExpectDecodeError(Action action, string name)
    {
        try { action(); } catch (BEncodingException) { Check(true, name); return; }
        throw new Exception("Expected bounded decoding failure: " + name);
    }

    static void ParserBoundaries()
    {
        byte[] ordinary = Encoding.ASCII.GetBytes("llli1eeee");
        Check(BEncodedValue.Decode(ordinary) is BEncodedList, "Small ordinary nested data remains supported");
        // Exercise rejection with a deliberately tiny test quota and a tiny ordinary tree.
        // No deep/crashing input, resource exhaustion or external target is used.
        ExpectDecodeError(() => {
            ReadOnlySpan<byte> span = ordinary;
            BEncodeDecoder.Decode(ref span, false, new DecodeBudget(maximumDepth: 2), 0);
        }, "Span decoder enforces depth before recursion");
        ExpectDecodeError(() => {
            using var stream = new MemoryStream(ordinary);
            BEncodeDecoder.Decode(stream, false, stream.ReadByte(), new DecodeBudget(maximumDepth: 2), 0);
        }, "Stream decoder enforces depth before recursion");
        ExpectDecodeError(() => {
            ReadOnlySpan<byte> span = Encoding.ASCII.GetBytes("li1ei2ei3ee");
            BEncodeDecoder.Decode(ref span, false, new DecodeBudget(maximumObjects: 3), 0);
        }, "Object budget is shared across sibling values");
        ExpectDecodeError(() => {
            using var stream = new MemoryStream(Encoding.ASCII.GetBytes("5:hello"));
            BEncodeDecoder.Decode(stream, false, stream.ReadByte(), new DecodeBudget(maximumBytes: 4), 0);
        }, "String byte budget is checked before allocation");
        foreach (string input in new[] { "i", "ie", "i-e", "i9223372036854775808e", "i-9223372036854775809e" })
        {
            byte[] bytes = Encoding.ASCII.GetBytes(input);
            ExpectDecodeError(() => BEncodedValue.Decode(bytes), "Span rejects invalid/bounded integer " + input);
            ExpectDecodeError(() => BEncodedValue.Decode(new MemoryStream(bytes)), "Stream rejects invalid/bounded integer " + input);
        }
        foreach (long value in new[] { long.MinValue, long.MaxValue, 0L, -1L })
        {
            byte[] bytes = Encoding.ASCII.GetBytes("i" + value + "e");
            Check(BEncodedValue.Decode<BEncodedNumber>(bytes).Number == value, "Int64 boundary remains exact: " + value);
        }
        using var fragmented = new FragmentedStream(Encoding.ASCII.GetBytes("5:hello"));
        Check(BEncodedValue.Decode<BEncodedString>(fragmented).Text == "hello", "Partial stream reads are assembled completely");
    }

    static void MetadataPaths()
    {
        static Torrent Create(params string[] paths)
        {
            var files = new BEncodedList();
            foreach (string path in paths)
            {
                var components = new BEncodedList();
                foreach (string segment in path.Split('/')) components.Add(new BEncodedString(segment));
                files.Add(new BEncodedDictionary { ["length"] = new BEncodedNumber(1), ["path"] = components });
            }
            return Torrent.Load(new BEncodedDictionary {
                ["info"] = new BEncodedDictionary {
                    ["name"] = new BEncodedString("Fixture"), ["piece length"] = new BEncodedNumber(16384),
                    ["pieces"] = new BEncodedString(new byte[20]), ["files"] = files
                }
            }.Encode());
        }
        foreach (string path in new[] { "COM¹.txt", "LPT²", "COM³.bin", new string('a', 256) })
        {
            try { TorrentInput.ValidateMetadata(Create(path)); throw new Exception("Unsupported Windows name was accepted"); }
            catch (ArgumentException) { Check(true, "Windows filename constraints reject " + (path.Length > 30 ? "overlong segment" : path)); }
        }
        try { TorrentInput.ValidateMetadata(Create("Folder", "folder/data.bin")); throw new Exception("File/directory collision was accepted"); }
        catch (ArgumentException) { Check(true, "File/directory collision is rejected case-insensitively"); }
        TorrentInput.ValidateMetadata(Create("folder/data.bin", "folder2/data.bin", new string('a', 255)));
        Check(true, "Ordinary nested paths and the supported component boundary remain accepted");
    }

    static async Task Until(Func<bool> predicate, string operation, int seconds = 12)
    {
        var watch = Stopwatch.StartNew();
        while (!predicate())
        {
            if (watch.Elapsed.TotalSeconds > seconds) throw new TimeoutException(operation);
            await Task.Delay(30);
        }
    }

    static async Task OwnershipIdentity(string root)
    {
        Directory.CreateDirectory(root);
        string path = Path.Combine(root, "owned.bin");
        string receipts = Path.Combine(root, "receipts");
        var file = new TestFile(path, 32);
        var ownership = new PayloadOwnership(receipts);
        var permission = new PayloadPermission("fixture-owner", false);
        ownership.PrepareWrite(file, permission);
        var created = File.GetCreationTimeUtc(path);
        string identity = PayloadFileIdentity.Get(path);
        await File.WriteAllBytesAsync(path, new byte[16]);
        Console.WriteLine($"IDENTITY before={identity} after={PayloadFileIdentity.Get(path)} createdBefore={created.Ticks} createdAfter={File.GetCreationTimeUtc(path).Ticks}");
        ownership.PrepareWrite(file, permission);
        Check(PayloadFileIdentity.Get(path) == identity, "Normal writes retain owned file identity");
        // A normal file replacement can preserve timestamps (including NTFS tunneling).
        // Keep the original file alive to ensure the inode/file ID cannot be recycled in this test.
        File.Move(path, path + ".original");
        byte[] replacement = Enumerable.Repeat((byte)0x58, 16).ToArray();
        await File.WriteAllBytesAsync(path, replacement);
        if (OperatingSystem.IsWindows()) File.SetCreationTimeUtc(path, created);
        Check(PayloadFileIdentity.Get(path) != identity, "Replacement receives a different native identity");
        try { new PayloadOwnership(receipts).PrepareWrite(file, permission); throw new Exception("Replaced file was adopted implicitly"); }
        catch (IOException) { Check(true, "Persisted ownership rejects a different file with the same name and creation time"); }
        Check((await File.ReadAllBytesAsync(path)).SequenceEqual(replacement), "Rejected replacement remains unchanged");

        string missing = Path.Combine(root, "absent.bin");
        string target = Path.Combine(root, "foreign.bin");
        await File.WriteAllBytesAsync(target, replacement);
        using var writer = new GuardedPieceWriter(new NoopWriter(), (_, _) => permission, receipts);
        try { await writer.MoveAsync(new TestFile(missing, 32), target, true); throw new Exception("Missing source adopted an existing target"); }
        catch (IOException) { Check(true, "Missing-source move cannot grant ownership of an existing target"); }
        try { new PayloadOwnership(receipts).PrepareWrite(new TestFile(target, 32), permission); throw new Exception("No-op move created a target receipt"); }
        catch (IOException) { Check(true, "Failed move leaves foreign target protected"); }
    }

    static async Task<(string Metadata, byte[] Bytes)> SingleFixture(string root, string? announce = null, int size = 32768)
    {
        Directory.CreateDirectory(Path.Combine(root, "seed"));
        byte[] bytes = new byte[size]; new Random(933).NextBytes(bytes);
        string file = Path.Combine(root, "seed", "notes.bin");
        await File.WriteAllBytesAsync(file, bytes);
        string metadata = Path.Combine(root, "fixture.torrent");
        var creator = new TorrentCreator(TorrentType.V1Only) { PieceLength = 16384 };
        if (announce != null) creator.Announce = announce;
        await creator.CreateAsync(new TorrentFileSource(file), metadata);
        return (metadata, bytes);
    }

    static async Task ExistingFileCheck(string root, bool partial)
    {
        var fixture = await SingleFixture(root);
        string downloads = Path.Combine(root, "downloads"); Directory.CreateDirectory(downloads);
        string path = Path.Combine(downloads, "notes.bin" + (partial ? ".!mt" : ""));
        byte[] original = Enumerable.Repeat((byte)0x5A, 131072).ToArray();
        await File.WriteAllBytesAsync(path, original);
        await using var service = new TorrentService(Path.Combine(root, "state"), true);
        await service.InitializeAsync();
        string id = await service.AddAsync(new(fixture.Metadata, downloads, false));
        Check((await File.ReadAllBytesAsync(path)).SequenceEqual(original), "Add paused preserves existing " + (partial ? "partial" : "complete") + " file");
        try { await service.RecheckAsync(id); } catch (IOException) { }
        await Until(() => service.GetSnapshots().Single().State != DownloadState.Checking, "Read-only recheck");
        Check(File.Exists(path) && (await File.ReadAllBytesAsync(path)).SequenceEqual(original), "Recheck preserves existing name, length and bytes");
        Check(Directory.GetFiles(downloads).Length == 1, "Read-only recheck creates no alternative payload");
    }

    static async Task EmptyFiles(string root, bool skip, bool existing)
    {
        string seed = Path.Combine(root, "seed"); Directory.CreateDirectory(seed);
        await File.WriteAllBytesAsync(Path.Combine(seed, "payload.bin"), new byte[32768]);
        await File.WriteAllBytesAsync(Path.Combine(seed, "notes.txt"), []);
        string metadata = Path.Combine(root, "fixture.torrent");
        await new TorrentCreator(TorrentType.V1Only) { PieceLength = 16384 }.CreateAsync(new TorrentFileSource(seed) { TorrentName = "Fixture" }, metadata);
        string downloads = Path.Combine(root, "downloads");
        string path = Path.Combine(downloads, "Fixture", "notes.txt");
        byte[] original = Enumerable.Repeat((byte)0x61, 131072).ToArray();
        if (existing) { Directory.CreateDirectory(Path.GetDirectoryName(path)!); await File.WriteAllBytesAsync(path, original); }
        await using var service = new TorrentService(Path.Combine(root, "state"), true);
        await service.InitializeAsync();
        string id = await service.AddAsync(new(metadata, downloads, false, new Dictionary<string, FilePriority> { ["notes.txt"] = skip ? FilePriority.Skip : FilePriority.Normal }));
        try { await service.ResumeAsync(id); } catch (IOException) when (existing && !skip) { }
        await Until(() => service.GetSnapshots().Single().State is DownloadState.Downloading or DownloadState.Error, "Starting zero-file case");
        if (existing) Check(File.Exists(path) && (await File.ReadAllBytesAsync(path)).SequenceEqual(original), "Zero-file start preserves existing nonempty data; skip=" + skip);
        else if (skip) Check(!File.Exists(path) && !File.Exists(path + ".!mt"), "Skipped empty file is not created");
        else Check((File.Exists(path) && new FileInfo(path).Length == 0) || (File.Exists(path + ".!mt") && new FileInfo(path + ".!mt").Length == 0), "Selected empty file is created safely");
        if (existing && !skip) Check(service.GetSnapshots().Single().State == DownloadState.Error, "Selected empty-file conflict reports an error");
    }

    static async Task NetworkFileProtection(string root)
    {
        using var tracker = new LocalTracker();
        var fixture = await SingleFixture(root, tracker.AnnounceUrl, 256 * 1024);
        int port;
        using (var socket = new TcpListener(IPAddress.Loopback, 0)) { socket.Start(); port = ((IPEndPoint)socket.LocalEndpoint).Port; }
        using var engine = new ClientEngine(new EngineSettingsBuilder {
            CacheDirectory = Path.Combine(root, "seed-cache"), DhtEndPoint = null,
            ListenEndPoints = new Dictionary<string, IPEndPoint> { ["ipv4"] = new(IPAddress.Loopback, port) },
            AllowLocalPeerDiscovery = false, AllowPortForwarding = false, AutoSaveLoadFastResume = false, AutoSaveLoadDhtCache = false,
            MaximumUploadRate = 256 * 1024
        }.ToSettings());
        var manager = await engine.AddAsync(fixture.Metadata, Path.Combine(root, "seed"), new TorrentSettingsBuilder { CreateContainingDirectory = false, AllowDht = false, AllowPeerExchange = false }.ToSettings());
        await manager.StartAsync();
        await Until(() => manager.State == TorrentState.Seeding, "Safety fixture seeder");
        try
        {
            foreach (bool adopt in new[] { false, true })
            {
                string downloads = Path.Combine(root, adopt ? "consented" : "protected"); Directory.CreateDirectory(downloads);
                string target = Path.Combine(downloads, "notes.bin");
                byte[] original = Enumerable.Repeat((byte)0x32, fixture.Bytes.Length).ToArray();
                await File.WriteAllBytesAsync(target, original);
                await using var service = new TorrentService(Path.Combine(root, "state-" + adopt), true);
                await service.InitializeAsync();
                await service.AddAsync(new(fixture.Metadata, downloads, true, AllowExistingFiles: adopt));
                await Until(() => service.GetSnapshots().Single().State is DownloadState.Seeding or DownloadState.Error, "Protected/adopted download", 22);
                if (adopt)
                {
                    Check(service.GetSnapshots().Single().State == DownloadState.Seeding, "Explicit existing-file consent permits repair");
                    Check((await File.ReadAllBytesAsync(target)).SequenceEqual(fixture.Bytes), "Consented repair contains verified seed bytes");
                }
                else
                {
                    Check(service.GetSnapshots().Single().State == DownloadState.Error, "Unknown existing file is refused before network write");
                    Check((await File.ReadAllBytesAsync(target)).SequenceEqual(original), "Network download cannot overwrite unknown existing data");
                }
            }
        }
        finally { await engine.StopAllAsync(TimeSpan.FromSeconds(2)); }
    }

    sealed class FragmentedStream(byte[] buffer) : MemoryStream(buffer)
    {
        public override int Read(byte[] buffer, int offset, int count) => base.Read(buffer, offset, Math.Min(count, 1));
    }

    sealed class TestFile(string fullPath, long length) : ITorrentManagerFile
    {
        public string Path => System.IO.Path.GetFileName(fullPath);
        public string FullPath => fullPath;
        public string DownloadCompleteFullPath => fullPath;
        public string DownloadIncompleteFullPath => fullPath + ".!mt";
        public long Length => length;
        public long Padding => 0;
        public long OffsetInTorrent => 0;
        public int StartPieceIndex => 0;
        public int EndPieceIndex => 0;
        public int PieceCount => 1;
        public MerkleRoot PiecesRoot => default;
        public ReadOnlyBitField BitField { get; } = new ReadOnlyBitField(1);
        public Priority Priority => Priority.Normal;
    }

    sealed class NoopWriter : IPieceWriter
    {
        public int OpenFiles => 0;
        public int MaximumOpenFiles => 1;
        public ReusableTask CreateEmptyAsync(ITorrentManagerFile file) => ReusableTask.CompletedTask;
        public ReusableTask CloseAsync(ITorrentManagerFile file) => ReusableTask.CompletedTask;
        public ReusableTask<bool> ExistsAsync(ITorrentManagerFile file) => ReusableTask.FromResult(File.Exists(file.FullPath));
        public ReusableTask FlushAsync(ITorrentManagerFile file) => ReusableTask.CompletedTask;
        public ReusableTask MoveAsync(ITorrentManagerFile file, string path, bool overwrite) => ReusableTask.CompletedTask;
        public ReusableTask<int> ReadAsync(ITorrentManagerFile file, long offset, Memory<byte> buffer) => ReusableTask.FromResult(0);
        public ReusableTask WriteAsync(ITorrentManagerFile file, long offset, ReadOnlyMemory<byte> buffer) => ReusableTask.CompletedTask;
        public ReusableTask SetMaximumOpenFilesAsync(int maximum) => ReusableTask.CompletedTask;
        public void Dispose() { }
    }
}
