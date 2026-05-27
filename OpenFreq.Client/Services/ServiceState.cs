using System;

namespace FalconBmsDataService.Models;

public enum ServiceState
{
    Stopped,
    Disconnected,
    Connected,
    RcsCreated,
    WaitingForBms
}
