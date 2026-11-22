using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MonoTorrent;

namespace CurseTorrent.Core;

internal readonly record struct PayloadPermission(string OwnerId, bool AllowExistingFiles);

/// <summary>
/// Records files created or explicitly adopted by this client. Unknown existing data
/// is readable but never writable by default. Receipts are deliberately separate
/// from resume bitfields: an unverified hash cache cannot grant write permission.
/// Loss of a receipt fails closed and requires explicit adoption in the add dialog.
/// This is accidental-overwrite protection, not a boundary against a local process
/// which can modify this user's files and receipts.
/// </summary>
internal sealed class PayloadOwnership(string directory)
{
    private sealed record Receipt(int Version, string Path, string OwnerId, string FileIdentity, long CreatedUtcTicks);
    private readonly Dictionary<string, Receipt> _receipts = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _sync = new();

    public void PrepareWrite(ITorrentManagerFile file, PayloadPermission permission)
    {
        lock (_sync)
        {
            string path = System.IO.Path.GetFullPath(file.FullPath);
            var info = new FileInfo(path);
            if (info.Exists)
            {
                if (info.Length > file.Length)
                    throw new IOException($"Файл «{file.Path}» больше файла раздачи. Он сохранён без изменений. Выберите другую папку.");
                if (Matches(path, permission.OwnerId, info)) return;
                if (!permission.AllowExistingFiles)
                    throw new IOException($"Файл «{file.Path}» уже существует и защищён от перезаписи. Выберите другую папку или заново добавьте раздачу с разрешением дозагрузки в существующие файлы.");
            }
            else
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
                // CreateNew closes the check/create race. Never open an intervening file for writing.
                using var created = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            }
            Save(path, permission.OwnerId);
        }
    }

    public string? GetMoveIdentity(ITorrentManagerFile file, PayloadPermission permission)
    {
        lock (_sync)
        {
            var path = System.IO.Path.GetFullPath(file.FullPath);
            var info = new FileInfo(path);
            if (!info.Exists) return null;
            // Rechecking external data must not rename it, even when repair was authorized.
            // Only a write/adoption creates the receipt permitting later completion renames.
            if (!Matches(path, permission.OwnerId, info))
                return null;
            return PayloadFileIdentity.Get(path);
        }
    }

    public void RecordMove(string oldPath, string newPath, string ownerId, string expectedIdentity)
    {
        lock (_sync)
        {
            if (!File.Exists(newPath)) return;
            if (PayloadFileIdentity.Get(newPath) != expectedIdentity)
                throw new IOException("Файл назначения изменился во время завершения загрузки. Данные сохранены без перезаписи.");
            Save(System.IO.Path.GetFullPath(newPath), ownerId);
            _receipts.Remove(System.IO.Path.GetFullPath(oldPath));
            try { File.Delete(ReceiptPath(oldPath)); } catch (IOException) { }
        }
    }

    private bool Matches(string path, string ownerId, FileInfo info)
    {
        if (!_receipts.TryGetValue(path, out var receipt))
        {
            try
            {
                string receiptPath = ReceiptPath(path);
                if (!File.Exists(receiptPath) || new FileInfo(receiptPath).Length > 128 * 1024) return false;
                receipt = JsonSerializer.Deserialize<Receipt>(File.ReadAllText(receiptPath));
                if (receipt is null || receipt.Version != 2 || !path.Equals(receipt.Path, StringComparison.OrdinalIgnoreCase)) return false;
                _receipts[path] = receipt;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return false; }
        }
        return receipt.OwnerId == ownerId && receipt.FileIdentity == PayloadFileIdentity.Get(path) &&
            (!OperatingSystem.IsWindows() || receipt.CreatedUtcTicks == info.CreationTimeUtc.Ticks);
    }

    private void Save(string path, string ownerId)
    {
        var receipt = new Receipt(2, path, ownerId, PayloadFileIdentity.Get(path), File.GetCreationTimeUtc(path).Ticks);
        Directory.CreateDirectory(directory);
        string destination = ReceiptPath(path);
        string temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(receipt));
            File.Move(temporary, destination, true);
            _receipts[path] = receipt;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private string ReceiptPath(string path) => System.IO.Path.Combine(directory,
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(System.IO.Path.GetFullPath(path).ToUpperInvariant()))) + ".json");
}
