using ShutterAutomation;
using ShutterAutomation.Models;
using ShutterAutomation.Services;

var host = Host.CreateDefaultBuilder(args)
    .ConfigureServices((context, services) =>
    {
        // Config
        services.AddOptions<AutomationConfig>()
            .Bind(context.Configuration.GetSection("Automation"));

        // HttpClients
        services.AddHttpClient<WeatherService>();
        services.AddHttpClient<ShutterService>(client =>
        {
            client.Timeout = TimeSpan.FromSeconds(10);
        });

        // Services
        services.AddSingleton<SunPositionService>();

        // Worker
        services.AddHostedService<AutomationWorker>();
    })
    .Build();

await host.RunAsync();
