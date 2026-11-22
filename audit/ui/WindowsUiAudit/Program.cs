using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Windows.Markup;
using System.Xml.Linq;
using CurseTorrent.App;
using CurseTorrent.Core;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        var app = new AuditApplication(args.Length == 0 ? "ui-audit-output" : args[0]);
        return app.Run();
    }
}

internal sealed class AuditApplication : System.Windows.Application
{
    private readonly string _outputDirectory;
    internal AuditApplication(string outputDirectory)
    {
        _outputDirectory = outputDirectory;
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        // Load the exact production resource definitions without constructing the
        // production App, whose startup would create an engine and user profile.
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
        var source = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "AppTheme.xaml"));
        var resources = source.Root!.Element(presentation + "Application.Resources")!;
        var dictionary = new XElement(presentation + "ResourceDictionary",
            new XAttribute("xmlns", presentation.NamespaceName),
            new XAttribute(XNamespace.Xmlns + "x", xaml.NamespaceName),
            resources.Elements().Select(element => new XElement(element)));
        using var reader = dictionary.CreateReader();
        Resources = (ResourceDictionary)XamlReader.Load(reader);
    }

    protected override async void OnStartup(StartupEventArgs e)
    {
        try
        {
            await Audit.RunAsync(Path.GetFullPath(_outputDirectory));
            Shutdown(0);
        }
        catch (Exception error)
        {
            Console.WriteLine("AUDIT_FATAL " + error);
            Shutdown(1);
        }
    }
}

internal static class Audit
{
    private static readonly List<object> Results = [];
    private static readonly BindingLog BindingErrors = new();
    private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
    private static void Record(string test, object data)
    {
        var item = new { test, data };
        Results.Add(item);
        Console.WriteLine("AUDIT_JSON " + JsonSerializer.Serialize(item));
    }

