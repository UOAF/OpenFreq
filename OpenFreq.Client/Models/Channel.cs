namespace OpenFreqClient.Models;

public class Channel
{
    /// <summary>
    /// Frequency in KHz
    /// </summary>
    public int FrequencyKhz { get; set; }
    
    public string? Name  { get; set; }
    public float RxDb {get; set;}
    
    public enum ChannelType
    {
        UHF, VHF, Custom
    }

    public enum ChannelConnectionStatus
    {
        Connected,
        Disconnected,
    }

    public enum ChannelTransmissionStatus
    {
        Idle,
        Receiving,
        Transmitting
    }
}