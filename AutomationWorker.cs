using Microsoft.Extensions.Options;
using ShutterAutomation.Models;
using ShutterAutomation.Services;

namespace ShutterAutomation;

public class AutomationWorker : BackgroundService
{
    private readonly ILogger<AutomationWorker> _logger;
    private readonly IOptionsMonitor<AutomationConfig> _config;
    private readonly SunPositionService _sunPosition;
    private readonly WeatherService _weather;
    private readonly ShutterService _shutters;

    private DateTime? _lastAutoClose;

    public AutomationWorker(
        ILogger<AutomationWorker> logger,
        IOptionsMonitor<AutomationConfig> config,
        SunPositionService sunPosition,
        WeatherService weather,
        ShutterService shutters)
    {
        _logger = logger;
        _config = config;
        _sunPosition = sunPosition;
        _weather = weather;
        _shutters = shutters;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Shutter automation started. Check interval: {Interval} minutes", _config.CurrentValue.CheckIntervalMinutes);
        _logger.LogInformation("Monitoring {Count} shutters", _config.CurrentValue.Shutters.Count);

        while (!stoppingToken.IsCancellationRequested)
        {
            await RunCycleAsync(stoppingToken);
            await Task.Delay(TimeSpan.FromMinutes(_config.CurrentValue.CheckIntervalMinutes), stoppingToken);
        }
    }

    private async Task RunCycleAsync(CancellationToken ct)
    {
        var tz = TimeZoneInfo.FindSystemTimeZoneById(_config.CurrentValue.TimeZoneId);
        var localNow = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, tz);

        _logger.LogInformation("--- Cycle at {Time} in {Timezone} ---", localNow.ToString("HH:mm:ss"), _config.CurrentValue.TimeZoneId);

        // 1. Sun position (pure math, no API)
        var sun = _sunPosition.Calculate(_config.CurrentValue.Latitude, _config.CurrentValue.Longitude, DateTime.UtcNow);
        _logger.LogInformation("Sun: azimuth={Azimuth:F1}°, elevation={Elevation:F1}°",
            sun.AzimuthDegrees, sun.ElevationDegrees);



        // 2. Weather (Open-Meteo)
        var weatherReading = await _weather.GetCurrentAsync(_config.CurrentValue.Latitude, _config.CurrentValue.Longitude, _config.CurrentValue.TimeZoneId, ct);
        if (weatherReading is null)
        {
            _logger.LogWarning("Skipping cycle: weather data unavailable");
            return;
        }
        _logger.LogInformation("Weather: {Temp:F1}°C, direct radiation={Rad:F0} W/m²",
            weatherReading.TemperatureCelsius, weatherReading.DirectRadiationWm2);

        // 3. Evaluate conditions
        bool shouldClose = EvaluateConditions(sun, weatherReading);
        _logger.LogInformation("Decision: shutters should be {State}", shouldClose ? "CLOSED (20%)" : "OPEN (100%)");

        if (shouldClose && _lastAutoClose.HasValue)
        {
            var cooldownRemaining = _lastAutoClose.Value.AddMinutes(_config.CurrentValue.CooldownMinutes) - DateTime.UtcNow;
            if (cooldownRemaining > TimeSpan.Zero)
            {
                _logger.LogInformation("In cooldown, {Min:F0} minutes remaining before state change allowed",
                    cooldownRemaining.TotalMinutes);
                return;
            }
        }

        // 4. Act on each shutter
        foreach (var shutter in _config.CurrentValue.Shutters)
        {
            await ProcessShutterAsync(shutter, shouldClose, ct);
        }

