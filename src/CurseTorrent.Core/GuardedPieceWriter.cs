using MonoTorrent;
using MonoTorrent.PieceWriter;
using ReusableTasks;

namespace CurseTorrent.Core;

/// <summary>
/// Checks reserved paths before IO and file ownership before mutations. The patched
/// DiskWriter performs read-only verification; existing external files can be read
/// without granting permission to overwrite or rename them. Preserve DiskWriter's
/// synchronization context and reusable task scheduling.
/// </summary>
internal sealed class GuardedPieceWriter : IPieceWriter
{
    private readonly IPieceWriter _inner;
    private readonly Func<ITorrentManagerFile, string?, PayloadPermission> _validate;
    private readonly PayloadOwnership _ownership;

    public GuardedPieceWriter(IPieceWriter inner, Func<ITorrentManagerFile, string?, PayloadPermission> validate, string ownershipDirectory)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(validate);
        _inner = inner;
        _validate = validate;
        _ownership = new(ownershipDirectory);
    }

    public int OpenFiles => _inner.OpenFiles;
    public int MaximumOpenFiles => _inner.MaximumOpenFiles;

    public ReusableTask<bool> ExistsAsync(ITorrentManagerFile file)
    {
        _validate(file, null);
        return _inner.ExistsAsync(file);
    }

    public ReusableTask<int> ReadAsync(ITorrentManagerFile file, long offset, Memory<byte> buffer)
    {
        _validate(file, null);
        return _inner.ReadAsync(file, offset, buffer);
    }

    public ReusableTask WriteAsync(ITorrentManagerFile file, long offset, ReadOnlyMemory<byte> buffer)
    {
        var permission = _validate(file, null);
        // A torrent-wide consent never adopts an excluded, pre-existing file for
        // a v1 boundary-piece write. Client-created boundary fragments remain supported.
        if (file.Priority == Priority.DoNotDownload)
            permission = permission with { AllowExistingFiles = false };
        _ownership.PrepareWrite(file, permission);
        return _inner.WriteAsync(file, offset, buffer);
    }

    public async ReusableTask MoveAsync(ITorrentManagerFile file, string fullPath, bool overwrite)
    {
        if (!await TryMoveAsync(file, fullPath, overwrite))
            throw new IOException($"Существующий файл «{file.Path}» сохранён без переименования.");
    }

    public async ReusableTask<bool> TryMoveAsync(ITorrentManagerFile file, string fullPath, bool overwrite)
    {
        var permission = _validate(file, fullPath);
        if (Path.GetFullPath(file.FullPath).Equals(Path.GetFullPath(fullPath), StringComparison.OrdinalIgnoreCase)) return true;
        if (File.Exists(fullPath) || Directory.Exists(fullPath))
            throw new IOException($"Файл назначения «{file.Path}» уже существует. Он сохранён без изменений.");
        // No physical file exists yet: changing its future path grants no ownership.
        if (!File.Exists(file.FullPath)) return true;
        string? identity = _ownership.GetMoveIdentity(file, permission);
        if (identity == null) return false;
        string oldPath = file.FullPath;
        // Even an owned source must never delete a different file at the destination.
        await _inner.MoveAsync(file, fullPath, false);
        _ownership.RecordMove(oldPath, fullPath, permission.OwnerId, identity);
        return true;
    }

    public ReusableTask CreateEmptyAsync(ITorrentManagerFile file)
    {
        if (file.Priority == Priority.DoNotDownload) return ReusableTask.CompletedTask;
        var permission = _validate(file, null);
        if (!permission.AllowExistingFiles && File.Exists(file.FullPath) && new FileInfo(file.FullPath).Length == 0)
            return ReusableTask.CompletedTask;
        _ownership.PrepareWrite(file, permission);
        return _inner.CreateEmptyAsync(file);
    }

    // Closing and flushing already-open handles must remain possible after a task is
    // rejected or removed. They neither choose a new path nor open a new file.
    public ReusableTask CloseAsync(ITorrentManagerFile file) => _inner.CloseAsync(file);
    public ReusableTask FlushAsync(ITorrentManagerFile file) => _inner.FlushAsync(file);
    public ReusableTask SetMaximumOpenFilesAsync(int maximumOpenFiles) => _inner.SetMaximumOpenFilesAsync(maximumOpenFiles);
    public void Dispose() => _inner.Dispose();
}
