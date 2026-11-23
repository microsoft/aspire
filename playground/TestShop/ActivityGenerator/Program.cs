using ActivityGenerator;

var builder = Host.CreateApplicationBuilder(args);

builder.AddServiceDefaults();
builder.Services.AddOpenTelemetry()
    .WithTracing(tracing => tracing.AddSource(ActivityGenerationWorker.ActivitySourceName));

foreach (var resource in new[] { "frontend", "apigateway", "catalogservice" })
{
    builder.Services.AddHttpClient(resource, client =>
    {
        client.BaseAddress = new Uri($"https+http://{resource}");
        client.Timeout = TimeSpan.FromSeconds(10);
        client.DefaultRequestHeaders.UserAgent.ParseAdd("TestShopDashboardLoad/1.0");
    });
}

builder.Services.AddHostedService<ActivityGenerationWorker>();

using var host = builder.Build();
await host.RunAsync();
