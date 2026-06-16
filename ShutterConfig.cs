namespace ShutterAutomation.Models;

public class ShutterConfig
{
    public string Name { get; set; } = string.Empty;
    public string Host { get; set; } = string.Empty;
    public int Channel { get; set; }
    public int CooldownMinutes { get; set; } = 30;
    public AzimuthConfig Azimuth { get; set; } = new();
    public ShutterWeights Weights { get; set; } = new();
    public HeatModelConfig HeatModel { get; set; } = new();
}

public class AzimuthConfig
{
    public double Center { get; set; } = 270.0;
    public double Width { get; set; } = 85.0;
}

public class ShutterWeights
{
    public double Temperature { get; set; } = 0.3;
    public double InstantRadiation { get; set; } = 0.2;
    public double AccumulatedRadiation { get; set; } = 0.3;
    public double Azimuth { get; set; } = 0.2;
}

public class HeatModelConfig
{
    public double TempMin { get; set; } = 19.0;
    public double TempMax { get; set; } = 30.0;
    public double RadiationMax { get; set; } = 400.0;
    public double AccumulationDecay { get; set; } = 0.93;
    public double AccumulationMax { get; set; } = 800.0;
    public double AccumulationAzimuthMin { get; set; } = 230.0;
    public double AccumulationAzimuthMax { get; set; } = 330.0;
    public double MinScoreToAct { get; set; } = 0.25;
    public int PositionFloorPercent { get; set; } = 40;
    public int PositionCeilingPercent { get; set; } = 100;
}

public class AutomationConfig
{
    public double Latitude { get; set; }
    public double Longitude { get; set; }
    public string TimeZoneId { get; set; } = "Europe/Brussels";
    public int CheckIntervalMinutes { get; set; } = 5;
    public double MinElevationDegrees { get; set; } = 10.0;
    public List<ShutterConfig> Shutters { get; set; } = new();
}