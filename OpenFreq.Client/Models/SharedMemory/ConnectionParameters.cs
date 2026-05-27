namespace FalconRadioService.Models;

public class ConnectionParameters
{
    public string Address { get; set; } = string.Empty;
    public int Port { get; set; }
    public string Password { get; set; } = string.Empty;
    public string Nickname { get; set; } = string.Empty;
    public bool UseAGC { get; set; }
    public bool ReadyToTransmit { get; set; }
    public bool AttemptingToConnect { get; set; }
    public bool TerminateClient { get; set; }
    public bool FlightMode { get; set; }

    public ConnectionParameters Clone()
    {
        return new ConnectionParameters
        {
            Address = Address,
            Port = Port,
            Password = Password,
            Nickname = Nickname,
            UseAGC = UseAGC,
            ReadyToTransmit = ReadyToTransmit,
            AttemptingToConnect = AttemptingToConnect,
            TerminateClient = TerminateClient,
            FlightMode = FlightMode
        };
    }

    public override string ToString()
    {
        return
            $"{Address}:{Port} (Nick:{Nickname}) (UseAGC:{UseAGC}) (ReadyToTransmit:{ReadyToTransmit}) (AttemptingToConnect:{AttemptingToConnect})  (TerminateClient:{TerminateClient}) (FlightMode:{FlightMode})";
    }
}