    public static async Task RunAsync(string output)
    {
        Directory.CreateDirectory(output);
        System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.GetCultureInfo("ru-RU");
        PresentationTraceSources.DataBindingSource.Listeners.Add(BindingErrors);
        PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Error;
        var service = new AuditService();
        var main = new MainWindow(service, smokeTest: true);
        System.Windows.Application.Current.MainWindow = main;
        main.Show();
        await Idle();
        ((DispatcherTimer)typeof(MainWindow).GetField("_timer", PrivateInstance)!.GetValue(main)!).Stop();
        var torrents = (DataGrid)main.FindName("TorrentGrid");
        var files = (DataGrid)main.FindName("FilesGrid");
        torrents.SelectedItem = torrents.Items.OfType<TorrentRow>().First(row => row.Id == "active");
        await Idle();
        main.UpdateLayout();
        Record("environment", new
        {
            framework = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
            os = Environment.OSVersion.ToString(),
            dpi = VisualTreeHelper.GetDpi(main),
            workArea = SystemParameters.WorkArea.ToString(),
            main = Geometry(main),
            mainMinimum = new { width = main.MinWidth, height = main.MinHeight }
        });
        SaveImage(main, Path.Combine(output, "01-main-files.png"));

        // Invoke the exact WPF column-header action used by a mouse click.
        await ClickHeader(files, 1);
        Record("file-size-sort-ascending", new
        {
            direction = files.Columns[1].SortDirection.ToString(),
            order = files.Items.OfType<TorrentFileRow>().Select(row => new { row.Path, row.Size }).ToArray(),
            expectedAscending = new[] { "10-bytes.bin", "2-kib.bin", "900-mib.bin", "100-gib.bin" },
            isNumericAscending = files.Items.OfType<TorrentFileRow>().Select(row => row.Path).SequenceEqual(new[] { "10-bytes.bin", "2-kib.bin", "900-mib.bin", "100-gib.bin" })
        });
        await ClickHeader(files, 2);
        Record("file-progress-sort-ascending", new
        {
            direction = files.Columns[2].SortDirection.ToString(),
            order = files.Items.OfType<TorrentFileRow>().Select(row => new { row.Path, row.ProgressText }).ToArray(),
            expectedAscending = new[] { "10-bytes.bin", "100-gib.bin", "2-kib.bin", "900-mib.bin" },
            isNumericAscending = files.Items.OfType<TorrentFileRow>().Select(row => row.Path).SequenceEqual(new[] { "10-bytes.bin", "100-gib.bin", "2-kib.bin", "900-mib.bin" })
        });
        SaveImage(main, Path.Combine(output, "02-main-sorted-progress.png"));
        var firstFile = files.Items.OfType<TorrentFileRow>().First();
        files.SelectedItem = firstFile;
        files.ScrollIntoView(firstFile);
        await Idle();
        var combo = Descendants<ComboBox>(files).First();
        Record("priority-display", new
        {
            expectedLabel = (combo.SelectedItem as PriorityOption)?.Label,
            renderedText = Descendants<TextBlock>(combo).Select(block => block.Text).Where(text => !string.IsNullOrWhiteSpace(text)).ToArray(),
            combo.DisplayMemberPath,
            selectedValue = combo.SelectedValue?.ToString()
        });
        combo.Focus();
        combo.IsDropDownOpen = true;
        await Idle();
        var popup = (Popup)combo.Template.FindName("PART_Popup", combo);
        Record("priority-dropdown-display", new
        {
            renderedText = Descendants<TextBlock>(popup.Child).Select(block => block.Text).Where(text => !string.IsNullOrWhiteSpace(text)).ToArray(),
            expectedLabels = new[] { "Не скачивать", "Низкий", "Обычный", "Высокий" }
        });
        string? selectedBefore = (torrents.SelectedItem as TorrentRow)?.Id;
        string? fileBefore = (files.SelectedItem as TorrentFileRow)?.Path;
        Refresh(main);
        await Idle();
        Record("timer-refresh-dropdown", new
        {
            mainSelectionPreserved = selectedBefore == (torrents.SelectedItem as TorrentRow)?.Id,
            fileSelectionPreserved = fileBefore == (files.SelectedItem as TorrentFileRow)?.Path,
            sameComboAttached = PresentationSource.FromVisual(combo) is not null,
            combo.IsDropDownOpen,
            combo.IsKeyboardFocusWithin,
            focusedElement = Keyboard.FocusedElement?.GetType().Name
        });
        combo.IsDropDownOpen = false;
        await Idle();

        // Trigger a real two-way priority binding through the rendered ComboBox.
        var rowForCombo = (TorrentFileRow)combo.DataContext;
        var newPriority = rowForCombo.Priority == FilePriority.High ? FilePriority.Low : FilePriority.High;
        int priorCalls = service.PriorityCalls.Count;
        combo.SelectedValue = newPriority;
        await Idle();
        Record("priority-binding", new
        {
            calls = service.PriorityCalls.Count - priorCalls,
            last = service.PriorityCalls.LastOrDefault(),
            expectedPath = rowForCombo.Path,
            expectedPriority = newPriority.ToString()
        });

        var detailTabs = (TabControl)main.FindName("DetailsTabs");
        detailTabs.SelectedIndex = 1;
        await Idle();
        SaveImage(main, Path.Combine(output, "03-main-details.png"));
        detailTabs.SelectedIndex = 0;
        main.Width = main.MinWidth;
        main.Height = main.MinHeight;
        await Idle();
        main.UpdateLayout();
        SaveImage(main, Path.Combine(output, "04-main-minimum.png"));
        await CheckHorizontalScroll(torrents);
        Record("minimum-main-layout", new
        {
            main = Geometry(main),
            torrentGrid = Bounds(torrents, main),
            fileGrid = Bounds(files, main),
            toolbar = Bounds((FrameworkElement)main.FindName("Toolbar"), main),
            columns = torrents.Columns.Select(column => new { header = column.Header?.ToString(), requested = column.Width.ToString(), actual = column.ActualWidth, minimum = column.MinWidth }).ToArray(),
            scrollViewers = Descendants<ScrollViewer>(torrents).Select(viewer => new { viewer.Name, viewer.ScrollableWidth, viewer.ScrollableHeight, viewer.ViewportWidth, viewer.ExtentWidth }).ToArray(),
            notes = "Native DPI is reported above. This is the declared minimum size, not a simulated DPI change."
        });
        main.Width = 1250;
        main.Height = 800;
        await Idle();

        var settings = new SettingsWindow(service.Settings) { Owner = main };
        settings.Show();
        await Idle();
        settings.UpdateLayout();
        SaveImage(settings, Path.Combine(output, "05-settings.png"));
        Record("settings-window", new { actual = Geometry(settings), minimum = new { width = settings.MinWidth, height = settings.MinHeight } });
        settings.Close();

        var preview = new TorrentPreview("fixture.torrent", "CurseTorrent interface audit", 3072, false,
            [new TorrentFilePreview("Documents/readme.txt", 1024), new TorrentFilePreview("Sample/data.bin", 2048)]);
        var add = new AddTorrentWindow(preview, service.Settings.DownloadDirectory) { Owner = main };
        add.Show();
        await Idle();
        add.UpdateLayout();
        SaveImage(add, Path.Combine(output, "06-add-torrent.png"));
        add.Close();

        var magnet = new MagnetWindow { Owner = main };
        magnet.Show();
        await Idle();
        SaveImage(magnet, Path.Combine(output, "07-magnet.png"));
        magnet.Close();

        var pendingMagnet = new AddTorrentWindow(new TorrentPreview("magnet:?xt=urn:btih:0123456789012345678901234567890123456789", "Linux distribution", 0, true, []), service.Settings.DownloadDirectory) { Owner = main };
        pendingMagnet.Show();
        await Idle();
        SaveImage(pendingMagnet, Path.Combine(output, "08-add-magnet.png"));
        pendingMagnet.Close();

        string longName = string.Join(" ", Enumerable.Repeat("Длинное имя раздачи", 100));
        string longMagnet = "magnet:?xt=urn:btih:0123456789012345678901234567890123456789&dn=" + Uri.EscapeDataString(longName);
        await using (var inspector = new TorrentService(Path.Combine(output, "inspect-only"), offlineMode: true))
        {
            var longPreview = await inspector.InspectAsync(longMagnet);
            var longAdd = new AddTorrentWindow(longPreview, service.Settings.DownloadDirectory) { Owner = main };
            longAdd.Show();
            await Idle();
            longAdd.UpdateLayout();
            var addButton = Descendants<Button>(longAdd).First(button => button.Content as string == "Добавить");
            Rect addBounds = Bounds(addButton, longAdd);
            Rect client = new(0, 0, longAdd.ActualWidth, longAdd.ActualHeight);
            Record("long-magnet-name-layout", new
            {
                magnetCharacters = longMagnet.Length,
                displayNameCharacters = longPreview.Name.Length,
                acceptedByRealCoreInspector = true,
                window = Geometry(longAdd),
                addButton = addBounds.ToString(),
                addButtonFullyInWindow = client.Contains(addBounds),
                title = Bounds((FrameworkElement)longAdd.FindName("TorrentName"), longAdd).ToString()
            });
            SaveImage(longAdd, Path.Combine(output, "09-add-long-magnet.png"));
            longAdd.Close();
        }

        // Screenshots and timings use synthetic snapshots only, not network activity.
        foreach (int count in new[] { 100, 1000, 10000 })
        {
            service.RowCount = count;
            var watch = Stopwatch.StartNew();
            Refresh(main);
            main.UpdateLayout();
            watch.Stop();
            Record("refresh-timing", new { torrentCount = count, elapsedMilliseconds = watch.Elapsed.TotalMilliseconds, includesInitialRows = true });
            watch.Restart();
            Refresh(main);
            main.UpdateLayout();
            watch.Stop();
            Record("refresh-timing", new { torrentCount = count, elapsedMilliseconds = watch.Elapsed.TotalMilliseconds, includesInitialRows = false });
        }
        service.RowCount = 4;
        Refresh(main);
        await Idle();
        Record("binding-errors", new { count = BindingErrors.Messages.Count, messages = BindingErrors.Messages.ToArray() });
        File.WriteAllText(Path.Combine(output, "ui-audit-results.json"), JsonSerializer.Serialize(Results, new JsonSerializerOptions { WriteIndented = true }));
        typeof(MainWindow).GetField("_allowClose", PrivateInstance)!.SetValue(main, true);
        main.Close();
    }

