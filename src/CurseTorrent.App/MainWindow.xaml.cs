using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;
using CurseTorrent.Core;
using Microsoft.Win32;
using Forms = System.Windows.Forms;

namespace CurseTorrent.App;

public partial class MainWindow : Window
{
    private readonly ITorrentService _service;
    private readonly bool _smokeTest;
    private readonly ObservableCollection<TorrentRow> _rows = [];
    private readonly ObservableCollection<TorrentFileRow> _fileRows = [];
    private readonly DispatcherTimer _timer;
    private readonly SemaphoreSlim _sourceQueue = new(1, 1);
    private readonly ICollectionView _view;
    private readonly ICollectionView _filesView;
    private Forms.NotifyIcon? _tray;
    private System.Drawing.Icon? _trayIcon;
    private bool _busy;
    private bool _synchronizing;
    private bool _exiting;
    private bool _allowClose;
    private bool _trayHintShown;
    private DateTime _statusUntil;
    private string? _fileTorrentId;
    private string _filter = "all";

    public MainWindow(ITorrentService service, bool smokeTest = false)
    {
        _service = service;
        _smokeTest = smokeTest;
        InitializeComponent();
        VersionLabel.Text = $"CurseTorrent {typeof(App).Assembly.GetName().Version?.ToString(3) ?? "—"}";
        WindowTheme.Attach(this);
        _view = CollectionViewSource.GetDefaultView(_rows);
        _view.Filter = MatchesFilter;
        TorrentGrid.ItemsSource = _view;
        _filesView = CollectionViewSource.GetDefaultView(_fileRows);
        if (_filesView is ICollectionViewLiveShaping live && live.CanChangeLiveSorting)
        {
            foreach (string property in new[] { nameof(TorrentFileRow.Path), nameof(TorrentFileRow.SizeBytes), nameof(TorrentFileRow.Progress), nameof(TorrentFileRow.Priority) })
                live.LiveSortingProperties.Add(property);
            live.IsLiveSorting = true;
        }
        FilesGrid.ItemsSource = _filesView;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) => Refresh();
        Loaded += (_, _) => { Refresh(); _timer.Start(); };
        if (!smokeTest) CreateTray();
    }

    public void ShowAndActivate()
    {
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
    }

    internal void RefreshSmokeView(bool selectFirst)
    {
        if (!_smokeTest) throw new InvalidOperationException("Smoke-only operation.");
        Refresh();
        if (selectFirst) TorrentGrid.SelectedItem = _rows.FirstOrDefault();
        Refresh();
    }

    private void CreateTray()
    {
        try
        {
            _trayIcon = System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath ?? "") ?? (System.Drawing.Icon)System.Drawing.SystemIcons.Application.Clone();
            var menu = new Forms.ContextMenuStrip();
            menu.Items.Add("Открыть CurseTorrent", null, (_, _) => Dispatcher.Invoke(ShowAndActivate));
            menu.Items.Add(new Forms.ToolStripSeparator());
            menu.Items.Add("Выход", null, (_, _) => Dispatcher.InvokeAsync(async () => await RequestExitAsync()));
            _tray = new Forms.NotifyIcon { Text = "CurseTorrent", Icon = _trayIcon, ContextMenuStrip = menu, Visible = true };
            _tray.DoubleClick += (_, _) => Dispatcher.Invoke(ShowAndActivate);
        }
        catch (Exception exception) { App.WriteLog(exception); }
    }

    public void ShowStartupWarnings()
    {
        if (_service.StartupWarnings.Count == 0) return;
        App.WriteLog("Сообщения при запуске:\n" + string.Join("\n\n", _service.StartupWarnings));
        ShowAndActivate();
        MessageBox.Show(this, DialogText.StartupSummary(_service.StartupWarnings), "Сообщения при запуске", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    public async Task QueueSourcesAsync(IEnumerable<string> sources)
    {
        string[] pending = sources.ToArray();
        if (pending.Length > 64)
        {
            MessageBox.Show(this, "За один раз можно добавить до 64 торрентов. Выберите меньшую группу файлов.", "Добавление торрентов", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        await _sourceQueue.WaitAsync();
        try
        {
            foreach (string source in pending)
            {
                if (_exiting) break;
                while (_busy && !_exiting) await Task.Delay(100);
                if (_exiting) break;
                ShowAndActivate();
                await RunOperationAsync(async () =>
                {
                    var preview = await _service.InspectAsync(source);
                    var dialog = new AddTorrentWindow(preview, _service.Settings.DownloadDirectory) { Owner = this };
                    if (dialog.ShowDialog() != true || dialog.Request is null || _exiting) return;
                    string id = await _service.AddAsync(dialog.Request);
                    _filter = "all";
                    Filters.SelectedIndex = 0;
                    SearchBox.Clear();
                    Refresh();
                    TorrentGrid.SelectedItem = _rows.FirstOrDefault(row => row.Id == id);
                    if (TorrentGrid.SelectedItem is not null) TorrentGrid.ScrollIntoView(TorrentGrid.SelectedItem);
                    SetStatus("Торрент добавлен");
                }, "Чтение торрента…");
            }
        }
        finally { _sourceQueue.Release(); }
    }

    private bool MatchesFilter(object item)
    {
        if (item is not TorrentRow row) return false;
        string search = SearchBox?.Text?.Trim() ?? "";
        if (!row.Name.Contains(search, StringComparison.CurrentCultureIgnoreCase)) return false;
        return _filter switch
        {
            "downloading" => row.Snapshot.State is DownloadState.Downloading or DownloadState.Metadata or DownloadState.Checking or DownloadState.Waiting,
            "seeding" => row.Snapshot.State == DownloadState.Seeding,
            "paused" => row.Snapshot.State is DownloadState.Paused or DownloadState.Stopped,
            "error" => row.Snapshot.State == DownloadState.Error,
            _ => true
        };
    }

    private void Refresh()
    {
        if (_exiting) return;
        try
        {
            string? selectedId = (TorrentGrid.SelectedItem as TorrentRow)?.Id;
            var snapshots = _service.GetSnapshots(selectedId);
            var ids = snapshots.Select(value => value.Id).ToHashSet(StringComparer.Ordinal);
            _synchronizing = true;
            for (int i = _rows.Count - 1; i >= 0; i--) if (!ids.Contains(_rows[i].Id)) _rows.RemoveAt(i);
            var existing = _rows.ToDictionary(row => row.Id, StringComparer.Ordinal);
            foreach (var snapshot in snapshots)
            {
                if (existing.TryGetValue(snapshot.Id, out var row)) row.Update(snapshot);
                else _rows.Add(new TorrentRow(snapshot));
            }
            // Source mutations must be processed immediately by WPF's ListCollectionView.
            // Refresh afterwards so existing rows are re-filtered when their state changes.
            _view.Refresh();
            if (selectedId is not null && TorrentGrid.SelectedItem is null)
                TorrentGrid.SelectedItem = _rows.FirstOrDefault(row => row.Id == selectedId && MatchesFilter(row));
            CountLabel.Text = _rows.Count == 0 ? "Добавьте первый торрент" : $"Торрентов: {_rows.Count}   ·   Активных: {snapshots.Count(value => value.State is DownloadState.Downloading or DownloadState.Seeding or DownloadState.Metadata or DownloadState.Checking)}";
            TransferLabel.Text = $"↓ {Display.Rate(snapshots.Sum(value => (long)value.DownloadBytesPerSecond))}    ↑ {Display.Rate(snapshots.Sum(value => (long)value.UploadBytesPerSecond))}";
            EmptyState.Visibility = _view.IsEmpty ? Visibility.Visible : Visibility.Collapsed;
            EmptyTitle.Text = _rows.Count == 0 ? "Здесь появятся ваши загрузки" : "Ничего не найдено";
            EmptyDescription.Text = _rows.Count == 0 ? "Перетащите .torrent в окно или добавьте magnet-ссылку" : "Измените запрос поиска или выберите другой фильтр";
            UpdateDetails();
            UpdateButtons();
            if (DateTime.UtcNow > _statusUntil && !_busy)
            {
                string port = _service.Settings.ListenPort == 0 ? "Порт автоматически" : $"Порт {_service.Settings.ListenPort}";
                StatusLabel.Text = $"Готово   ·   {port}   ·   Лимит ↓ {Limit(_service.Settings.DownloadLimitKiB)} / ↑ {Limit(_service.Settings.UploadLimitKiB)}";
                StatusLabel.ToolTip = StatusLabel.Text;
            }
        }
        catch (Exception exception)
        {
            App.WriteLog(exception);
            SetStatus("Не удалось обновить список: " + exception.Message);
            if (_smokeTest) throw;
        }
        finally { _synchronizing = false; }
    }

    private static string Limit(int value) => value == 0 ? "без ограничения" : $"{value} КиБ/с";

    private void UpdateDetails()
    {
        var selected = (TorrentGrid.SelectedItem as TorrentRow)?.Snapshot;
        DetailsTabs.Visibility = selected is null ? Visibility.Collapsed : Visibility.Visible;
        NoSelection.Visibility = selected is null ? Visibility.Visible : Visibility.Collapsed;
        if (selected is null)
        {
            _fileTorrentId = null;
            _fileRows.Clear();
            return;
        }
        var files = selected.Files;
        if (_fileTorrentId != selected.Id || _fileRows.Count != files.Count || !_fileRows.Select(file => file.Path).SequenceEqual(files.Select(file => file.Path)))
        {
            _fileRows.Clear();
            foreach (var file in files)
            {
                var row = new TorrentFileRow(file);
                row.PropertyChanged += OnFilePropertyChanged;
                _fileRows.Add(row);
            }
            _fileTorrentId = selected.Id;
        }
        else for (int i = 0; i < files.Count; i++) _fileRows[i].Update(files[i]);
        FilesHint.Text = selected.HasMetadata ? "Приоритет «Не скачивать» исключает файл. Части на границе файлов могут загружаться для проверки целостности." : "Список файлов появится после получения метаданных от пиров. По умолчанию выбраны все файлы.";
        DetailsDirectory.Text = selected.SaveDirectory;
        DetailsSize.Text = $"{Display.Bytes(selected.SelectedSizeBytes)} / {Display.Bytes(selected.SizeBytes)}";
        DetailsTransferred.Text = $"{Display.Bytes(selected.DownloadedBytes)} / {Display.Bytes(selected.UploadedBytes)}";
        DetailsPeers.Text = $"{selected.ConnectedPeers} / {selected.Seeds}";
        DetailsState.Text = selected.ErrorMessage is null ? Display.State(selected.State) : $"{Display.State(selected.State)}: {selected.ErrorMessage}";
    }

    private async void OnFilePropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (_synchronizing || args.PropertyName != nameof(TorrentFileRow.Priority) || sender is not TorrentFileRow file || _fileTorrentId is null) return;
        string id = _fileTorrentId;
        var priority = file.Priority;
        await RunOperationAsync(() => _service.SetFilePriorityAsync(id, file.Path, priority), "Изменение приоритета…");
    }

    private void UpdateButtons()
    {
        bool selected = TorrentGrid.SelectedItem is TorrentRow;
        ResumeButton.IsEnabled = PauseButton.IsEnabled = RecheckButton.IsEnabled = RemoveButton.IsEnabled = FolderButton.IsEnabled = selected && !_busy;
        Toolbar.IsEnabled = SettingsButton.IsEnabled = FilesGrid.IsEnabled = !_busy && !_exiting;
    }

    private async Task RunOperationAsync(Func<Task> operation, string status = "Выполняется…")
    {
        if (_busy || _exiting) return;
        _busy = true;
        SetStatus(status);
        UpdateButtons();
        try { await operation(); }
        catch (Exception exception)
        {
            App.WriteLog(exception);
            SetStatus("Ошибка: " + exception.Message);
            MessageBox.Show(this, DialogText.Summary(exception.Message), "CurseTorrent", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally { _busy = false; Refresh(); UpdateButtons(); }
    }

    private void SetStatus(string text) { StatusLabel.Text = text; StatusLabel.ToolTip = text; _statusUntil = DateTime.UtcNow.AddSeconds(8); }

    private async void OnAddTorrent(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFileDialog { Filter = "Торрент-файлы (*.torrent)|*.torrent", Multiselect = true, Title = "Добавить торрент" };
        if (picker.ShowDialog(this) == true) await QueueSourcesAsync(picker.FileNames);
    }

    private async void OnAddMagnet(object sender, RoutedEventArgs e)
    {
        var dialog = new MagnetWindow { Owner = this };
        if (dialog.ShowDialog() == true) await QueueSourcesAsync([dialog.Magnet]);
    }

    private async void OnResume(object sender, RoutedEventArgs e)
    {
        if (TorrentGrid.SelectedItem is TorrentRow row) await RunOperationAsync(() => _service.ResumeAsync(row.Id), "Продолжение загрузки…");
    }
    private async void OnPause(object sender, RoutedEventArgs e)
    {
        if (TorrentGrid.SelectedItem is TorrentRow row) await RunOperationAsync(() => _service.PauseAsync(row.Id), "Сохранение и пауза…");
    }
    private async void OnRecheck(object sender, RoutedEventArgs e)
    {
        if (TorrentGrid.SelectedItem is TorrentRow row) await RunOperationAsync(() => _service.RecheckAsync(row.Id), "Запущена проверка целостности…");
    }
    private async void OnRemove(object sender, RoutedEventArgs e)
    {
        if (TorrentGrid.SelectedItem is not TorrentRow row) return;
        if (MessageBox.Show(this, $"Убрать «{DialogText.SingleLine(row.Name)}» из списка?\n\nЗагруженные файлы останутся на диске. Раздача будет остановлена.", "Удалить торрент из списка", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
            await RunOperationAsync(() => _service.RemoveAsync(row.Id), "Удаление из списка…");
    }
    private void OnOpenFolder(object sender, RoutedEventArgs e)
    {
        if (TorrentGrid.SelectedItem is not TorrentRow row) return;
        try
        {
            if (!Directory.Exists(row.Snapshot.SaveDirectory)) throw new DirectoryNotFoundException("Папка загрузки пока не создана или недоступна.");
            Process.Start(new ProcessStartInfo("explorer.exe") { UseShellExecute = true, Arguments = "\"" + row.Snapshot.SaveDirectory + "\"" });
        }
        catch (Exception exception) { App.WriteLog(exception); MessageBox.Show(this, DialogText.Summary(exception.Message), "Не удалось открыть папку", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }
    private async void OnSettings(object sender, RoutedEventArgs e)
    {
        var dialog = new SettingsWindow(_service.Settings) { Owner = this };
        if (dialog.ShowDialog() == true && dialog.Result is not null)
            await RunOperationAsync(() => _service.UpdateSettingsAsync(dialog.Result), "Сохранение настроек…");
    }

    private void OnFilterChanged(object sender, SelectionChangedEventArgs e)
    {
        if (Filters.SelectedItem is ListBoxItem item) _filter = item.Tag?.ToString() ?? "all";
        if (_view is not null) Refresh();
    }
    private void OnSearchChanged(object sender, TextChangedEventArgs e) { if (_view is not null) Refresh(); }
    private void OnTorrentSelected(object sender, SelectionChangedEventArgs e)
    {
        if (!_synchronizing && _view is not null) Refresh();
    }

    private static string[] DropSources(IDataObject data)
    {
        if (data.GetDataPresent(DataFormats.FileDrop) && data.GetData(DataFormats.FileDrop) is string[] files)
            return files.Where(file => file.EndsWith(".torrent", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (data.GetDataPresent(DataFormats.UnicodeText) && data.GetData(DataFormats.UnicodeText) is string text && text.TrimStart().StartsWith("magnet:?", StringComparison.OrdinalIgnoreCase))
            return [text.Trim()];
        return [];
    }
    private void OnDragOver(object sender, DragEventArgs e) { e.Effects = !_exiting && DropSources(e.Data).Length > 0 ? DragDropEffects.Copy : DragDropEffects.None; e.Handled = true; }
    private async void OnDrop(object sender, DragEventArgs e) { e.Handled = true; await QueueSourcesAsync(DropSources(e.Data)); }
    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (_busy || _exiting) return;
        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.O) { e.Handled = true; OnAddTorrent(this, new RoutedEventArgs()); }
        else if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.M) { e.Handled = true; OnAddMagnet(this, new RoutedEventArgs()); }
        else if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.F) { e.Handled = true; SearchBox.Focus(); SearchBox.SelectAll(); }
        else if (e.Key == Key.Delete && TorrentGrid.IsKeyboardFocusWithin) { e.Handled = true; OnRemove(this, new RoutedEventArgs()); }
    }

    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_allowClose) return;
        e.Cancel = true;
        if (_exiting) return;
        if (_service.Settings.CloseToTray && _tray is not null && !_smokeTest)
        {
            Hide();
            if (!_trayHintShown)
            {
                _trayHintShown = true;
                _tray.ShowBalloonTip(3500, "CurseTorrent работает в фоне", "Загрузки и раздачи продолжаются. Для завершения выберите «Выход» в меню значка.", Forms.ToolTipIcon.Info);
            }
        }
        else await RequestExitAsync();
    }

    public async Task RequestExitAsync()
    {
        if (_exiting) return;
        _exiting = true;
        _timer.Stop();
        SetStatus("Сохранение загрузок и завершение…");
        MainLayout.IsEnabled = false;
        try
        {
            foreach (Window owned in OwnedWindows.Cast<Window>().ToArray()) owned.Close();
            // Let an in-flight add / pause finish before disposing its engine.
            while (_busy) await Task.Delay(50);
            await _service.ShutdownAsync();
            await _service.DisposeAsync();
        }
        catch (Exception exception)
        {
            App.WriteLog(exception);
            if (!_smokeTest) MessageBox.Show(this, "Не удалось полностью сохранить состояние загрузок. При следующем запуске может потребоваться проверка файлов.\n\n" + DialogText.Summary(exception.Message), "Завершение CurseTorrent", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            _tray?.Dispose();
            _trayIcon?.Dispose();
            _allowClose = true;
            Close();
            System.Windows.Application.Current.Shutdown();
        }
    }
}
