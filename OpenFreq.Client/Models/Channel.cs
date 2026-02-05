using FalconBmsDataService.Models;

namespace OpenFreqClient.Models;

public class Channel
{
    /// <summary>
    /// Frequency in KHz
    /// </summary>
    public int FrequencyKhz { get; set; }
    
    public string? Name  { get; set; }
    public float RxDb {get; set;}
    
    public ChannelStatus Status { get; set; } = ChannelStatus.Disconnected;

    public bool Enabled { get; set; } = true;

    public enum ChannelType
    {
        UHF, VHF, Custom
    }

    public enum ChannelStatus
    {
        Connected,
        Disconnected,
        Receiving,
        Transmitting
    }
}