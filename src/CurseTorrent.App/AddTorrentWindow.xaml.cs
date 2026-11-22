using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Windows;
using CurseTorrent.Core;
using Microsoft.Win32;

namespace CurseTorrent.App;

public partial class AddTorrentWindow : Window
{
    private readonly TorrentPreview _preview;
    private readonly ObservableCollection<SelectableFileRow> _files = [];
    private bool _changingSelection;
    public AddTorrentRequest? Request { get; private set; }

    public AddTorrentWindow(TorrentPreview preview, string defaultDirectory)
    {
        _preview = preview;
        InitializeComponent();
        WindowTheme.Attach(this);
        TorrentName.Text = preview.Name;
        TorrentSize.Text = preview.IsMagnet ? "Magnet-ссылка" : $"{preview.Files.Count} файлов · {Display.Bytes(preview.SizeBytes)}";
        DirectoryBox.Text = defaultDirectory;
        foreach (var file in preview.Files)
        {
            var row = new SelectableFileRow(file);
            row.PropertyChanged += (_, _) => { if (!_changingSelection) UpdateSummary(); };
            _files.Add(row);
        }
        FilesGrid.ItemsSource = _files;
        SizeChanged += (_, _) => FilesPanel.Height = Math.Clamp(ActualHeight - 520, 100, 320);
        if (preview.IsMagnet)
        {
            FilesGrid.Visibility = SelectionControls.Visibility = Visibility.Collapsed;
            MagnetHint.Visibility = Visibility.Visible;
        }
        UpdateSummary();
    }

    private void UpdateSummary()
    {
        SelectionSummary.Text = _preview.IsMagnet ? "Все файлы будут выбраны автоматически после получения метаданных." : $"Выбрано {_files.Count(file => file.IsSelected)} из {_files.Count} · {Display.Bytes(_files.Where(file => file.IsSelected).Sum(file => file.SizeBytes))}";
    }
    private void OnSelectAll(object sender, RoutedEventArgs e) => SelectAll(true);
    private void OnSelectNone(object sender, RoutedEventArgs e) => SelectAll(false);
    private void SelectAll(bool selected)
    {
        _changingSelection = true;
        try { foreach (var file in _files) file.IsSelected = selected; }
        finally { _changingSelection = false; UpdateSummary(); }
    }
    private void OnBrowse(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Папка для загрузки" };
        if (Directory.Exists(DirectoryBox.Text)) dialog.InitialDirectory = DirectoryBox.Text;
        if (dialog.ShowDialog(this) == true) DirectoryBox.Text = dialog.FolderName;
    }
    private void OnAdd(object sender, RoutedEventArgs e)
    {
        try
        {
            string directory = DirectoryBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(directory) || !Path.IsPathFullyQualified(directory)) throw new ArgumentException("Выберите папку или укажите полный путь, например D:\\Загрузки.");
            directory = Path.GetFullPath(directory);
            if (!_preview.IsMagnet && !_files.Any(file => file.IsSelected)) throw new ArgumentException("Выберите хотя бы один файл для скачивания.");
            var priorities = _preview.IsMagnet ? null : _files.ToDictionary(file => file.Path, file => file.IsSelected ? FilePriority.Normal : FilePriority.Skip, StringComparer.Ordinal);
            Request = new AddTorrentRequest(_preview.Source, directory, StartCheckBox.IsChecked == true, priorities,
                AllowExistingFiles: AllowExistingFilesCheck.IsChecked == true);
            DialogResult = true;
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        { ErrorLabel.Text = exception.Message; ErrorLabel.BringIntoView(); }
    }
}
