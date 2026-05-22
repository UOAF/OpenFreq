using System;

namespace FalconBmsDataService.Models;

public enum RadioType
{
    Radio1 = 0,  // BMS UHF
    Radio2 = 1,  // BMS VHF
    Guard = 2    // GUARD
}

public enum RadioDeviceType
{
    MAIN = 0
}

[Flags]
public enum ClientStatusFlags
{
    AllClear        = 0x00,
    ClientActive    = 0x01,
    Connected       = 0x02,
    TryingToConnect = 0x04,
    ExitReceived    = 0x08,
    ConnectionFail  = 0x08000000,
    HostUnknown     = 0x10000000,
    BadPassword     = 0x20000000,
    NoMicrophone    = 0x40000000,
    NoSpeakers      = unchecked((int)0x80000000),
    ErrorMask       = unchecked((int)0xF8000000)
}
