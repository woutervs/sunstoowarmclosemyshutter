using Microsoft.Extensions.Logging;
using System.Globalization;
using System.Text.Json;

namespace ShutterAutomation.Services;

public record WeatherReading(double TemperatureCelsius, double DirectRadiationWm2);

public class WeatherService
{
    private readonly HttpClient _http;
    private readonly ILogger<WeatherService> _logger;

    // Open-Meteo free API — no key required
    private const string BaseUrl = "https://api.open-meteo.com/v1/forecast";

    public WeatherService(HttpClient http, ILogger<WeatherService> logger)
    {
        _http = http;
        _logger = logger;
    }

    public async Task<WeatherReading?> GetCurrentAsync(double latitude, double longitude, string timeZoneId, CancellationToken ct = default)
    {
        var url = string.Format(CultureInfo.InvariantCulture,
                      "{0}?latitude={1:F4}&longitude={2:F4}", BaseUrl, latitude, longitude) +
                  $"&current=temperature_2m,direct_radiation" +
                  $"&timezone={Uri.EscapeDataString(timeZoneId)}";

        try
        {
            var response = await _http.GetAsync(url, ct);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);

            var current = doc.RootElement.GetProperty("current");

            double temperature = current.GetProperty("temperature_2m").GetDouble();
            double radiation = current.GetProperty("direct_radiation").GetDouble();

            _logger.LogDebug("Weather: {Temp}°C, DirectRadiation: {Rad} W/m²", temperature, radiation);

            return new WeatherReading(temperature, radiation);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to fetch weather data from Open-Meteo");
            return null;
        }
    }
}
