using OpenFreqAudio;
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

    public RadioPlayback.AudioChannel BmsUhfChannel { get; set; } = RadioPlayback.AudioChannel.Both;
    public RadioPlayback.AudioChannel BmsVhfChannel { get; set; } = RadioPlayback.AudioChannel.Both;
    
    public string BmsSquelchUhfHotkeyCode { get; set; } = "VcUndefined";
    public string BmsSquelchVhfHotkeyCode { get; set; } = "VcUndefined";
    
    // Window Position & Size
    public int? Left { get; set; }
    public int? Top { get; set; }
    public int? Width { get; set; }
    public int? Height { get; set; }
    public int? WindowState { get; set; }
    public int? MaximizedScreenX { get; set; }
    public int? MaximizedScreenY { get; set; }
    public int? MaximizedScreenWidth { get; set; }
    public int? MaximizedScreenHeight { get; set; }
    public string DisplayName { get; set; }
}