        if (shouldClose)
            _lastAutoClose = DateTime.UtcNow;
    }

    private bool EvaluateConditions(SunPositionService.SunPosition sun, WeatherReading weather)
    {
        var reasons = new List<string>();
        var blockers = new List<string>();

        // Temperature check (with hysteresis handled via appsettings thresholds)
        if (weather.TemperatureCelsius >= _config.CurrentValue.Temperature.CloseThresholdCelsius)
            reasons.Add($"temperature {weather.TemperatureCelsius:F1}°C >= {_config.CurrentValue.Temperature.CloseThresholdCelsius}°C");
        else
            blockers.Add($"temperature {weather.TemperatureCelsius:F1}°C < {_config.CurrentValue.Temperature.CloseThresholdCelsius}°C");

        // Sun elevation check
        if (sun.ElevationDegrees >= _config.CurrentValue.Sun.MinElevationDegrees)
            reasons.Add($"elevation {sun.ElevationDegrees:F1}° >= {_config.CurrentValue.Sun.MinElevationDegrees}°");
        else
            blockers.Add($"elevation {sun.ElevationDegrees:F1}° < {_config.CurrentValue.Sun.MinElevationDegrees}° (sun too low)");

        // Sun azimuth check (is sun facing the west wall?)
        if (sun.AzimuthDegrees >= _config.CurrentValue.Sun.AzimuthMinDegrees && sun.AzimuthDegrees <= _config.CurrentValue.Sun.AzimuthMaxDegrees)
            reasons.Add($"azimuth {sun.AzimuthDegrees:F1}° in [{_config.CurrentValue.Sun.AzimuthMinDegrees}°–{_config.CurrentValue.Sun.AzimuthMaxDegrees}°]");
        else
            blockers.Add($"azimuth {sun.AzimuthDegrees:F1}° outside window [{_config.CurrentValue.Sun.AzimuthMinDegrees}°–{_config.CurrentValue.Sun.AzimuthMaxDegrees}°] (sun not facing windows)");

        // Direct radiation check (filters overcast days)
        if (weather.DirectRadiationWm2 >= _config.CurrentValue.Weather.DirectRadiationThresholdWm2)
            reasons.Add($"radiation {weather.DirectRadiationWm2:F0} W/m² >= {_config.CurrentValue.Weather.DirectRadiationThresholdWm2} W/m²");
        else
            blockers.Add($"radiation {weather.DirectRadiationWm2:F0} W/m² < {_config.CurrentValue.Weather.DirectRadiationThresholdWm2} W/m² (overcast or nighttime)");

        bool shouldClose = blockers.Count == 0;

        if (shouldClose)
            _logger.LogInformation("All conditions met: [{Reasons}]", string.Join(", ", reasons));
        else
            _logger.LogInformation("Conditions not met — blockers: [{Blockers}]", string.Join(", ", blockers));

        return shouldClose;
    }

    private async Task ProcessShutterAsync(ShutterConfig shutter, bool shouldClose, CancellationToken ct)
    {
        var currentPosition = await _shutters.GetPositionAsync(shutter, ct);
        if (currentPosition is null)
        {
            _logger.LogWarning("[{Name}] Skipping: could not read current position", shutter.Name);
            return;
        }

        int tolerance = _config.CurrentValue.Shutter.PositionTolerancePercent;

        if (shouldClose)
        {
            bool alreadyAtTarget = currentPosition.Value <= _config.CurrentValue.Shutter.ClosedPositionPercent + tolerance;
            if (alreadyAtTarget)
            {
                _logger.LogInformation("[{Name}] Already at or past closed position {Pos}%, no action needed",
                    shutter.Name, currentPosition.Value);
                return;
            }

            _logger.LogInformation("[{Name}] Closing from {Current}% to {Target}%",
                shutter.Name, currentPosition.Value, _config.CurrentValue.Shutter.ClosedPositionPercent);
            await _shutters.SetPositionAsync(shutter, _config.CurrentValue.Shutter.ClosedPositionPercent, ct);
        }
        else
        {
            bool isAtAutoClosed = Math.Abs(currentPosition.Value - _config.CurrentValue.Shutter.ClosedPositionPercent) <= tolerance;
            if (!isAtAutoClosed)
            {
                _logger.LogInformation("[{Name}] At manual position {Pos}%, not overriding on open",
                    shutter.Name, currentPosition.Value);
                return;
            }

            _logger.LogInformation("[{Name}] Opening from {Current}% to {Target}%",
                shutter.Name, currentPosition.Value, _config.CurrentValue.Shutter.OpenPositionPercent);
            await _shutters.SetPositionAsync(shutter, _config.CurrentValue.Shutter.OpenPositionPercent, ct);
        }
    }
}