    private static async Task CheckHorizontalScroll(DataGrid grid)
    {
        var viewer = Descendants<ScrollViewer>(grid).First();
        viewer.ScrollToHorizontalOffset(0);
        await Idle();
        var bar = Descendants<ScrollBar>(grid).FirstOrDefault(b => b.Orientation == Orientation.Horizontal && b.Visibility == Visibility.Visible);
        if (bar is null)
        {
            Record("horizontal-scroll-page-click", new { available = false, viewer.ScrollableWidth });
            return;
        }
        var track = (Track)bar.Template.FindName("PART_Track", bar);
        double before = viewer.HorizontalOffset;
        double verticalBefore = viewer.VerticalOffset;
        var button = track.IncreaseRepeatButton;
        bool canExecute = button.Command is RoutedCommand routed && routed.CanExecute(button.CommandParameter, button);
        if (button.Command is RoutedCommand command && canExecute) command.Execute(button.CommandParameter, button);
        await Idle();
        Record("horizontal-scroll-page-click", new
        {
            available = true,
            viewer.ScrollableWidth,
            command = (button.Command as RoutedCommand)?.Name,
            canExecute,
            horizontalBefore = before,
            horizontalAfter = viewer.HorizontalOffset,
            verticalBefore,
            verticalAfter = viewer.VerticalOffset
        });
    }

