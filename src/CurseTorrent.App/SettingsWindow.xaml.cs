using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using CurseTorrent.Core;
using Microsoft.Win32;

namespace CurseTorrent.App;

public partial class SettingsWindow : Window
{
    private readonly AppSettings _original;
    public AppSettings? Result { get; private set; }
    public SettingsWindow(AppSettings settings)
    {
        _original = settings;
        InitializeComponent();
        WindowTheme.Attach(this);
        DirectoryBox.Text = settings.DownloadDirectory;
        DownloadBox.Text = settings.DownloadLimitKiB.ToString(CultureInfo.InvariantCulture);
        UploadBox.Text = settings.UploadLimitKiB.ToString(CultureInfo.InvariantCulture);
        PortBox.Text = settings.ListenPort.ToString(CultureInfo.InvariantCulture);
        ConnectionsBox.Text = settings.MaximumConnections.ToString(CultureInfo.InvariantCulture);
        ResumeCheck.IsChecked = settings.ResumeOnStartup;
        TrayCheck.IsChecked = settings.CloseToTray;
        PortForwardingCheck.IsChecked = settings.EnablePortForwarding;
    }
    private void OnBrowse(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFolderDialog { Title = "Папка по умолчанию" };
        if (Directory.Exists(DirectoryBox.Text)) picker.InitialDirectory = DirectoryBox.Text;
        if (picker.ShowDialog(this) == true) DirectoryBox.Text = picker.FolderName;
    }
    private static int Number(string text, string name, int minimum, int maximum)
    {
        if (!int.TryParse(text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int value) || value < minimum || value > maximum)
            throw new ArgumentException($"{name}: введите целое число от {minimum} до {maximum}.");
        return value;
    }
    private void OnSave(object sender, RoutedEventArgs e)
    {
        try
        {
            string directory = DirectoryBox.Text.Trim();
            if (!Path.IsPathFullyQualified(directory)) throw new ArgumentException("Укажите полный путь к папке загрузки.");
            int port = Number(PortBox.Text, "Порт", 0, 65535);
            if (port is > 0 and < 1024) throw new ArgumentException("Порт: 0 для автоматического выбора или число от 1024 до 65535.");
            Result = _original with
            {
                DownloadDirectory = Path.GetFullPath(directory),
                DownloadLimitKiB = Number(DownloadBox.Text, "Скачивание", 0, 2_097_151),
                UploadLimitKiB = Number(UploadBox.Text, "Отдача", 0, 2_097_151),
                ListenPort = port,
                MaximumConnections = Number(ConnectionsBox.Text, "Соединения", 10, 2000),
                ResumeOnStartup = ResumeCheck.IsChecked == true,
                CloseToTray = TrayCheck.IsChecked == true,
                EnablePortForwarding = PortForwardingCheck.IsChecked == true
            };
            DialogResult = true;
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        { ShowError(exception); }
    }
    private void OnRegister(object sender, RoutedEventArgs e)
    {
        try { FileAssociations.Register(); RegistrationLabel.Text = "Зарегистрировано. Теперь выберите CurseTorrent в «Приложениях по умолчанию»."; }
        catch (Exception exception) { ShowError(exception); }
    }
    private void OnDefaultApps(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo("ms-settings:defaultapps") { UseShellExecute = true }); }
        catch (Exception exception) { ShowError(exception); }
    }
    private void ShowError(Exception exception) { ErrorLabel.Text = exception.Message; ErrorLabel.BringIntoView(); }
}
