using System.Runtime.InteropServices;
using FalconBmsDataService.Models;
using FalconRadioService.Models;
using FalconRadioService.Parsers;

namespace OpenFreq.Client.Tests;

/// <summary>
/// Tests for reading PTT from RCC shared memory, against a zeroed buffer laid out like BMS's RadioClientControl.
/// </summary>
public sealed class RadioControlParserTests : IDisposable
{
    // Up to the end of mRadios: port (4) + address, password, nickname (3 x 64) + RadioChannel (3 x 12).
    private const int RccPrefixSize = 232;

    // mRadios[1].mPttDepressed: mRadios starts at 196, and mPttDepressed is 8 bytes into a 12-byte RadioChannel.
    private const int Radio2PttOffset = 196 + 12 + 8;

    private readonly IntPtr _rcc = Marshal.AllocHGlobal(RccPrefixSize);

    public RadioControlParserTests()
    {
        Marshal.Copy(new byte[RccPrefixSize], 0, _rcc, RccPrefixSize);
    }

    public void Dispose() => Marshal.FreeHGlobal(_rcc);

    [Fact]
    public void PttFlag_MatchesTheFullChannelRead()
    {
        Marshal.WriteByte(_rcc, Radio2PttOffset, 1);

        Assert.True(RadioControlParser.ParsePttDepressed(_rcc, RadioType.Radio2));
        Assert.True(RadioControlParser.ParseRadioChannel(_rcc, RadioType.Radio2).PttDepressed);
        Assert.False(RadioControlParser.ParsePttDepressed(_rcc, RadioType.Radio1));
        Assert.False(RadioControlParser.ParsePttDepressed(_rcc, RadioType.Guard));
    }
}
