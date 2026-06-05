// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Net;
using System.Net.Sockets;
using Polly;
using Polly.Retry;
using Polly.Timeout;

namespace Aspire.Hosting.Tests.Helpers;

internal static class Network
{
    /// <summary>
    /// Finds an available (unoccupied) TCP port on the specified network address by attempting
    /// to bind a socket to randomly chosen ports within the given range.
    /// </summary>
    /// <param name="minPort">The inclusive lower bound of the random port range.</param>
    /// <param name="maxPort">The inclusive upper bound of the random port range.</param>
    /// <param name="address">The network address to bind to (e.g. "127.0.0.1").</param>
    /// <returns>An available port number.</returns>
    /// <exception cref="InvalidOperationException">Thrown when no available port is found within the timeout period.</exception>
    public static async Task<int> GetAvailablePortAsync(
        int minPort = 10000,
        int maxPort = 20000,
        string address = "127.0.0.1")
    {
        var triedPorts = new List<int>();
        var random = new Random();
        var ipAddress = IPAddress.Parse(address);

        var pipeline = new ResiliencePipelineBuilder<int>()
            .AddTimeout(new TimeoutStrategyOptions { Timeout = TimeSpan.FromSeconds(10) })
            .AddRetry(new RetryStrategyOptions<int>
            {
                ShouldHandle = new PredicateBuilder<int>().Handle<SocketException>(),
                BackoffType = DelayBackoffType.Exponential,
                Delay = TimeSpan.FromMilliseconds(50),
                MaxDelay = TimeSpan.FromSeconds(2),
                MaxRetryAttempts = int.MaxValue,
            })
            .Build();

        try
        {
            return await pipeline.ExecuteAsync(async (ct) =>
            {
                var port = random.Next(minPort, maxPort + 1);
                triedPorts.Add(port);

                using var socket = new Socket(ipAddress.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
                socket.Bind(new IPEndPoint(ipAddress, port));
                socket.Close();

                return port;
            });
        }
        catch (TimeoutRejectedException)
        {
            throw new InvalidOperationException(
                $"Could not find an available port on address '{address}' after 10 seconds. " +
                $"Tried ports: {string.Join(", ", triedPorts)}");
        }
    }
}
