using System;
using System.Runtime.InteropServices;

namespace OpenFreq.Client.NativeMethods;

internal static class Win32RadioMemory
{
    // Constants
    public const uint PAGE_READWRITE = 0x04;
    public const uint FILE_MAP_ALL_ACCESS = 0xF001F;
    public const uint FILE_MAP_READ = 0x0004;
    public const uint SECTION_MAP_READ = 0x0004;
    public static readonly IntPtr INVALID_HANDLE_VALUE = new IntPtr(-1);

    // Mutex constants
    public const uint SYNCHRONIZE = 0x00100000;

    // Shared memory names
    public const string FALCON_RCC_SHARED_MEMORY = "FalconRccSharedMemoryArea";
    public const string FALCON_RCS_SHARED_MEMORY = "FalconRcsSharedMemoryArea";

    // Semaphore/Mutex names
    public const string FALCON_SEMAPHORE = "FALCONBMS-9B416580-DE23-11B2-A386-000C6E135DDE";
    public const string RADIO_CLIENT_SEMAPHORE = "RADIOCLNT-D9F30F52-5AF2-495B-B187-C8F93625D6ED";

    // Structure sizes
    public const int RCS_SIZE = 4;  // RadioClientStatus is just an int
    public const int RCC_SIZE = 4096; // Approximate RadioClientControl size

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Ansi)]
    public static extern IntPtr CreateFileMapping(
        IntPtr hFile,
        IntPtr lpFileMappingAttributes,
        uint flProtect,
        uint dwMaximumSizeHigh,
        uint dwMaximumSizeLow,
        string lpName);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Ansi)]
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

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Ansi)]
    public static extern IntPtr CreateMutex(
        IntPtr lpMutexAttributes,
        bool bInitialOwner,
        string lpName);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Ansi)]
    public static extern IntPtr OpenMutex(
        uint dwDesiredAccess,
        bool bInheritHandle,
        string lpName);
}
