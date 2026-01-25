using OpenFreqClient.Services.Interfaces;

namespace OpenFreqClient.Models;

public class OpenFreqSettings
{
    public string OpenFreqServerAddress { get; set; } = "";
    public string OpenFreqPassword { get; set; } = "";

    public IOpenFreqService.Mode OwnPositionMode { get; set; } = IOpenFreqService.Mode.BMS;
    public string TacviewServerAddress { get; set; } = "";
    public string TacviewServerPassword { get; set; } = "";
    
    public string HeightmapPath { get; set; } = "";

    public string InputDeviceName { get; set; } = "";
    public string OutputDeviceName { get; set; } = "";
    public string SelectedTheater { get; set; } = "Korea KTO";
}