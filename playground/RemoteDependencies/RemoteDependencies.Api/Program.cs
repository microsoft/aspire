// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using StackExchange.Redis;

var builder = WebApplication.CreateBuilder(args);
var connectionString = builder.Configuration.GetConnectionString("cache")
    ?? throw new InvalidOperationException("Aspire must supply ConnectionStrings__cache.");
builder.Services.AddSingleton<IConnectionMultiplexer>(_ =>
    ConnectionMultiplexer.Connect(connectionString + ",abortConnect=false,connectTimeout=2000,syncTimeout=2000,asyncTimeout=2000"));
var app = builder.Build();
app.MapGet("/", async (IConnectionMultiplexer connection, ILogger<Program> logger) =>
{
    try
    {
        var count = await connection.GetDatabase().StringIncrementAsync("remote-development:requests");
        return Results.Ok(new { requests = count, cache = connectionString, application = "local" });
    }
    catch (RedisException ex)
    {
        logger.LogError(ex, "Remote cache unavailable through SSH");
        return Results.Problem("Remote cache unavailable through SSH. See the cache resource logs.", statusCode: 503);
    }
});
await app.RunAsync();
