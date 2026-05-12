using OpenFreq.Client.Models;
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

    public int BmsUhfPan { get; set; } // 0 == center
    public int BmsVhfPan { get; set; }
    
    public HotkeyBinding? BmsSquelchUhfHotkey { get; set; }
    public HotkeyBinding? BmsSquelchVhfHotkey { get; set; }
    
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
    public string DisplayName { get; set; } = "Unnamed";
    public bool SidetoneEnabled { get; set; } = false;
    public double SidetoneVolume { get; set; } = 0.4;
    public bool? DarkMode { get; set; }
}