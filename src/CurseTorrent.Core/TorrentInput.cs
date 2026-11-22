using MonoTorrent;

namespace CurseTorrent.Core;

internal static class TorrentInput
{
    public const int MaximumMetadataBytes = 16 * 1024 * 1024;
    public const int MaximumSourceCharacters = 32768;

    public static string Normalize(string source)
    {
        if (string.IsNullOrWhiteSpace(source)) throw new ArgumentException("Укажите .torrent-файл или magnet-ссылку.");
        source = source.Trim().Trim('"');
        if (source.Length > MaximumSourceCharacters) throw new ArgumentException("Слишком длинный путь или magnet-ссылка.");
        if (source.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase))
        {
            if (!MagnetLink.TryParse(source, out _)) throw new ArgumentException("Некорректная magnet-ссылка: отсутствует допустимый хеш раздачи.");
            return source;
        }
        if (Uri.TryCreate(source, UriKind.Absolute, out var uri) && uri.IsFile) source = uri.LocalPath;
        if (!Path.GetExtension(source).Equals(".torrent", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Поддерживаются только .torrent-файлы и magnet-ссылки.");
        return Path.GetFullPath(source);
    }

    public static bool IsMagnet(string source) => source.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase);

    public static void CheckMetadataFile(string path)
    {
        var file = new FileInfo(path);
        if (!file.Exists) throw new FileNotFoundException("Torrent-файл не найден.", path);
        if (file.Length == 0 || file.Length > MaximumMetadataBytes)
            throw new ArgumentException("Размер .torrent-файла должен быть от 1 байта до 16 МиБ.");
    }

    public static async Task<byte[]> ReadMetadataAsync(string path)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous);
        if (stream.Length <= 0 || stream.Length > MaximumMetadataBytes)
            throw new ArgumentException("Размер .torrent-файла должен быть от 1 байта до 16 МиБ.");
        byte[] bytes = new byte[checked((int)stream.Length)];
        await stream.ReadExactlyAsync(bytes).ConfigureAwait(false);
        if (await stream.ReadAsync(new byte[1]).ConfigureAwait(false) != 0)
            throw new IOException("Torrent-файл изменился во время чтения. Повторите попытку.");
        return bytes;
    }

    public static async Task<Torrent> LoadMetadataAsync(string path) => Torrent.Load(await ReadMetadataAsync(path).ConfigureAwait(false));

    public static void ValidateMetadata(Torrent torrent)
    {
        ValidateRelativePath(torrent.Name);
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in torrent.Files)
        {
            ValidateRelativePath(file.Path);
            if (!paths.Add(file.Path.Replace('\\', '/')))
                throw new ArgumentException("Раздача содержит совпадающие имена файлов для Windows.");
        }
        foreach (string path in paths)
        {
            int slash = path.IndexOf('/');
            while (slash >= 0)
            {
                if (paths.Contains(path[..slash]))
                    throw new ArgumentException("Раздача использует одно имя и как файл, и как папку.");
                slash = path.IndexOf('/', slash + 1);
            }
        }
    }

    private static void ValidateRelativePath(string path)
    {
        if (string.IsNullOrEmpty(path) || Path.IsPathRooted(path) || path[0] is '/' or '\\')
            throw new ArgumentException("Раздача содержит недопустимый путь к файлу.");
        foreach (string segment in path.Split(['/', '\\']))
        {
            if (segment.Length == 0 || segment.Length > 255 || segment is "." or ".." || segment.EndsWith('.') || segment.EndsWith(' ') ||
                segment.Any(c => c < 32 || "<>:\"|?*".Contains(c)))
                throw new ArgumentException($"Имя файла не поддерживается Windows: {path}");
            string stem = segment.Split('.')[0].ToUpperInvariant();
            if (stem is "CON" or "PRN" or "AUX" or "NUL" ||
                (stem.Length == 4 && (stem.StartsWith("COM") || stem.StartsWith("LPT")) && (stem[3] is >= '1' and <= '9' or '¹' or '²' or '³')))
                throw new ArgumentException($"Зарезервированное имя файла Windows: {path}");
        }
    }

    public static string NormalizeDirectory(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Path.IsPathFullyQualified(directory))
            throw new ArgumentException("Укажите полный путь к папке загрузки.");
        return Path.GetFullPath(directory.Trim());
    }
}
