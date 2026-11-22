using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CurseTorrent.Core;

namespace CurseTorrent.App;

public partial class App : System.Windows.Application
{
    private SingleInstance? _instance;
    private MainWindow? _window;
    private readonly List<string[]> _pendingSources = [];
    private bool _smokeTest;
    private static readonly object LogLock = new();

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        ThemeManager.Initialize();
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("ru-RU");
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("ru-RU");
        _smokeTest = e.Args.Contains("--smoke-test", StringComparer.Ordinal);
        DispatcherUnhandledException += OnDispatcherException;
        TaskScheduler.UnobservedTaskException += (_, args) => { WriteLog(args.Exception); args.SetObserved(); };
        try
        {
            if (_smokeTest)
            {
                await RunSmokeTestAsync(e.Args);
                return;
            }
            _instance = new SingleInstance();
            var sources = e.Args.Where(arg => !arg.StartsWith("--", StringComparison.Ordinal)).ToArray();
            if (!_instance.IsPrimary)
            {
                await _instance.ForwardAsync(sources);
                Shutdown();
                return;
            }
            _instance.StartServer(incoming => Dispatcher.BeginInvoke(new Action(() => ReceiveSources(incoming))));
            var service = new TorrentService();
            await service.InitializeAsync();
            _window = new MainWindow(service);
            MainWindow = _window;
            _window.Show();
            _window.ShowStartupWarnings();
            await _window.QueueSourcesAsync(sources);
            foreach (var pending in _pendingSources.ToArray()) await _window.QueueSourcesAsync(pending);
            _pendingSources.Clear();
        }
        catch (Exception exception)
        {
            WriteLog(exception);
            if (!_smokeTest) MessageBox.Show("Не удалось запустить CurseTorrent.\n\n" + DialogText.Summary(exception.Message) + "\n\nЕсли приложение уже запущено, откройте его через значок в трее.", "CurseTorrent", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    private async void ReceiveSources(string[] sources)
    {
        try
        {
            if (_window is null) { _pendingSources.Add(sources); return; }
            _window.ShowAndActivate();
            await _window.QueueSourcesAsync(sources);
        }
        catch (Exception exception) { WriteLog(exception); }
    }

    private async Task RunSmokeTestAsync(string[] args)
    {
        var bindingTrace = new BindingFailureListener();
        PresentationTraceSources.DataBindingSource.Listeners.Add(bindingTrace);
        PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Error;
        var service = new SmokeTorrentService { IncludeTestTorrent = true };
        _window = new MainWindow(service, true);
        MainWindow = _window;
        _window.Show();
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        VerifyWindowTheme(_window);
        _window.RefreshSmokeView(true);
        // Materialize actual row/file templates and every dialog, without network traffic.
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        _window.UpdateLayout();
        Window[] dialogs =
        [
            new SettingsWindow(service.Settings),
            new MagnetWindow(),
            new AddTorrentWindow(new TorrentPreview("ui-test.torrent", "Проверка интерфейса", 2097152, false,
                [new TorrentFilePreview("Папка/Тест.bin", 1048576), new TorrentFilePreview("Тест 2.bin", 1048576)]), service.Settings.DownloadDirectory),
            new AddTorrentWindow(new TorrentPreview("magnet:?", "Проверка magnet", 0, true, []), service.Settings.DownloadDirectory)
        ];
        foreach (var dialog in dialogs)
        {
            dialog.Owner = _window;
            dialog.Show();
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            dialog.UpdateLayout();
            VerifyWindowTheme(dialog);
            dialog.Close();
        }
        service.IncludeTestTorrent = false;
        _window.RefreshSmokeView(false);
        await Task.Delay(700);
        _window.UpdateLayout();
        int screenshotIndex = Array.IndexOf(args, "--smoke-test-screenshot");
        if (screenshotIndex >= 0 && screenshotIndex + 1 < args.Length)
        {
            string path = Path.GetFullPath(args[screenshotIndex + 1]);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var visual = (FrameworkElement)_window.Content;
            var bitmap = new RenderTargetBitmap((int)Math.Ceiling(visual.ActualWidth), (int)Math.Ceiling(visual.ActualHeight), 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(visual);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var file = File.Create(path);
            encoder.Save(file);
        }
        if (bindingTrace.HasErrors) throw new InvalidOperationException("WPF binding errors: " + bindingTrace.Messages);
        await _window.RequestExitAsync();
    }

    private static void VerifyWindowTheme(Window window)
    {
        Color expected = SystemParameters.HighContrast ? SystemColors.WindowColor : Color.FromRgb(0x10, 0x12, 0x16);
        if (window.Background is not SolidColorBrush background ||
            background.Color != expected || background.Opacity != 1)
            throw new InvalidOperationException($"The active theme was not applied to {window.GetType().Name}.");
    }

    private void OnDispatcherException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        WriteLog(e.Exception);
        e.Handled = true;
        if (_smokeTest) { Shutdown(1); return; }
        MessageBox.Show("Не удалось выполнить действие.\n\n" + DialogText.Summary(e.Exception.Message), "CurseTorrent", MessageBoxButton.OK, MessageBoxImage.Error);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _instance?.Dispose();
        base.OnExit(e);
    }

    internal static void WriteLog(Exception exception) => WriteLog(exception.ToString());

    internal static void WriteLog(string details)
    {
        try
        {
            string directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CurseTorrent");
            Directory.CreateDirectory(directory);
            string path = Path.Combine(directory, "app.log");
            lock (LogLock)
            {
                if (File.Exists(path) && new FileInfo(path).Length > 2 * 1024 * 1024) File.Move(path, path + ".previous", true);
                File.AppendAllText(path, $"{DateTimeOffset.Now:O}\n{details}\n\n", Encoding.UTF8);
            }
        }
        catch (Exception) { /* A logging failure must never replace the original error. */ }
    }
}

internal sealed class BindingFailureListener : TraceListener
{
    private readonly StringBuilder _messages = new();
    internal bool HasErrors => _messages.Length > 0;
    internal string Messages => _messages.ToString();
    public override void Write(string? message) => _messages.Append(message);
    public override void WriteLine(string? message) => _messages.AppendLine(message);
}

internal sealed class SmokeTorrentService : ITorrentService
{
    internal bool IncludeTestTorrent { get; set; }
    public AppSettings Settings { get; private set; } = new();
    public IReadOnlyList<string> StartupWarnings => [];
    public Task InitializeAsync() => Task.CompletedTask;
    public Task<TorrentPreview> InspectAsync(string source) => throw new NotSupportedException();
    public Task<string> AddAsync(AddTorrentRequest request) => throw new NotSupportedException();
    public IReadOnlyList<TorrentSnapshot> GetSnapshots(string? includeFilesForId = null) => IncludeTestTorrent
        ? [new TorrentSnapshot("ui-smoke", "Проверка интерфейса · тестовые данные", Settings.DownloadDirectory, DownloadState.Downloading,
            37.5, 2097152, 2097152, 786432, 0, 0, 0, 0, 0, null, null, true,
            includeFilesForId == "ui-smoke" ? [new TorrentFileSnapshot("Папка/Тест.bin", 1048576, 75, FilePriority.High), new TorrentFileSnapshot("Тест 2.bin", 1048576, 0, FilePriority.Normal)] : [])]
        : [];
    public Task ResumeAsync(string id) => Task.CompletedTask;
    public Task PauseAsync(string id) => Task.CompletedTask;
    public Task RecheckAsync(string id) => Task.CompletedTask;
    public Task RemoveAsync(string id) => Task.CompletedTask;
    public Task SetFilePriorityAsync(string id, string path, FilePriority priority) => Task.CompletedTask;
    public Task UpdateSettingsAsync(AppSettings settings) { Settings = settings; return Task.CompletedTask; }
    public Task ShutdownAsync() => Task.CompletedTask;
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
