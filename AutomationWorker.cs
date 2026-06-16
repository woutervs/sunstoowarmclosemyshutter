using Microsoft.Extensions.Options;
using ShutterAutomation.Models;
using ShutterAutomation.Services;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ShutterAutomation;

public class AutomationWorker : BackgroundService
{
    private readonly ILogger<AutomationWorker> _logger;
    private readonly IOptionsMonitor<AutomationConfig> _config;
    private readonly SunPositionService _sunPosition;
    private readonly WeatherService _weather;
    private readonly ShutterService _shutters;

    private AutomationConfig _previousConfig = null!;
    private readonly Dictionary<string, double> _accumulatedRadiation = new();
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

        _previousConfig = _config.CurrentValue;

        _config.OnChange(newConfig =>
        {
            var oldJson = JsonSerializer.SerializeToNode(_previousConfig);
            var newJson = JsonSerializer.SerializeToNode(newConfig);

            var changes = CompareJson(oldJson!, newJson!, "");

            if (changes.Count > 0)
                _logger.LogInformation("Configuration changed:\n{Changes}", string.Join("\n", changes));
            else
                _logger.LogInformation("Configuration reloaded but no values changed");

            _previousConfig = newConfig;
        });
    }

    private static List<string> CompareJson(JsonNode oldNode, JsonNode newNode, string path)
    {
        var changes = new List<string>();

        if (oldNode is JsonObject oldObj && newNode is JsonObject newObj)
        {
            foreach (var key in oldObj.Select(x => x.Key).Union(newObj.Select(x => x.Key)))
            {
                var childPath = string.IsNullOrEmpty(path) ? key : $"{path}.{key}";
                var oldChild = oldObj[key];
                var newChild = newObj[key];

                if (oldChild is null || newChild is null)
                {
                    changes.Add($"{childPath}: {oldChild} → {newChild}");
                    continue;
                }

                changes.AddRange(CompareJson(oldChild, newChild, childPath));
            }
        }
        else if (oldNode is JsonArray oldArr && newNode is JsonArray newArr)
        {
            for (int i = 0; i < Math.Max(oldArr.Count, newArr.Count); i++)
            {
                var childPath = $"{path}[{i}]";
                if (i >= oldArr.Count || i >= newArr.Count)
                    changes.Add($"{childPath}: {oldArr.ElementAtOrDefault(i)} → {newArr.ElementAtOrDefault(i)}");
                else
                    changes.AddRange(CompareJson(oldArr[i]!, newArr[i]!, childPath));
            }
        }
        else if (oldNode.ToJsonString() != newNode.ToJsonString())
        {
            changes.Add($"{path}: {oldNode} → {newNode}");
        }

        return changes;
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

        _logger.LogInformation("Weather: {Temp:F1}°C, radiation={Rad:F0} W/m²",
            weather.TemperatureCelsius, weather.DirectRadiationWm2);

        // 3. Sun elevation gate
        if (sun.ElevationDegrees < config.MinElevationDegrees)
        {
            _logger.LogInformation("Sun elevation {Elev:F1}° below minimum {Min}°, skipping",
                sun.ElevationDegrees, config.MinElevationDegrees);
            return;
        }

        // 4. Act on each shutter independently
        foreach (var shutter in config.Shutters)
        {
            await ProcessShutterAsync(shutter, sun, weather, ct);
        }
    }

    private async Task<bool> ProcessShutterAsync(
        ShutterConfig shutter,
        SunPositionService.SunPosition sun,
        WeatherReading weather,
        CancellationToken ct)
    {
        var model = shutter.HeatModel;

        // Update per-shutter radiation accumulator
        _accumulatedRadiation.TryGetValue(shutter.Name, out var accumulated);
        bool sunInWindow = sun.AzimuthDegrees >= model.AccumulationAzimuthMin
                        && sun.AzimuthDegrees <= model.AccumulationAzimuthMax;
        if (sunInWindow)
        {
            accumulated = accumulated * model.AccumulationDecay + weather.DirectRadiationWm2;
            accumulated = Math.Min(accumulated, model.AccumulationMax);
        }
        else
        {
            accumulated *= model.AccumulationDecay;
        }
        _accumulatedRadiation[shutter.Name] = accumulated;

        // Compute individual scores (0–1)
        double tempScore = Math.Clamp(
            (weather.TemperatureCelsius - model.TempMin) / (model.TempMax - model.TempMin), 0, 1);

        double instantRadScore = Math.Clamp(
            weather.DirectRadiationWm2 / model.RadiationMax, 0, 1);

        double accRadScore = Math.Clamp(
            accumulated / model.AccumulationMax, 0, 1);

        // Soft azimuth score — cosine falloff from center, 0 outside window
        double azimuthDelta = Math.Abs(sun.AzimuthDegrees - shutter.Azimuth.Center);
        double halfWidth = shutter.Azimuth.Width / 2.0;
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
            "[{Name}] scores: temp={T:F2} rad={R:F2} acc={A:F2} azimuth={Az:F2} → heat={H:F2} (accumulated={Acc:F0})",
            shutter.Name, tempScore, instantRadScore, accRadScore, azimuthScore, heatScore, accumulated);

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