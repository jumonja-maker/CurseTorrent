using System;
using System.IO;
using Microsoft.Win32;

namespace CurseTorrent.App;

internal static class FileAssociations
{
    internal static void Register()
    {
        string executable = Environment.ProcessPath ?? throw new InvalidOperationException("Не найден путь приложения.");
        if (!string.Equals(Path.GetFileName(executable), "CurseTorrent.exe", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Для регистрации запустите установленный CurseTorrent.exe.");
        string command = $"\"{executable}\" \"%1\"";
        string icon = $"\"{executable}\",0";
        Set(@"Software\Classes\CurseTorrent.Torrent", "", "Торрент-файл CurseTorrent");
        Set(@"Software\Classes\CurseTorrent.Torrent\DefaultIcon", "", icon);
        Set(@"Software\Classes\CurseTorrent.Torrent\shell\open\command", "", command);
        Set(@"Software\Classes\CurseTorrent.Magnet", "", "Magnet-ссылка CurseTorrent");
        Set(@"Software\Classes\CurseTorrent.Magnet", "URL Protocol", "");
        Set(@"Software\Classes\CurseTorrent.Magnet\DefaultIcon", "", icon);
        Set(@"Software\Classes\CurseTorrent.Magnet\shell\open\command", "", command);
        Set(@"Software\Classes\.torrent\OpenWithProgids", "CurseTorrent.Torrent", "");
        Set(@"Software\Classes\Applications\CurseTorrent.exe", "FriendlyAppName", "CurseTorrent");
        Set(@"Software\Classes\Applications\CurseTorrent.exe\SupportedTypes", ".torrent", "");
        Set(@"Software\Classes\Applications\CurseTorrent.exe\shell\open\command", "", command);
        Set(@"Software\CurseTorrent\Capabilities", "ApplicationName", "CurseTorrent");
        Set(@"Software\CurseTorrent\Capabilities", "ApplicationDescription", "Классический клиент BitTorrent");
        Set(@"Software\CurseTorrent\Capabilities", "ApplicationIcon", icon);
        Set(@"Software\CurseTorrent\Capabilities\FileAssociations", ".torrent", "CurseTorrent.Torrent");
        Set(@"Software\CurseTorrent\Capabilities\URLAssociations", "magnet", "CurseTorrent.Magnet");
        Set(@"Software\RegisteredApplications", "CurseTorrent", @"Software\CurseTorrent\Capabilities");
    }
    private static void Set(string path, string name, string value)
    {
        using var key = Registry.CurrentUser.CreateSubKey(path) ?? throw new IOException("Не удалось зарегистрировать приложение для текущего пользователя.");
        key.SetValue(name, value, RegistryValueKind.String);
    }
}
