using MonoTorrent;
using MonoTorrent.PieceWriter;
using ReusableTasks;

namespace CurseTorrent.Core;

/// <summary>
/// Checks payload ownership before the engine touches a file. In particular, reading
/// through DiskWriter can create or truncate files, so reads require the same guard
/// as writes. Forward the original reusable tasks without changing synchronization
/// context; DiskWriter owns its IO scheduling.
/// </summary>
internal sealed class GuardedPieceWriter : IPieceWriter
{
    private readonly IPieceWriter _inner;
    private readonly Action<ITorrentManagerFile, string?> _validate;

    public GuardedPieceWriter(IPieceWriter inner, Action<ITorrentManagerFile, string?> validate)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(validate);
        _inner = inner;
        _validate = validate;
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
        _validate(file, null);
        return _inner.WriteAsync(file, offset, buffer);
    }

    public ReusableTask MoveAsync(ITorrentManagerFile file, string fullPath, bool overwrite)
    {
        _validate(file, fullPath);
        return _inner.MoveAsync(file, fullPath, overwrite);
    }

    // Closing and flushing already-open handles must remain possible after a task is
    // rejected or removed. They neither choose a new path nor open a new file.
    public ReusableTask CloseAsync(ITorrentManagerFile file) => _inner.CloseAsync(file);
    public ReusableTask FlushAsync(ITorrentManagerFile file) => _inner.FlushAsync(file);
    public ReusableTask SetMaximumOpenFilesAsync(int maximumOpenFiles) => _inner.SetMaximumOpenFilesAsync(maximumOpenFiles);
    public void Dispose() => _inner.Dispose();
}
