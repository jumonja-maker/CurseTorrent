using System.Text.Json;
using System.Text.Json.Serialization;

namespace CurseTorrent.Core;

/// <summary>Atomic JSON storage with schema validation before accepting or rotating a backup.</summary>
internal static class AtomicJsonFile
{
    internal const int MaximumStateBytes = 32 * 1024 * 1024;
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public static async Task<T?> ReadAsync<T>(string path, List<string> warnings, Action<T>? validate = null, Action<JsonElement>? validateJson = null)
    {
        Exception? primaryError = null;
        if (File.Exists(path))
        {
            try { return await ReadOneAsync(path, validate, validateJson).ConfigureAwait(false); }
            catch (Exception ex) when (IsRecoverable(ex))
            {
                primaryError = ex;
                PreserveDamagedFile(path);
            }
        }

        // A missing primary can also be the result of an interrupted external operation.
        // Do not discard a usable backup simply because File.Exists(primary) is false.
        if (File.Exists(path + ".bak"))
        {
            try
            {
                var recovered = await ReadOneAsync(path + ".bak", validate, validateJson).ConfigureAwait(false);
                // Repair the primary without rotating the damaged document over the good backup.
                // If this write fails, abort initialization; the good backup is still untouched.
                await WriteCoreAsync(path, recovered, rotateBackup: false).ConfigureAwait(false);
                warnings.Add($"{Path.GetFileName(path)} восстановлен из резервной копии." +
                    (primaryError != null ? " Повреждённый файл сохранён рядом." : ""));
                return recovered;
            }
            catch (Exception ex) when (ex is JsonException or InvalidDataException)
            {
                warnings.Add($"Резервная копия {Path.GetFileName(path)} также повреждена: {ex.Message}");
            }
        }

        if (primaryError != null)
            warnings.Add($"Не удалось прочитать {Path.GetFileName(path)}: {primaryError.Message}. Повреждённый файл сохранён рядом.");
        return default;
    }

    private static bool IsRecoverable(Exception exception) =>
        exception is JsonException or InvalidDataException or IOException or UnauthorizedAccessException;

    private static async Task<T> ReadOneAsync<T>(string path, Action<T>? validate, Action<JsonElement>? validateJson)
    {
        if (new FileInfo(path).Length > MaximumStateBytes) throw new InvalidDataException("Файл состояния слишком велик.");
        await using var stream = File.OpenRead(path);
        using var document = await JsonDocument.ParseAsync(stream).ConfigureAwait(false);
        validateJson?.Invoke(document.RootElement);
        var value = document.RootElement.Deserialize<T>(Options)
            ?? throw new JsonException("Пустой файл состояния.");
        validate?.Invoke(value);
        return value;
    }

    private static void PreserveDamagedFile(string path)
    {
        string damaged = path + ".damaged-" + DateTime.UtcNow.ToString("yyyyMMddHHmmssfff") + "-" + Guid.NewGuid().ToString("N");
        try { File.Copy(path, damaged, overwrite: false); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { throw new IOException($"Не удалось сохранить повреждённый файл {Path.GetFileName(path)}. Данные не перезаписаны.", ex); }
    }

    public static async Task WriteAsync<T>(string path, T value, Action<T>? validate = null, Action<JsonElement>? validateJson = null)
    {
        validate?.Invoke(value);
        bool rotateBackup = File.Exists(path);
        if (rotateBackup)
        {
            try { _ = await ReadOneAsync(path, validate, validateJson).ConfigureAwait(false); }
            catch (Exception ex) when (ex is JsonException or InvalidDataException)
            {
                // The primary might have been changed by an external process since startup.
                // Never replace the last valid backup with a structurally invalid document.
                PreserveDamagedFile(path);
                rotateBackup = false;
            }
        }
        await WriteCoreAsync(path, value, rotateBackup).ConfigureAwait(false);
    }

    private static async Task WriteCoreAsync<T>(string path, T value, bool rotateBackup)
    {
        string temp = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            await using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 16384, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, value, Options).ConfigureAwait(false);
                if (stream.Length > MaximumStateBytes)
                    throw new InvalidDataException("Файл состояния превышает 32 МиБ. Предыдущая сессия сохранена без изменений.");
                await stream.FlushAsync().ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }
            if (File.Exists(path)) File.Replace(temp, path, rotateBackup ? path + ".bak" : null);
            else File.Move(temp, path);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}
