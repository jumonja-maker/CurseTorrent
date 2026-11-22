using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace CurseTorrent.Core;

internal static class PayloadFileIdentity
{
    [StructLayout(LayoutKind.Sequential)]
    private struct FileIdInfo { public ulong VolumeSerialNumber; public Guid FileId; }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle handle, int informationClass,
        out FileIdInfo information, uint bufferSize);

    [StructLayout(LayoutKind.Explicit, Size = 256)]
    private struct StatxInfo
    {
        [FieldOffset(0)] public uint Mask;
        [FieldOffset(32)] public ulong Inode;
        [FieldOffset(80)] public long BirthSeconds;
        [FieldOffset(88)] public uint BirthNanoseconds;
        [FieldOffset(136)] public uint DeviceMajor;
        [FieldOffset(140)] public uint DeviceMinor;
    }

    [DllImport("libc", EntryPoint = "statx", SetLastError = true)]
    private static extern int Statx(int directoryFd, string path, int flags, uint mask, out StatxInfo info);

    public static string Get(string path)
    {
        using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (OperatingSystem.IsWindows())
        {
            if (!GetFileInformationByHandleEx(handle, 18 /* FileIdInfo */, out var info, (uint)Marshal.SizeOf<FileIdInfo>()))
                throw new IOException("Не удалось подтвердить принадлежность файла на этом диске. Выберите другую папку загрузки.", new Win32Exception(Marshal.GetLastWin32Error()));
            return $"windows:{info.VolumeSerialNumber:X16}:{info.FileId:N}";
        }
        if (OperatingSystem.IsLinux())
        {
            // .NET's CreationTime fallback may change on a normal write. Ask the kernel
            // for the opened inode's actual birth time; an inode alone may be recycled.
            if (Statx(handle.DangerousGetHandle().ToInt32(), "", 0x1000 /* AT_EMPTY_PATH */, 0x1fff, out var stat) == 0 &&
                (stat.Mask & 0x900) == 0x900 /* STATX_INO | STATX_BTIME */)
                return $"linux:{stat.DeviceMajor}:{stat.DeviceMinor}:{stat.Inode}:{stat.BirthSeconds}:{stat.BirthNanoseconds}";
        }
        throw new IOException("Файловая система не предоставляет идентификатор для безопасного продолжения загрузки.");
    }
}
