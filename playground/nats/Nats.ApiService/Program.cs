using System.Text.Json;
using NATS.Client.Core;
using NATS.Client.JetStream;
using NATS.Client.JetStream.Models;
using Nats.Common;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.AddServiceDefaults();

builder.AddNatsClient("nats");

builder.AddNatsJetStream();

var app = builder.Build();

var jetStream = app.Services.GetRequiredService<INatsJSContext>();
await jetStream.CreateOrUpdateStreamAsync(new StreamConfig("events", ["events.>"]));

app.UseFileServer();

// Configure the HTTP request pipeline.
app.MapDefaultEndpoints();
app.MapGet("/ping", async (INatsClient nats) =>
{
    var rtt = await nats.PingAsync();
    return Results.Json(new { rtt, nats.Connection.ServerInfo });
});

app.MapPost("/publish/", async (AppEvent @event, INatsJSContext js) =>
{
    var ack = await js.PublishAsync(@event.Subject, @event);
    ack.EnsureSuccess();
    return Results.Ok(new { ack.Stream, ack.Seq, ack.Duplicate });
});

app.MapGet("/consume", async (INatsJSContext js, HttpContext ctx, CancellationToken ct) =>
{
    ctx.Response.Headers.ContentType = "text/event-stream";
    ctx.Response.Headers.CacheControl = "no-cache";

    var stream = await js.GetStreamAsync("events", cancellationToken: ct);
    var consumer = await stream.CreateOrderedConsumerAsync(cancellationToken: ct);

    await foreach (var msg in consumer.ConsumeAsync<AppEvent>(cancellationToken: ct))
    {
        if (msg.Data is null)
        {
            continue;
        }

        await ctx.Response.WriteAsync($"data: {JsonSerializer.Serialize(msg.Data)}\n\n", ct);
        await ctx.Response.Body.FlushAsync(ct);
    }
});

app.MapGet("/consume/{name}", async (string name, INatsJSContext js, HttpContext ctx, CancellationToken ct) =>
{
    ctx.Response.Headers.ContentType = "text/event-stream";
    ctx.Response.Headers.CacheControl = "no-cache";

    var stream = await js.GetStreamAsync("events", cancellationToken: ct);
    var consumer = await stream.CreateOrUpdateConsumerAsync(new ConsumerConfig(name), ct);

    await foreach (var msg in consumer.ConsumeAsync<AppEvent>(cancellationToken: ct))
    {
        if (msg.Data is null)
        {
            continue;
        }

        await ctx.Response.WriteAsync($"data: {JsonSerializer.Serialize(msg.Data)}\n\n", ct);
        await ctx.Response.Body.FlushAsync(ct);
        await msg.AckAsync(cancellationToken: ct);
    }
});

app.Run();
