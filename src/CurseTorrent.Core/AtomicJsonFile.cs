using System.Text.Json;
using System.Text.Json.Serialization;

namespace CurseTorrent.Core;

internal static class AtomicJsonFile
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public static async Task<T?> ReadAsync<T>(string path, List<string> warnings)
    {
        if (!File.Exists(path)) return default;
        try { return await ReadOneAsync<T>(path).ConfigureAwait(false); }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            // Preserve the damaged file before the next successful session checkpoint.
            string damaged = path + ".damaged-" + DateTime.UtcNow.ToString("yyyyMMddHHmmssfff");
            try { File.Copy(path, damaged, overwrite: false); }
            catch (Exception copyError) when (copyError is IOException or UnauthorizedAccessException)
            { throw new IOException($"Не удалось сохранить повреждённый файл {Path.GetFileName(path)}. Данные не перезаписаны.", copyError); }
            if (File.Exists(path + ".bak"))
            {
                try
                {
                    var recovered = await ReadOneAsync<T>(path + ".bak").ConfigureAwait(false);
                    warnings.Add($"{Path.GetFileName(path)} восстановлен из резервной копии. Повреждённый файл сохранён рядом.");
                    return recovered;
                }
                catch (Exception backupError) when (backupError is JsonException or IOException or UnauthorizedAccessException) { }
            }
            warnings.Add($"Не удалось прочитать {Path.GetFileName(path)}: {ex.Message}. Повреждённый файл сохранён рядом.");
            return default;
        }
    }

    private static async Task<T?> ReadOneAsync<T>(string path)
    {
        if (new FileInfo(path).Length > 32 * 1024 * 1024) throw new IOException("Файл состояния слишком велик.");
        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<T>(stream, Options).ConfigureAwait(false)
            ?? throw new JsonException("Пустой файл состояния.");
    }

    public static async Task WriteAsync<T>(string path, T value)
    {
        string temp = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            await using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 16384, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, value, Options).ConfigureAwait(false);
                await stream.FlushAsync().ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }
            if (File.Exists(path)) File.Replace(temp, path, path + ".bak");
            else File.Move(temp, path);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}
