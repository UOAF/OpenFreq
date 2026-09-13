using System;
using System.Runtime.InteropServices;

namespace OpenFreq.Client.NativeMethods;

/// <summary>
/// Win32 API declarations for shared memory access
/// </summary>
internal static class Win32SharedMemory
{
    public const uint SECTION_MAP_READ = 0x0004;

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern IntPtr OpenFileMapping(
        uint dwDesiredAccess,
        bool bInheritHandle,
        string lpName);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr MapViewOfFile(
        IntPtr hFileMappingObject,
        uint dwDesiredAccess,
        uint dwFileOffsetHigh,
        uint dwFileOffsetLow,
        IntPtr dwNumberOfBytesToMap);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool UnmapViewOfFile(IntPtr lpBaseAddress);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool CloseHandle(IntPtr hObject);
}
