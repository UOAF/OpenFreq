namespace OpenFreqClient.Models;

public static class Channel
{
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
