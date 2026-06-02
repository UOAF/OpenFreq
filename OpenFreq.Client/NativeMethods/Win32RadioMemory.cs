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
    public const int RCS_SIZE = 4; // RadioClientStatus is just an int
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

    [StructLayout(LayoutKind.Sequential)]
    public struct SECURITY_ATTRIBUTES
    {
        public int nLength;
        public IntPtr lpSecurityDescriptor;
        public int bInheritHandle;
    }

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool ConvertStringSecurityDescriptorToSecurityDescriptorW(
        string stringSecurityDescriptor,
        uint stringSdRevision,
        out IntPtr securityDescriptor,
        out int securityDescriptorSize);

    [DllImport("kernel32.dll")]
    public static extern IntPtr LocalFree(IntPtr hMem);

    public const uint SDDL_REVISION_1 = 1;

    // Security descriptor applied to the kernel objects which OpenFreq creates (the RCS
    // shared-memory mapping and the radio-client mutex):
    //   D:(A;;GA;;;WD)        DACL: grant GENERIC_ALL to Everyone
    //   S:(ML;;NW;;;LW)       SACL: mandatory label = Low integrity, no-write-up policy
    public const string SHARED_MEMORY_SDDL = "D:(A;;GA;;;WD)S:(ML;;NW;;;LW)";

    /// <summary>
    /// Builds an unmanaged SECURITY_ATTRIBUTES* carrying SHARED_MEMORY_SDDL.
    /// Returns IntPtr.Zero if the descriptor could not be built (caller should fall back to passing IntPtr.Zero = default security).
    /// </summary>
    public static IntPtr CreateSharedMemorySecurityAttributes()
    {
        if (!ConvertStringSecurityDescriptorToSecurityDescriptorW(
                SHARED_MEMORY_SDDL, SDDL_REVISION_1, out var pSd, out _))
        {
            return IntPtr.Zero;
        }

        var sa = new SECURITY_ATTRIBUTES
        {
            nLength = Marshal.SizeOf<SECURITY_ATTRIBUTES>(),
            lpSecurityDescriptor = pSd,
            bInheritHandle = 0
        };

        var pSa = Marshal.AllocHGlobal(sa.nLength);
        Marshal.StructureToPtr(sa, pSa, false);
        return pSa;
    }

    public static void FreeSharedMemorySecurityAttributes(IntPtr pSa)
    {
        if (pSa == IntPtr.Zero)
            return;

        var sa = Marshal.PtrToStructure<SECURITY_ATTRIBUTES>(pSa);
        if (sa.lpSecurityDescriptor != IntPtr.Zero)
            LocalFree(sa.lpSecurityDescriptor);

        Marshal.FreeHGlobal(pSa);
    }
}
