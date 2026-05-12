using NATS.Client.Core;
using Nats.Common;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

builder.AddNatsClient("nats");

builder.Services.AddHostedService<AppEventsBackendService>();

var app = builder.Build();
app.MapDefaultEndpoints();
app.Run();

public class AppEventsBackendService(INatsClient nats,  ILogger<AppEventsBackendService> logger) : IHostedService
{
    private readonly CancellationTokenSource _cts = new();
    private Task? _subscription;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _subscription = Task.Run(async () =>
        {
            await foreach (var msg in nats.SubscribeAsync<AppEvent>("events.>", cancellationToken: _cts.Token).ConfigureAwait(false))
            {
                logger.LogInformation("Processing event: {Data}", msg.Data);
            }
        }, cancellationToken);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await _cts.CancelAsync();
        if (_subscription != null)
        {
            await _subscription;
        }
    }
}
