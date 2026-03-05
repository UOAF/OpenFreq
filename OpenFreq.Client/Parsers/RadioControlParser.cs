using System;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using System.Text;
using FalconBmsDataService.Models;
using FalconRadioService.Models;

namespace FalconRadioService.Parsers;

[SuppressMessage("ReSharper", "InconsistentNaming")]
public static class RadioControlParser
{
    private const int OFFSET_PORT = 0;              // int (4 bytes)
    private const int OFFSET_ADDRESS = 4;           // char[64]
    private const int OFFSET_PASSWORD = 68;         // char[64]
    private const int OFFSET_NICKNAME = 132;        // char[64]
    private const int OFFSET_RADIOS = 196;          // RadioChannel[3] (36 bytes)
    private const int OFFSET_SIGNAL_CONNECT = 232;  // bool
    private const int OFFSET_ATTEMPT_TO_CONNECT = 233; // bool
    private const int OFFSET_TERMINATE_CLIENT = 234;   // bool
    private const int OFFSET_FLIGHT_MODE = 235;        // bool
    private const int OFFSET_USE_AGC = 236;            // bool
    // 3 bytes padding here for alignment
    private const int OFFSET_DEVICES = 240;            // RadioDevice[1] (4 bytes)
    private const int OFFSET_PLAYER_COUNT = 244;       // int (4 bytes)
    private const int OFFSET_PLAYER_MAP = 248;         // Telemetry[96]

    // RadioChannel structure size (12 bytes with padding)
    private const int RADIOCHANNEL_SIZE = 12;

    public static ConnectionParameters ParseConnectionParameters(IntPtr baseAddress)
    {
        if (baseAddress == IntPtr.Zero)
            return new ConnectionParameters();

        try
        {
            var parameters = new ConnectionParameters
            {
                Port = Marshal.ReadInt32(baseAddress, OFFSET_PORT),
                Address = ReadString(baseAddress, OFFSET_ADDRESS, 64),
                Password = ReadString(baseAddress, OFFSET_PASSWORD, 64),
                Nickname = ReadString(baseAddress, OFFSET_NICKNAME, 64),
                ReadyToTransmit = Marshal.ReadByte(baseAddress, OFFSET_SIGNAL_CONNECT) != 0,
                AttemptingToConnect = Marshal.ReadByte(baseAddress, OFFSET_ATTEMPT_TO_CONNECT) != 0,
                TerminateClient = Marshal.ReadByte(baseAddress, OFFSET_TERMINATE_CLIENT) != 0,
                FlightMode = Marshal.ReadByte(baseAddress, OFFSET_FLIGHT_MODE) != 0,
                UseAGC = Marshal.ReadByte(baseAddress, OFFSET_USE_AGC) != 0
            };

            return parameters;
        }
        catch
        {
            return new ConnectionParameters();
        }
    }

    public static RadioChannel ParseRadioChannel(IntPtr baseAddress, RadioType radioType)
    {
        if (baseAddress == IntPtr.Zero)
            return new RadioChannel(radioType);

        try
        {
            int offset = OFFSET_RADIOS + ((int)radioType * RADIOCHANNEL_SIZE);

            var channel = new RadioChannel(radioType)
            {
                Frequency = Marshal.ReadInt32(baseAddress, offset),
                RxVolume = Marshal.ReadInt32(baseAddress, offset + 4),
                PttDepressed = Marshal.ReadByte(baseAddress, offset + 8) != 0,
                IsOn = Marshal.ReadByte(baseAddress, offset + 9) != 0
            };

            return channel;
        }
        catch
        {
            return new RadioChannel(radioType);
        }
    }

    public static RadioDevice ParseRadioDevice(IntPtr baseAddress, RadioDeviceType deviceType)
    {
        if (baseAddress == IntPtr.Zero)
            return new RadioDevice(deviceType);

        try
        {
            int offset = OFFSET_DEVICES + ((int)deviceType * 4);

            var device = new RadioDevice(deviceType)
            {
                IntercomVolume = Marshal.ReadInt32(baseAddress, offset)
            };

            return device;
        }
        catch
        {
            return new RadioDevice(deviceType);
        }
    }

    public static string ParseLogbookName(IntPtr baseAddress)
    {
        if (baseAddress == IntPtr.Zero)
            return string.Empty;

        try
        {
            // Ownship is at mPlayerMap[0]
            // Telemetry structure: float agl (4), float range (4), uint flags (4), char name[21]
            int nameOffset = OFFSET_PLAYER_MAP + 12; // Skip agl, range, flags
            return ReadString(baseAddress, nameOffset, 21);
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string ReadString(IntPtr baseAddress, int offset, int maxLength)
    {
        byte[] buffer = new byte[maxLength];
        Marshal.Copy(baseAddress + offset, buffer, 0, maxLength);

        // Find null terminator
        int nullIndex = Array.IndexOf(buffer, (byte)0);
        if (nullIndex >= 0)
            maxLength = nullIndex;

        return Encoding.ASCII.GetString(buffer, 0, maxLength);
    }
}