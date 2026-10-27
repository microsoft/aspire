// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using StackExchange.Redis;

namespace NativeIntegrations.Redis;

/// <summary>Uses the ordinary Redis client for the sample's health, persistence and consumer commands.</summary>
internal sealed class RedisClient : IAsyncDisposable
{
    private ConnectionMultiplexer? _connection;

    /// <summary>Connects and authenticates with bounded connection and command timeouts.</summary>
    public async Task ConnectAsync(string host, int port, string password)
    {
        if (_connection is not null)
        {
            throw new InvalidOperationException("Redis is already connected.");
        }
        var options = new ConfigurationOptions
        {
            Password = password,
            AllowAdmin = true,
            ConnectTimeout = 5000,
            AsyncTimeout = 5000,
            ConnectRetry = 0
        };
        options.EndPoints.Add(host, port);
        _connection = await ConnectionMultiplexer.ConnectAsync(options).ConfigureAwait(false);
    }

    /// <summary>Executes a sample command through the library's protocol implementation.</summary>
    public async Task<string> CommandAsync(params string[] arguments)
    {
        var connection = _connection ?? throw new InvalidOperationException("Redis is not connected.");
        var result = await connection.GetDatabase().ExecuteAsync(arguments[0],
            arguments.Skip(1).Cast<object>().ToArray()).ConfigureAwait(false);

        return result.ToString();
    }

    /// <summary>Releases the sample's Redis connection.</summary>
    public ValueTask DisposeAsync()
    {
        _connection?.Dispose();

        return ValueTask.CompletedTask;
    }
}
