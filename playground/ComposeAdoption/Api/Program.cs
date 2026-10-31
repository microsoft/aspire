// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Net.Sockets;
using System.Text;

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

app.MapGet("/", async (IConfiguration configuration, CancellationToken cancellationToken) =>
{
    // Redis RESP: the same counter is used by container and local-source modes.
    var response = await SendRedisAsync(configuration, "*2\r\n$4\r\nINCR\r\n$6\r\nvisits\r\n"u8.ToArray(), cancellationToken);
    if (!response.StartsWith(':') || !long.TryParse(response.AsSpan(1), out var visits))
    {
        throw new InvalidOperationException($"Redis INCR failed: {response}");
    }
    return Results.Ok(new { implementation = configuration["IMPLEMENTATION"] ?? "local-dotnet", visits });
});

app.MapGet("/health", async (IConfiguration configuration, CancellationToken cancellationToken) =>
{
    var response = await SendRedisAsync(configuration, "*1\r\n$4\r\nPING\r\n"u8.ToArray(), cancellationToken);
    if (response != "+PONG")
    {
        throw new InvalidOperationException($"Redis PING failed: {response}");
    }
    return Results.Ok(new { redis = "ready" });
});

app.Run();

static async Task<string> SendRedisAsync(IConfiguration configuration, ReadOnlyMemory<byte> command, CancellationToken cancellationToken)
{
    var redis = new Uri(configuration["REDIS_URL"] ?? throw new InvalidOperationException("REDIS_URL is required."));
    using var connection = new TcpClient();
    await connection.ConnectAsync(redis.Host, redis.Port, cancellationToken);
    await using var stream = connection.GetStream();
    await stream.WriteAsync(command, cancellationToken);
    using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);
    return await reader.ReadLineAsync(cancellationToken) ?? throw new InvalidOperationException("Redis closed the connection before responding.");
}