    private static async Task ClickHeader(DataGrid grid, int column)
    {
        grid.UpdateLayout();
        var header = Descendants<DataGridColumnHeader>(grid).First(h => h.Column == grid.Columns[column]);
        typeof(DataGridColumnHeader).GetMethod("OnClick", PrivateInstance)!.Invoke(header, null);
        await Idle();
    }
    private static void Refresh(MainWindow window) => typeof(MainWindow).GetMethod("Refresh", PrivateInstance)!.Invoke(window, null);
    private static Task Idle() => System.Windows.Application.Current.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle).Task;
    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) yield return match;
            foreach (var nested in Descendants<T>(child)) yield return nested;
        }
    }
    private static Rect Bounds(FrameworkElement element, Visual root) => element.TransformToAncestor(root).TransformBounds(new Rect(0, 0, element.ActualWidth, element.ActualHeight));
    private static object Geometry(Window window) => new { width = window.ActualWidth, height = window.ActualHeight, left = window.Left, top = window.Top };
    private static void SaveImage(Window window, string path)
    {
        // Render the complete WPF Window visual. Rendering its Content directly
        // with a Margin crops 26 pixels from the dialog and loses Window.Background.
        FrameworkElement visual = window;
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(visual.ActualWidth), (int)Math.Ceiling(visual.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(path);
        encoder.Save(file);
        Console.WriteLine("AUDIT_SCREENSHOT " + Path.GetFileName(path));
    }
}

internal sealed class BindingLog : TraceListener
{
    internal List<string> Messages { get; } = [];
    public override void Write(string? message) { if (!string.IsNullOrWhiteSpace(message)) Messages.Add(message); }
    public override void WriteLine(string? message) { if (!string.IsNullOrWhiteSpace(message)) Messages.Add(message); }
}

internal sealed class AuditService : ITorrentService
{
    internal int RowCount { get; set; } = 4;
    internal List<object> PriorityCalls { get; } = [];
    private readonly Dictionary<string, FilePriority> _priorities = new(StringComparer.Ordinal);
    private static readonly (string Name, long Bytes, double Progress)[] FileData =
    [
        ("10-bytes.bin", 10, 2),
        ("100-gib.bin", 100L * 1024 * 1024 * 1024, 12),
        ("2-kib.bin", 2048, 20),
        ("900-mib.bin", 900L * 1024 * 1024, 100)
    ];
    public AppSettings Settings { get; private set; } = new() { CloseToTray = false };
    public IReadOnlyList<string> StartupWarnings => [];
    public Task InitializeAsync() => Task.CompletedTask;
    public Task<TorrentPreview> InspectAsync(string source) => throw new NotSupportedException();
    public Task<string> AddAsync(AddTorrentRequest request) => throw new NotSupportedException();
    public IReadOnlyList<TorrentSnapshot> GetSnapshots(string? includeFilesForId = null)
    {
        var result = new List<TorrentSnapshot>(RowCount);
        for (int i = 0; i < RowCount; i++)
        {
            string id = i == 0 ? "active" : "fixture-" + i;
            var state = (i % 4) switch { 0 => DownloadState.Downloading, 1 => DownloadState.Seeding, 2 => DownloadState.Paused, _ => DownloadState.Error };
            var files = includeFilesForId == id ? FileData.Select(f => new TorrentFileSnapshot(f.Name, f.Bytes, f.Progress, _priorities.GetValueOrDefault(f.Name, FilePriority.Normal))).ToArray() : [];
            result.Add(new TorrentSnapshot(id, i switch { 0 => "Linux ISO · текущая загрузка", 1 => "Документы проекта · завершено", 2 => "Архив фотографий · на паузе", 3 => "Пример недоступной папки", _ => "Synthetic torrent " + i }, Settings.DownloadDirectory,
                state, i == 1 ? 100 : 37.5, 2L * 1024 * 1024 * 1024, 2L * 1024 * 1024 * 1024, 805306368, 134217728,
                state == DownloadState.Downloading ? 4 * 1024 * 1024 : 0, state == DownloadState.Seeding ? 512 * 1024 : 0,
                state is DownloadState.Downloading or DownloadState.Seeding ? 12 : 0, 4, state == DownloadState.Downloading ? TimeSpan.FromMinutes(4) : null,
                state == DownloadState.Error ? "Нет доступа к папке загрузки. Проверьте подключение диска." : null, true, files));
        }
        return result;
    }
    public Task ResumeAsync(string id) => Task.CompletedTask;
    public Task PauseAsync(string id) => Task.CompletedTask;
    public Task RecheckAsync(string id) => Task.CompletedTask;
    public Task RemoveAsync(string id) => Task.CompletedTask;
    public Task SetFilePriorityAsync(string id, string path, FilePriority priority)
    {
        _priorities[path] = priority;
        PriorityCalls.Add(new { id, path, priority = priority.ToString() });
        return Task.CompletedTask;
    }
    public Task UpdateSettingsAsync(AppSettings settings) { Settings = settings; return Task.CompletedTask; }
    public Task ShutdownAsync() => Task.CompletedTask;
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
