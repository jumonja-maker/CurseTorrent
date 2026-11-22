using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using CurseTorrent.Core;

namespace CurseTorrent.App;

internal static class Display
{
    internal static string Bytes(long bytes)
    {
        if (bytes < 1024) return $"{Math.Max(0, bytes)} Б";
        string[] units = ["КиБ", "МиБ", "ГиБ", "ТиБ"];
        double value = bytes;
        int unit = -1;
        do { value /= 1024; unit++; } while (value >= 1024 && unit < units.Length - 1);
        return $"{value.ToString(value >= 100 ? "0" : "0.0", CultureInfo.CurrentCulture)} {units[unit]}";
    }
    internal static string Rate(long bytes) => bytes <= 0 ? "—" : Bytes(bytes) + "/с";
    internal static string Duration(TimeSpan? duration) => duration switch
    {
        null => "—",
        { TotalDays: >= 1 } value => $"{(int)value.TotalDays} д {value.Hours} ч",
        { TotalHours: >= 1 } value => $"{(int)value.TotalHours} ч {value.Minutes} мин",
        { TotalMinutes: >= 1 } value => $"{(int)value.TotalMinutes} мин {value.Seconds} с",
        var value => $"{Math.Max(0, (int)value.Value.TotalSeconds)} с"
    };
    internal static string State(DownloadState state) => state switch
    {
        DownloadState.Stopped => "Остановлено",
        DownloadState.Waiting => "Ожидание пиров",
        DownloadState.Metadata => "Получение метаданных",
        DownloadState.Checking => "Проверка файлов",
        DownloadState.Downloading => "Скачивается",
        DownloadState.Seeding => "Раздаётся",
        DownloadState.Paused => "На паузе",
        DownloadState.Stopping => "Остановка…",
        DownloadState.Error => "Ошибка",
        _ => state.ToString()
    };
    internal static readonly IReadOnlyList<PriorityOption> Priorities = [new(FilePriority.Skip, "Не скачивать"), new(FilePriority.Low, "Низкий"), new(FilePriority.Normal, "Обычный"), new(FilePriority.High, "Высокий")];
}

public sealed record PriorityOption(FilePriority Value, string Label);

public abstract class NotifyModel : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    protected void Changed([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    protected void AllChanged() => Changed(string.Empty);
}

public sealed class TorrentRow(TorrentSnapshot snapshot) : NotifyModel
{
    public TorrentSnapshot Snapshot { get; private set; } = snapshot;
    public string Id => Snapshot.Id;
    public string Name => Snapshot.Name;
    public string Size => Snapshot.HasMetadata ? Display.Bytes(Snapshot.SelectedSizeBytes) : "—";
    public double Progress => Snapshot.Progress;
    public string ProgressText => Snapshot.HasMetadata ? $"{Snapshot.Progress:0.0}%" : "…";
    public string StateText => Display.State(Snapshot.State);
    public string Download => Display.Rate(Snapshot.DownloadBytesPerSecond);
    public string Upload => Display.Rate(Snapshot.UploadBytesPerSecond);
    public string Peers => Snapshot.ConnectedPeers.ToString(CultureInfo.CurrentCulture);
    public string Remaining => Snapshot.Progress >= 100 ? "Готово" : Display.Duration(Snapshot.EstimatedRemaining);
    public string Hint => Snapshot.ErrorMessage ?? Snapshot.Name;
    public void Update(TorrentSnapshot value) { Snapshot = value; AllChanged(); }
}

public sealed class TorrentFileRow(TorrentFileSnapshot snapshot) : NotifyModel
{
    private TorrentFileSnapshot _snapshot = snapshot;
    public string Path => _snapshot.Path;
    public long SizeBytes => _snapshot.SizeBytes;
    public double Progress => _snapshot.Progress;
    public string Size => Display.Bytes(_snapshot.SizeBytes);
    public string ProgressText => $"{_snapshot.Progress:0.0}%";
    public FilePriority Priority
    {
        get => _snapshot.Priority;
        set
        {
            if (value == _snapshot.Priority) return;
            _snapshot = _snapshot with { Priority = value };
            Changed();
        }
    }
    public IReadOnlyList<PriorityOption> Priorities => Display.Priorities;
    public void Update(TorrentFileSnapshot value) { _snapshot = value; AllChanged(); }
}

public sealed class SelectableFileRow(TorrentFilePreview file) : NotifyModel
{
    private bool _selected = true;
    public string Path => file.Path;
    public long SizeBytes => file.SizeBytes;
    public string Size => Display.Bytes(file.SizeBytes);
    public bool IsSelected { get => _selected; set { _selected = value; Changed(); } }
}
