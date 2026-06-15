using System.Text.Json;
using Microsoft.Extensions.Logging;
using ShutterAutomation.Models;

namespace ShutterAutomation.Services;

public class ShutterService
{
    private readonly HttpClient _http;
    private readonly ILogger<ShutterService> _logger;

    public ShutterService(HttpClient http, ILogger<ShutterService> logger)
    {
        _http = http;
        _logger = logger;
    }

    /// <summary>
    /// Returns the current position of a shutter (0 = fully closed, 100 = fully open).
    /// Returns null if the request fails.
    /// </summary>
    public async Task<int?> GetPositionAsync(ShutterConfig shutter, CancellationToken ct = default)
    {
        var url = $"http://{shutter.Host}/roller/{shutter.Channel}";

        try
        {
            var response = await _http.GetAsync(url, ct);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);

            // current_pos: 0 = closed, 100 = open
            var position = doc.RootElement.GetProperty("current_pos").GetInt32();
            _logger.LogDebug("[{Name}] Current position: {Pos}%", shutter.Name, position);
            return position;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[{Name}] Failed to get position from {Host}/roller/{Channel}",
                shutter.Name, shutter.Host, shutter.Channel);
            return null;
        }
    }

    /// <summary>
    /// Sends a go-to-position command. Position is 0–100 (Shelly convention: 0 = closed, 100 = open).
    /// </summary>
    public async Task<bool> SetPositionAsync(ShutterConfig shutter, int targetPosition, CancellationToken ct = default)
    {
        // Shelly Gen2 API: POST /roller/{channel}?go=to_pos&roller_pos={pos}
        var url = $"http://{shutter.Host}/roller/{shutter.Channel}?go=to_pos&roller_pos={targetPosition}";

        try
        {
            var response = await _http.GetAsync(url, ct);
            response.EnsureSuccessStatusCode();
            _logger.LogInformation("[{Name}] Command sent: move to {Pos}%", shutter.Name, targetPosition);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[{Name}] Failed to set position to {Pos}% on {Host}/roller/{Channel}",
                shutter.Name, targetPosition, shutter.Host, shutter.Channel);
            return false;
        }
    }
}
