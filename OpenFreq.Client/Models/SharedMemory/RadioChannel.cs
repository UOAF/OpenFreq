using FalconBmsDataService.Models;

namespace FalconRadioService.Models;

public class RadioChannel
{
    public RadioType RadioType { get; set; }
    public int Frequency { get; set; }        // 6-digit MHz * 1000
    public int RxVolume { get; set; }         // 0-10000
    public bool PttDepressed { get; set; }
    public bool IsOn { get; set; }

    public RadioChannel(RadioType radioType)
    {
        RadioType = radioType;
    }

    public RadioChannel Clone()
    {
        return new RadioChannel(RadioType)
        {
            Frequency = Frequency,
            RxVolume = RxVolume,
            PttDepressed = PttDepressed,
            IsOn = IsOn
        };
    }

    public override string ToString()
    {
        return $"{RadioType}: {Frequency / 1000.0:F3} MHz, Vol:{RxVolume}, PTT:{PttDepressed}, On:{IsOn}";
    }
}
