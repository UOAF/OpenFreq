using FalconBmsDataService.Models;

namespace FalconRadioService.Models;

public class RadioDevice
{
    public RadioDeviceType DeviceType { get; set; }
    public int IntercomVolume { get; set; }   // 0-10000

    public RadioDevice(RadioDeviceType deviceType)
    {
        DeviceType = deviceType;
    }

    public RadioDevice Clone()
    {
        return new RadioDevice(DeviceType)
        {
            IntercomVolume = IntercomVolume
        };
    }
}
