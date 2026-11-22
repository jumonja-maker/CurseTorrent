namespace CurseTorrent.Core;

// Deliberately not an IOException: an unknown future schema must not trigger a
// fallback which could downgrade or overwrite data written by a newer client.
internal sealed class UnsupportedSessionVersionException : Exception
{
    public UnsupportedSessionVersionException() : base("Эта версия CurseTorrent не поддерживает формат сохранённой сессии. Файлы оставлены без изменений.") { }
}
