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

    private double _accumulatedRadiation = 0;
    private readonly Dictionary<string, DateTime> _lastAutoClose = new();

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
        var config = _config.CurrentValue;
        var tz = TimeZoneInfo.FindSystemTimeZoneById(config.TimeZoneId);
        var utcNow = DateTime.UtcNow;
        var localNow = TimeZoneInfo.ConvertTimeFromUtc(utcNow, tz);

        _logger.LogInformation("--- Cycle at {Time} ({Timezone}) ---", localNow.ToString("HH:mm:ss"), config.TimeZoneId);

        // 1. Sun position
        var sun = _sunPosition.Calculate(config.Latitude, config.Longitude, utcNow);
        _logger.LogInformation("Sun: azimuth={Azimuth:F1}°, elevation={Elevation:F1}°",
            sun.AzimuthDegrees, sun.ElevationDegrees);

        // 2. Weather
        var weather = await _weather.GetCurrentAsync(config.Latitude, config.Longitude, config.TimeZoneId, ct);
        if (weather is null)
        {
            _logger.LogWarning("Skipping cycle: weather data unavailable");
            return;
        }

        // 3. Update radiation accumulator
        _accumulatedRadiation = _accumulatedRadiation * config.HeatModel.AccumulationDecay + weather.DirectRadiationWm2;
        _accumulatedRadiation = Math.Min(_accumulatedRadiation, config.HeatModel.AccumulationMax);

        _logger.LogInformation("Weather: {Temp:F1}°C, radiation={Rad:F0} W/m², accumulated={Acc:F0}",
            weather.TemperatureCelsius, weather.DirectRadiationWm2, _accumulatedRadiation);

        // 4. Sun elevation gate
        if (sun.ElevationDegrees < config.MinElevationDegrees)
        {
            _logger.LogInformation("Sun elevation {Elev:F1}° below minimum {Min}°, skipping",
                sun.ElevationDegrees, config.MinElevationDegrees);
            return;
        }

        // 5. Act on each shutter independently
        foreach (var shutter in config.Shutters)
        {
            await ProcessShutterAsync(shutter, sun, weather, config, ct);
        }
    }

    private async Task<bool> ProcessShutterAsync(
        ShutterConfig shutter,
        SunPositionService.SunPosition sun,
        WeatherReading weather,
        AutomationConfig config,
        CancellationToken ct)
    {
        var model = config.HeatModel;

        // Compute individual scores (0–1)
        double tempScore = Math.Clamp(
            (weather.TemperatureCelsius - model.TempMin) / (model.TempMax - model.TempMin), 0, 1);

        double instantRadScore = Math.Clamp(
            weather.DirectRadiationWm2 / model.RadiationMax, 0, 1);

        double accRadScore = Math.Clamp(
            _accumulatedRadiation / model.AccumulationMax, 0, 1);

        // Soft azimuth score — cosine falloff from center, 0 outside window
        double azimuthDelta = Math.Abs(sun.AzimuthDegrees - shutter.AzimuthCenter);
        double halfWidth = shutter.AzimuthWidth / 2.0;
        double azimuthScore = azimuthDelta <= halfWidth
            ? Math.Max(0, Math.Cos(azimuthDelta / halfWidth * Math.PI / 2.0))
            : 0.0;

        // Weighted heat score
        var w = shutter.Weights;
        double heatScore = w.Temperature * tempScore
                         + w.InstantRadiation * instantRadScore
                         + w.AccumulatedRadiation * accRadScore
                         + w.Azimuth * azimuthScore;

        _logger.LogInformation(
            "[{Name}] scores: temp={T:F2} rad={R:F2} acc={A:F2} azimuth={Az:F2} → heat={H:F2}",
            shutter.Name, tempScore, instantRadScore, accRadScore, azimuthScore, heatScore);

        // Below minimum score — do nothing
        if (heatScore < model.MinScoreToAct)
        {
            _logger.LogInformation("[{Name}] Heat score {Score:F2} below minimum {Min:F2}, no action",
                shutter.Name, heatScore, model.MinScoreToAct);
            return false;
        }

        // Map score to target position (higher score = more closed = lower position %)
        int targetPosition = (int)Math.Round(
            model.PositionCeilingPercent - heatScore * (model.PositionCeilingPercent - model.PositionFloorPercent));
        targetPosition = Math.Clamp(targetPosition, model.PositionFloorPercent, model.PositionCeilingPercent);

        // Per-shutter cooldown check
        if (_lastAutoClose.TryGetValue(shutter.Name, out var lastClose))
        {
            var cooldownRemaining = lastClose.AddMinutes(shutter.CooldownMinutes) - DateTime.UtcNow;
            if (cooldownRemaining > TimeSpan.Zero)
            {
                _logger.LogInformation("[{Name}] In cooldown, {Min:F0} minutes remaining",
                    shutter.Name, cooldownRemaining.TotalMinutes);
                return false;
            }
        }

        // Poll current position
        var currentPosition = await _shutters.GetPositionAsync(shutter, ct);
        if (currentPosition is null)
        {
            _logger.LogWarning("[{Name}] Skipping: could not read current position", shutter.Name);
            return false;
        }

        // Only close further, never open via automation
        if (targetPosition >= currentPosition.Value)
        {
            _logger.LogInformation("[{Name}] Target {Target}% >= current {Current}%, no action needed (won't open)",
                shutter.Name, targetPosition, currentPosition.Value);
            return false;
        }

        _logger.LogInformation("[{Name}] Closing from {Current}% to {Target}% (heat score: {Score:F2})",
            shutter.Name, currentPosition.Value, targetPosition, heatScore);

        await _shutters.SetPositionAsync(shutter, targetPosition, ct);
        _lastAutoClose[shutter.Name] = DateTime.UtcNow;
        return true;
    }
}