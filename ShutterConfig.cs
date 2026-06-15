namespace ShutterAutomation.Models;

public class ShutterConfig
{
    public string Name { get; set; } = string.Empty;
    public string Host { get; set; } = string.Empty;
    public int Channel { get; set; }
}

public class AutomationConfig
{
    public double Latitude { get; set; }
    public double Longitude { get; set; }
    public string TimeZoneId { get; set; } = "Europe/Brussels";
    public int CheckIntervalMinutes { get; set; } = 5;
    public int CooldownMinutes { get; set; } = 60;
    public TemperatureConfig Temperature { get; set; } = new();
    public SunConfig Sun { get; set; } = new();
    public WeatherConfig Weather { get; set; } = new();
    public ShutterPositionConfig Shutter { get; set; } = new();
    public List<ShutterConfig> Shutters { get; set; } = new();
}

public class TemperatureConfig
{
    public double CloseThresholdCelsius { get; set; } = 22.0;
    public double OpenThresholdCelsius { get; set; } = 20.0;
}

public class SunConfig
{
    public double AzimuthMinDegrees { get; set; } = 210.0;
    public double AzimuthMaxDegrees { get; set; } = 300.0;
    public double MinElevationDegrees { get; set; } = 10.0;
}

public class WeatherConfig
{
    public double DirectRadiationThresholdWm2 { get; set; } = 150.0;
}

public class ShutterPositionConfig
{
    public int ClosedPositionPercent { get; set; } = 20;
    public int OpenPositionPercent { get; set; } = 100;
    public int PositionTolerancePercent { get; set; } = 5;
}
