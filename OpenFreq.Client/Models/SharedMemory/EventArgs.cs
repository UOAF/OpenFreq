using System;
using FalconBmsDataService.Models;

namespace FalconRadioService.Models;

public class ServiceStateChangedEventArgs : EventArgs
{
    public ServiceState OldState { get; }
    public ServiceState NewState { get; }
    public DateTime Timestamp { get; }

    public ServiceStateChangedEventArgs(ServiceState oldState, ServiceState newState)
    {
        OldState = oldState;
        NewState = newState;
        Timestamp = DateTime.UtcNow;
    }
}

public class RadioFrequencyChangedEventArgs : EventArgs
{
    public RadioType RadioType { get; }
    public int OldFrequencyKhz { get; }
    public int NewFrequencyKhz { get; }

    public RadioFrequencyChangedEventArgs(RadioType radioType, int oldFreqKhz, int newFreqKhz)
    {
        RadioType = radioType;
        OldFrequencyKhz = oldFreqKhz;
        NewFrequencyKhz = newFreqKhz;
    }
}

public class RadioVolumeChangedEventArgs : EventArgs
{
    public RadioType RadioType { get; }
    public int OldVolume { get; }
    public int NewVolume { get; }

    public RadioVolumeChangedEventArgs(RadioType radioType, int oldVol, int newVol)
    {
        RadioType = radioType;
        OldVolume = oldVol;
        NewVolume = newVol;
    }
}

public class RadioPttChangedEventArgs : EventArgs
{
    public RadioType RadioType { get; }
    public bool OldPtt { get; }
    public bool NewPtt { get; }

    public RadioPttChangedEventArgs(RadioType radioType, bool oldPtt, bool newPtt)
    {
        RadioType = radioType;
        OldPtt = oldPtt;
        NewPtt = newPtt;
    }
}

public class RadioPowerChangedEventArgs : EventArgs
{
    public RadioType RadioType { get; }
    public bool OldPower { get; }
    public bool NewPower { get; }

    public RadioPowerChangedEventArgs(RadioType radioType, bool oldPower, bool newPower)
    {
        RadioType = radioType;
        OldPower = oldPower;
        NewPower = newPower;
    }
}

public class ConnectionParametersChangedEventArgs : EventArgs
{
    public ConnectionParameters OldParameters { get; }
    public ConnectionParameters NewParameters { get; }

    public ConnectionParametersChangedEventArgs(ConnectionParameters oldParams, ConnectionParameters newParams)
    {
        OldParameters = oldParams;
        NewParameters = newParams;
    }
}

public class FlyingStateChangedEventArgs : EventArgs
{
    public bool OldFlyingState { get; }
    public bool NewFlyingState { get; }

    public FlyingStateChangedEventArgs(bool oldFlyingState, bool newFlyingState)
    {
        OldFlyingState = oldFlyingState;
        NewFlyingState = newFlyingState;
    }
}
