using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Xml.Linq;
using CurseTorrent.App;
using CurseTorrent.Core;
using Forms = System.Windows.Forms;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args) => new TestApplication(args.Length == 0 ? "artifacts/ui-tests" : args[0]).Run();
}

internal sealed class TestApplication : System.Windows.Application
{
    private readonly string _output;
    internal TestApplication(string output)
    {
        _output = Path.GetFullPath(output);
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        // Use the exact production resources without starting the real App engine/profile.
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
        var source = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "AppTheme.xaml"));
        var resources = source.Root!.Element(presentation + "Application.Resources")!;
        var dictionary = new XElement(presentation + "ResourceDictionary",
            new XAttribute("xmlns", presentation.NamespaceName), new XAttribute(XNamespace.Xmlns + "x", xaml.NamespaceName),
            resources.Elements().Select(element => new XElement(element)));
        using var reader = dictionary.CreateReader();
        Resources = (ResourceDictionary)XamlReader.Load(reader);
    }
    protected override async void OnStartup(StartupEventArgs e)
    {
        try { Shutdown(await UiTests.RunAsync(_output) ? 0 : 1); }
        catch (Exception error) { Console.WriteLine("UI_TEST_FATAL " + error); Shutdown(1); }
    }
}

internal static class UiTests
{
    private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;
    private static readonly List<object> Results = [];
    private static readonly List<string> Failures = [];
    private static int _checks;
    private static readonly BindingLog BindingErrors = new();
    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr window, out NativeRect rect);
    [DllImport("user32.dll")]
    private static extern bool GetClientRect(IntPtr window, out NativeRect rect);

    private static void Check(bool passed, string name, object? evidence = null)
    {
        _checks++;
        if (!passed) Failures.Add(name);
        var result = new { name, passed, evidence };
        Results.Add(result);
        Console.WriteLine("UI_TEST " + JsonSerializer.Serialize(result));
    }

    internal static async Task<bool> RunAsync(string output)
    {
        Directory.CreateDirectory(output);
        CheckDialogSummaries();
        System.Globalization.CultureInfo.DefaultThreadCurrentCulture = System.Globalization.CultureInfo.GetCultureInfo("ru-RU");
        System.Globalization.CultureInfo.DefaultThreadCurrentUICulture = System.Globalization.CultureInfo.GetCultureInfo("ru-RU");
        PresentationTraceSources.DataBindingSource.Listeners.Add(BindingErrors);
        PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Error;
        var service = new FixtureService();
        var main = new MainWindow(service, smokeTest: true);
        System.Windows.Application.Current.MainWindow = main;
        main.Show();
        await Idle();
        ((DispatcherTimer)typeof(MainWindow).GetField("_timer", PrivateInstance)!.GetValue(main)!).Stop();
        CheckWithinWorkArea(main, "main initial placement");
        var torrents = (DataGrid)main.FindName("TorrentGrid");
        var files = (DataGrid)main.FindName("FilesGrid");
        torrents.SelectedItem = torrents.Items.OfType<TorrentRow>().First(row => row.Id == "active");
        await Idle();
        Check(Descendants<Button>((FrameworkElement)main.FindName("Toolbar")).All(button => InClient(button, main)), "main actions fit initial window");
        SaveImage(main, Path.Combine(output, "01-main-files.png"));

        await ClickHeader(files, 1);
        Check(files.Items.OfType<TorrentFileRow>().Select(row => row.Path).SequenceEqual(new[] { "10-bytes.bin", "2-kib.bin", "900-mib.bin", "100-gib.bin" }),
            "file sizes sort numerically", files.Items.OfType<TorrentFileRow>().Select(row => new { row.Path, row.Size }).ToArray());
        await ClickHeader(files, 2);
        Check(files.Items.OfType<TorrentFileRow>().Select(row => row.Path).SequenceEqual(new[] { "10-bytes.bin", "100-gib.bin", "2-kib.bin", "900-mib.bin" }),
            "file progress sorts numerically", files.Items.OfType<TorrentFileRow>().Select(row => new { row.Path, row.ProgressText }).ToArray());
        service.ProgressOverrides["10-bytes.bin"] = 99;
        service.ProgressOverrides["100-gib.bin"] = 5;
        Refresh(main);
        await Idle();
        Check(files.Items.OfType<TorrentFileRow>().Select(row => row.Path).SequenceEqual(new[] { "100-gib.bin", "2-kib.bin", "10-bytes.bin", "900-mib.bin" }),
            "numeric sorting remains correct after progress changes");
        service.ProgressOverrides.Clear();
        Refresh(main);
        await Idle();
        SaveImage(main, Path.Combine(output, "02-main-sorted-files.png"));

        var firstFile = files.Items.OfType<TorrentFileRow>().First();
        files.SelectedItem = firstFile;
        files.ScrollIntoView(firstFile);
        await Idle();
        var combo = Descendants<ComboBox>(files).First();
        int priorCalls = service.PriorityCalls.Count;
        foreach (var priority in new[] { FilePriority.Skip, FilePriority.Low, FilePriority.Normal, FilePriority.High })
        {
            combo.SelectedValue = priority;
            await Idle();
            string expected = ((PriorityOption)combo.SelectedItem).Label;
            string[] texts = Descendants<TextBlock>(combo).Select(block => block.Text).ToArray();
            Check(texts.Contains(expected) && texts.All(text => !text.Contains("PriorityOption", StringComparison.Ordinal)),
                "priority shows its label " + priority, texts);
        }
        Check(service.PriorityCalls.Count - priorCalls == 4 && service.PriorityCalls.Skip(priorCalls).All(call => call.Id == "active" && call.Path == firstFile.Path),
            "one correct command for each priority change", service.PriorityCalls.Skip(priorCalls).ToArray());
        combo.Focus();
        await Idle();
        var focusBorder = (Border)combo.Template.FindName("ComboBorder", combo);
        Check(combo.IsKeyboardFocusWithin && focusBorder.BorderBrush is SolidColorBrush focus && focus.Color == ((SolidColorBrush)System.Windows.Application.Current.Resources["FocusBrush"]).Color,
            "priority has a visible focused border");
        combo.IsDropDownOpen = true;
        await Idle();
        var popup = (Popup)combo.Template.FindName("PART_Popup", combo);
        string[] popupLabels = Descendants<TextBlock>(popup.Child).Select(block => block.Text).Where(text => !string.IsNullOrWhiteSpace(text)).ToArray();
        Check(popupLabels.SequenceEqual(new[] { "Не скачивать", "Низкий", "Обычный", "Высокий" }), "all priority menu labels are readable", popupLabels);
        Refresh(main);
        await Idle();
        Check((torrents.SelectedItem as TorrentRow)?.Id == "active" && (files.SelectedItem as TorrentFileRow)?.Path == firstFile.Path && combo.IsDropDownOpen && combo.IsKeyboardFocusWithin,
            "refresh preserves selection and open priority focus");
        combo.IsDropDownOpen = false;
        await Idle();

        var detailTabs = (TabControl)main.FindName("DetailsTabs");
        detailTabs.SelectedIndex = 1;
        await Idle();
        SaveImage(main, Path.Combine(output, "03-main-details.png"));
        service.Settings = service.Settings with { ListenPort = 0 };
        typeof(MainWindow).GetField("_statusUntil", PrivateInstance)!.SetValue(main, DateTime.MinValue);
        Refresh(main);
        Check(((TextBlock)main.FindName("StatusLabel")).Text.Contains("Порт автоматически", StringComparison.Ordinal), "automatic port has an honest label");
        Check(Equals(((TextBlock)main.FindName("StatusLabel")).ToolTip, ((TextBlock)main.FindName("StatusLabel")).Text), "ready tooltip matches current status");
        detailTabs.SelectedIndex = 0;
        main.Width = main.MinWidth;
        main.Height = main.MinHeight;
        await Idle();
        main.UpdateLayout();
        CheckWithinWorkArea(main, "main minimum placement");
        Check(torrents.Columns.All(column => column.ActualWidth >= column.MinWidth - 0.1), "minimum window preserves numeric column widths",
            torrents.Columns.Select(column => new { column.Header, column.ActualWidth, column.MinWidth }).ToArray());
        Check(Descendants<Button>((FrameworkElement)main.FindName("Toolbar")).All(button => InClient(button, main)), "main actions fit minimum window");
        await CheckHorizontalScroll(torrents);
        SaveImage(main, Path.Combine(output, "04-main-minimum.png"));
        main.Width = 980;
        main.Height = 700;
        await Idle();

        var settings = new SettingsWindow(service.Settings) { Owner = main };
        settings.Show();
        await Idle();
        CheckWithinWorkArea(settings, "settings initial placement");
        CheckFooter(settings, "SaveButton", "settings default footer");
        SaveImage(settings, Path.Combine(output, "05-settings.png"));
        settings.Width = settings.MinWidth;
        settings.Height = settings.MinHeight;
        ((TextBlock)settings.FindName("ErrorLabel")).Text = LongError();
        await Idle();
        CheckFooter(settings, "SaveButton", "settings long error and short window footer");
        var settingsScroll = Descendants<ScrollViewer>(settings).First(viewer => viewer.ScrollableHeight > 0);
        settingsScroll.ScrollToEnd();
        await Idle();
        Check(settingsScroll.VerticalOffset > 0, "settings form remains scrollable with a long error");
        SaveImage(settings, Path.Combine(output, "10-settings-short-error.png"));
        settings.Close();

        var preview = new TorrentPreview("fixture.torrent", "Проверка интерфейса", 3072, false,
            [new TorrentFilePreview("Documents/readme.txt", 1024), new TorrentFilePreview("Sample/data.bin", 2048)]);
        var add = new AddTorrentWindow(preview, service.Settings.DownloadDirectory) { Owner = main };
        add.Show();
        await Idle();
        CheckWithinWorkArea(add, "add torrent initial placement");
        CheckFooter(add, "AddButton", "add torrent default footer");
        Check(((CheckBox)add.FindName("AllowExistingFilesCheck")).IsChecked == false, "existing files consent is initially off");
        SaveImage(add, Path.Combine(output, "06-add-torrent.png"));
        add.Close();
        foreach (bool consent in new[] { false, true }) TestAddConsent(main, preview, service.Settings.DownloadDirectory, consent);

        var magnet = new MagnetWindow { Owner = main };
        magnet.Show();
        await Idle();
        Check(magnet.IsKeyboardFocusWithin && ((TextBox)magnet.FindName("MagnetBox")).IsKeyboardFocused, "magnet input receives focus");
        CheckFooter(magnet, "ContinueButton", "magnet default footer");
        SaveImage(magnet, Path.Combine(output, "07-magnet.png"));
        magnet.Width = magnet.MinWidth;
        magnet.Height = magnet.MinHeight;
        ((TextBlock)magnet.FindName("ErrorLabel")).Text = LongError();
        await Idle();
        CheckFooter(magnet, "ContinueButton", "magnet short window and long error footer");
        magnet.Close();

        string[] names = ["Linux distribution", new string('W', 255), string.Join(" ", Enumerable.Repeat("Длинное имя раздачи", 100)), new string('Ж', 20000), string.Join("\n", Enumerable.Repeat("Строка названия", 250))];
        for (int index = 0; index < names.Length; index++)
        {
            var longAdd = new AddTorrentWindow(new TorrentPreview("magnet:?xt=urn:btih:0123456789012345678901234567890123456789", names[index], 0, true, []), service.Settings.DownloadDirectory) { Owner = main };
            longAdd.Show();
            await Idle();
            CheckFooter(longAdd, "AddButton", "magnet title length " + names[index].Length + " default footer");
            if (index == 0) SaveImage(longAdd, Path.Combine(output, "08-add-magnet.png"));
            if (index == 2) SaveImage(longAdd, Path.Combine(output, "09-add-long-magnet.png"));
            longAdd.Width = longAdd.MinWidth;
            longAdd.Height = longAdd.MinHeight;
            ((TextBlock)longAdd.FindName("ErrorLabel")).Text = LongError();
            await Idle();
            CheckFooter(longAdd, "AddButton", "magnet title length " + names[index].Length + " short window and long error footer");
            var consent = (CheckBox)longAdd.FindName("AllowExistingFilesCheck");
            consent.BringIntoView();
            await Idle();
            var form = (ScrollViewer)longAdd.FindName("FormScroll");
            Check(form.ScrollableHeight > 0 && form.VerticalOffset > 0, "long form can reach consent " + names[index].Length);
            CheckFooter(longAdd, "AddButton", "scrolling form keeps actions visible " + names[index].Length);
            longAdd.Close();
        }

        ApplyPalette(true);
        await Idle();
        Check(main.Background is SolidColorBrush accessibleBackground && accessibleBackground.Color == SystemColors.WindowColor, "high contrast palette reaches an existing window");
        Check(((SolidColorBrush)System.Windows.Application.Current.Resources["SelectionTextBrush"]).Color == SystemColors.HighlightTextColor, "high contrast selected text uses the system color");
        SaveImage(main, Path.Combine(output, "11-system-palette.png"));
        ApplyPalette(false);
        await Idle();
        Check(main.Background is SolidColorBrush darkBackground && darkBackground.Color == Color.FromRgb(0x10, 0x12, 0x16), "returning from high contrast restores dark theme");
        Check(BindingErrors.Messages.Count == 0, "no WPF binding errors", BindingErrors.Messages.ToArray());
        var summary = new { checks = _checks, failures = Failures.ToArray(), environment = new { framework = RuntimeInformation.FrameworkDescription, os = Environment.OSVersion.ToString(), dpi = VisualTreeHelper.GetDpi(main), workArea = SystemParameters.WorkArea.ToString() }, results = Results };
        File.WriteAllText(Path.Combine(output, "ui-regression-results.json"), JsonSerializer.Serialize(summary, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"UI_TEST_SUMMARY checks={_checks} failed={Failures.Count}");
        typeof(MainWindow).GetField("_allowClose", PrivateInstance)!.SetValue(main, true);
        main.Close();
        return Failures.Count == 0;
    }

    private static void TestAddConsent(Window owner, TorrentPreview preview, string directory, bool consent)
    {
        var dialog = new AddTorrentWindow(preview, directory) { Owner = owner };
        dialog.Loaded += (_, _) => dialog.Dispatcher.BeginInvoke(new Action(() =>
        {
            ((CheckBox)dialog.FindName("AllowExistingFilesCheck")).IsChecked = consent;
            ((Button)dialog.FindName("AddButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        }), DispatcherPriority.ApplicationIdle);
        bool? accepted = dialog.ShowDialog();
        Check(accepted == true && dialog.Request?.AllowExistingFiles == consent, "existing files consent is passed only when selected " + consent);
    }

    private static void CheckDialogSummaries()
    {
        var formatter = typeof(MainWindow).Assembly.GetType("CurseTorrent.App.DialogText")!;
        const BindingFlags flags = BindingFlags.Static | BindingFlags.NonPublic;
        string normal = (string)formatter.GetMethod("SingleLine", flags)!.Invoke(null, ["Обычная загрузка", 100])!;
        Check(normal == "Обычная загрузка", "ordinary confirmation names remain unchanged");
        string name = (string)formatter.GetMethod("SingleLine", flags)!.Invoke(null, [string.Join("\n", Enumerable.Repeat("Название загрузки", 200)), 100])!;
        Check(name.Length <= 100 && !name.Contains('\n') && name.EndsWith('…'), "multiline torrent names cannot expand native confirmation");
        string error = (string)formatter.GetMethod("Summary", flags)!.Invoke(null, [LongError()])!;
        Check(error.Length <= 600 && error.Split('\n').Length <= 6 && error.Contains('…'), "native error summaries bound both lines and characters");
        string warnings = (string)formatter.GetMethod("StartupSummary", flags)!.Invoke(null, [Enumerable.Repeat(LongError(), 10000).ToArray()])!;
        Check(warnings.Length < 700 && warnings.Contains("10000", StringComparison.Ordinal) && warnings.Contains("9997", StringComparison.Ordinal) && warnings.Contains("app.log", StringComparison.Ordinal),
            "startup summary reports the full count without expanding every warning");
    }

    private static async Task CheckHorizontalScroll(DataGrid grid)
    {
        var viewer = Descendants<ScrollViewer>(grid).First(view => view.Name == "DG_ScrollViewer");
        viewer.ScrollToHorizontalOffset(0);
        await Idle();
        var bar = Descendants<ScrollBar>(grid).FirstOrDefault(control => control.Orientation == Orientation.Horizontal && control.Visibility == Visibility.Visible);
        Check(bar is not null && viewer.ScrollableWidth > 0, "narrow torrent table has horizontal scrolling", new { viewer.ScrollableWidth });
        if (bar is null) return;
        var track = (Track)bar.Template.FindName("PART_Track", bar);
        Check(ReferenceEquals(track.IncreaseRepeatButton.Command, ScrollBar.PageRightCommand) && ReferenceEquals(track.DecreaseRepeatButton.Command, ScrollBar.PageLeftCommand), "horizontal scrollbar has horizontal page actions");
        double vertical = viewer.VerticalOffset;
        ScrollBar.PageRightCommand.Execute(null, track.IncreaseRepeatButton);
        await Idle();
        Check(viewer.HorizontalOffset > 0 && viewer.VerticalOffset == vertical, "horizontal page click moves only horizontally", new { viewer.HorizontalOffset, viewer.VerticalOffset });
        ScrollBar.PageLeftCommand.Execute(null, track.DecreaseRepeatButton);
        await Idle();
        Check(viewer.HorizontalOffset == 0 && viewer.VerticalOffset == vertical, "horizontal page back returns to the start");
    }

    private static void CheckWithinWorkArea(Window window, string name)
    {
        var handle = new WindowInteropHelper(window).Handle;
        _ = GetWindowRect(handle, out var bounds);
        var work = Forms.Screen.FromHandle(handle).WorkingArea;
        Check(bounds.Left >= work.Left && bounds.Top >= work.Top && bounds.Right <= work.Right && bounds.Bottom <= work.Bottom, name,
            new { window = new { bounds.Left, bounds.Top, bounds.Right, bounds.Bottom }, workArea = new { work.Left, work.Top, work.Right, work.Bottom } });
    }
    private static void CheckFooter(Window window, string buttonName, string name)
    {
        var primary = (Button)window.FindName(buttonName);
        var cancel = Descendants<Button>(window).First(button => button.IsCancel);
        Check(InClient(primary, window) && InClient(cancel, window), name,
            new { primary = Bounds(primary, window).ToString(), cancel = Bounds(cancel, window).ToString(), window.ActualHeight });
    }
    private static bool InClient(FrameworkElement element, Window window)
    {
        _ = GetClientRect(new WindowInteropHelper(window).Handle, out var client);
        var dpi = VisualTreeHelper.GetDpi(window);
        var available = new Rect(0, 0, client.Right / dpi.DpiScaleX, client.Bottom / dpi.DpiScaleY);
        return element.IsVisible && element.ActualWidth > 0 && element.ActualHeight > 0 && available.Contains(Bounds(element, window));
    }
    private static Rect Bounds(FrameworkElement element, Visual root) => element.TransformToAncestor(root).TransformBounds(new Rect(0, 0, element.ActualWidth, element.ActualHeight));
    private static async Task ClickHeader(DataGrid grid, int column)
    {
        grid.UpdateLayout();
        var header = Descendants<DataGridColumnHeader>(grid).First(control => control.Column == grid.Columns[column]);
        typeof(DataGridColumnHeader).GetMethod("OnClick", PrivateInstance)!.Invoke(header, null);
        await Idle();
    }
    private static void ApplyPalette(bool highContrast) => typeof(MainWindow).Assembly.GetType("CurseTorrent.App.ThemeManager")!.GetMethod("ApplyPalette", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [System.Windows.Application.Current.Resources, highContrast]);
    private static string LongError() => string.Join("\n", Enumerable.Repeat("Папка недоступна. Проверьте путь, права доступа и подключение диска. " + new string('Ж', 80), 80));
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
    private static void SaveImage(Window window, string path)
    {
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth), (int)Math.Ceiling(window.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(window);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(path);
        encoder.Save(file);
        Console.WriteLine("UI_TEST_SCREENSHOT " + Path.GetFileName(path));
    }
}

internal sealed class BindingLog : TraceListener
{
    internal List<string> Messages { get; } = [];
    public override void Write(string? message) { if (!string.IsNullOrWhiteSpace(message)) Messages.Add(message); }
    public override void WriteLine(string? message) { if (!string.IsNullOrWhiteSpace(message)) Messages.Add(message); }
}

internal sealed record PriorityCall(string Id, string Path, string Priority);
internal sealed class FixtureService : ITorrentService
{
    internal List<PriorityCall> PriorityCalls { get; } = [];
    internal Dictionary<string, double> ProgressOverrides { get; } = new(StringComparer.Ordinal);
    private readonly Dictionary<string, FilePriority> _priorities = new(StringComparer.Ordinal);
    private static readonly (string Name, long Bytes, double Progress)[] FileData =
    [("10-bytes.bin", 10, 2), ("100-gib.bin", 100L * 1024 * 1024 * 1024, 12), ("2-kib.bin", 2048, 20), ("900-mib.bin", 900L * 1024 * 1024, 100)];
    public AppSettings Settings { get; set; } = new() { CloseToTray = false };
    public IReadOnlyList<string> StartupWarnings => [];
    public Task InitializeAsync() => Task.CompletedTask;
    public Task<TorrentPreview> InspectAsync(string source) => throw new NotSupportedException();
    public Task<string> AddAsync(AddTorrentRequest request) => throw new NotSupportedException();
    public IReadOnlyList<TorrentSnapshot> GetSnapshots(string? includeFilesForId = null)
    {
        var result = new List<TorrentSnapshot>(4);
        for (int i = 0; i < 4; i++)
        {
            string id = i == 0 ? "active" : "fixture-" + i;
            var state = i switch { 0 => DownloadState.Downloading, 1 => DownloadState.Seeding, 2 => DownloadState.Paused, _ => DownloadState.Error };
            var files = includeFilesForId == id ? FileData.Select(file => new TorrentFileSnapshot(file.Name, file.Bytes, ProgressOverrides.GetValueOrDefault(file.Name, file.Progress), _priorities.GetValueOrDefault(file.Name, FilePriority.Normal))).ToArray() : [];
            long total = FileData.Sum(file => file.Bytes);
            result.Add(new TorrentSnapshot(id, i switch { 0 => "Linux ISO · тестовая загрузка", 1 => "Документы проекта · завершено", 2 => "Архив фотографий · на паузе", _ => "Пример недоступной папки" }, Settings.DownloadDirectory,
                state, i == 1 ? 100 : 37.5, total, total, i == 1 ? total : (long)(total * 0.375), 134217728,
                state == DownloadState.Downloading ? 4 * 1024 * 1024 : 0, state == DownloadState.Seeding ? 512 * 1024 : 0,
                state is DownloadState.Downloading or DownloadState.Seeding ? 12 : 0, 4, state == DownloadState.Downloading ? TimeSpan.FromHours(4) : null,
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
        PriorityCalls.Add(new PriorityCall(id, path, priority.ToString()));
        return Task.CompletedTask;
    }
    public Task UpdateSettingsAsync(AppSettings settings) { Settings = settings; return Task.CompletedTask; }
    public Task ShutdownAsync() => Task.CompletedTask;
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
