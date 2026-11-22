using MonoTorrent;

namespace CurseTorrent.Core;

/// <summary>
/// An in-memory hint tied to an actual successful hash check. It is never persisted
/// as proof across process restarts. Unknown/missing/unstable files disable the hint.
/// </summary>
internal sealed record PayloadFingerprint(long Length, long LastWriteUtcTicks, string Identity)
{
    public static Dictionary<string, PayloadFingerprint>? Capture(IEnumerable<ITorrentManagerFile> files)
    {
        try
        {
            var orderedFiles = files.OrderBy(file => file.StartPieceIndex).ToArray();
            var selectedRanges = new List<(int Start, int End)>();
            foreach (var file in orderedFiles.Where(file => file.Priority != Priority.DoNotDownload && file.Length > 0))
            {
                if (selectedRanges.Count > 0 && (long)selectedRanges[^1].End + 1 >= file.StartPieceIndex)
                    selectedRanges[^1] = (selectedRanges[^1].Start, Math.Max(selectedRanges[^1].End, file.EndPieceIndex));
                else
                    selectedRanges.Add((file.StartPieceIndex, file.EndPieceIndex));
            }

            var result = new Dictionary<string, PayloadFingerprint>(StringComparer.OrdinalIgnoreCase);
            int rangeIndex = 0;
            foreach (var file in orderedFiles)
            {
                // A v1 piece may contain both selected and skipped files. Its verified
                // bit is only reusable if every file supplying its bytes is unchanged.
                // Merge and sweep piece intervals instead of comparing every file pair.
                while (rangeIndex < selectedRanges.Count && selectedRanges[rangeIndex].End < file.StartPieceIndex)
                    rangeIndex++;
                if (file.Priority == Priority.DoNotDownload &&
                    (file.Length == 0 || rangeIndex == selectedRanges.Count || selectedRanges[rangeIndex].Start > file.EndPieceIndex))
                    continue;

                // An owned completion rename may have finished before the manager's
                // path notification. Accept only one of this file's reserved paths.
                string path = file.FullPath;
                if (!File.Exists(path) && File.Exists(file.DownloadCompleteFullPath)) path = file.DownloadCompleteFullPath;
                var info = new FileInfo(path);
                if (!info.Exists) return null;
                long length = info.Length;
                long write = info.LastWriteTimeUtc.Ticks;
                string identity = PayloadFileIdentity.Get(path);
                info.Refresh();
                if (!info.Exists || info.Length != length || info.LastWriteTimeUtc.Ticks != write || PayloadFileIdentity.Get(path) != identity)
                    return null;
                result.Add(file.Path, new(length, write, identity));
            }
            return result;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    public static bool Matches(IReadOnlyDictionary<string, PayloadFingerprint>? expected, IReadOnlyDictionary<string, PayloadFingerprint>? actual) =>
        expected != null && actual != null && expected.Count == actual.Count &&
        expected.All(pair => actual.TryGetValue(pair.Key, out var value) && pair.Value == value);
}
