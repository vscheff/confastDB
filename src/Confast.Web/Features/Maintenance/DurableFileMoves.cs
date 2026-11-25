using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Confast.Web.Features.Maintenance;

internal static class DurableFileMoves
{
    internal static void Move(string source, string destination, bool directory = false)
    {
        if (OperatingSystem.IsWindows())
        {
            const int writeThrough = 8;
            if (!MoveFileEx(source, destination, writeThrough))
                throw new IOException("The storage rename failed.", new Win32Exception(Marshal.GetLastPInvokeError()));
        }
        else
        {
            if (directory) Directory.Move(source, destination);
            else File.Move(source, destination);
            FlushDirectory(Path.GetDirectoryName(destination)!);
        }
    }

    internal static void FlushDirectory(string path)
    {
        if (OperatingSystem.IsWindows()) return;
        var descriptor = Open(path, 0);
        if (descriptor < 0) throw new IOException("The storage directory could not be opened for a durable update.");
        try
        {
            if (Sync(descriptor) != 0) throw new IOException("The storage directory could not be flushed.");
        }
        finally { Close(descriptor); }
    }

    [DllImport("kernel32.dll", EntryPoint = "MoveFileExW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool MoveFileEx(string source, string destination, int flags);

    [DllImport("libc", EntryPoint = "open", SetLastError = true)]
    private static extern int Open([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags);

    [DllImport("libc", EntryPoint = "fsync", SetLastError = true)]
    private static extern int Sync(int descriptor);

    [DllImport("libc", EntryPoint = "close", SetLastError = true)]
    private static extern int Close(int descriptor);
}
