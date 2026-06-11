// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.Extensions.Logging;

namespace Aspire.Dashboard.Tests.Integration;

// Copied from https://github.com/dotnet/aspnetcore/blob/1b2e5286b089fa1cab90ba8692c2df7ca6f9c077/src/Servers/Kestrel/shared/test/ServerRetryHelper.cs
public static class ServerRetryHelper
{
    private const int RetryCount = 20;

    // Named mutex to prevent race conditions when multiple parallel tests (even across processes)
    // try to find and bind ports. Without this, GetAvailablePort can return the same port to
    // multiple tests before any of them bind.
    // Note: Using a session-local mutex (no "Global\" prefix) which is sufficient for tests
    // running in the same user session and avoids permission issues on some platforms.
    private const string PortAllocationMutexName = "AspireDashboardTestPortAllocation";

    /// <summary>
    /// Retry a func. Useful when a test needs an explicit port and you want to avoid port conflicts.
    /// </summary>
    public static Task BindPortWithRetry(Func<int, Task> retryFunc, ILogger logger)
    {
        return BindPortsWithRetry(ports => retryFunc(ports.Single()), logger, portCount: 1);
    }

    /// <summary>
    /// Retry a func. Useful when a test needs an explicit port and you want to avoid port conflicts.
    /// </summary>
    public static async Task BindPortsWithRetry(Func<List<int>, Task> retryFunc, ILogger logger, int portCount)
    {
        var retryCount = 0;

        // Add a random number to starting port to reduce chance of conflicts because of multiple tests using this retry.
        var nextPortAttempt = 30000 + Random.Shared.Next(10000);

        // Use a named mutex to prevent multiple parallel tests (even across processes) from
        // selecting the same port between the time we find it and the time the test binds to it.
        using var mutex = new Mutex(initiallyOwned: false, PortAllocationMutexName);

        while (true)
        {
            // Find a port that's available for TCP and UDP. Start with the given port search upwards from there.
            var ports = new List<int>(portCount);

            if (!mutex.WaitOne(TestConstants.DefaultTimeoutTimeSpan))
            {
                throw new TimeoutException($"Timed out waiting for port allocation mutex after {TestConstants.DefaultTimeoutTimeSpan}.");
            }

            try
            {
                while (ports.Count < portCount)
                {
                    var port = GetAvailablePort(ref nextPortAttempt, logger);
                    ports.Add(port);
                }

                // Should never happen, but sanity check to ensure we have unique ports.
                if (ports.Count != ports.Distinct().Count())
                {
                    throw new InvalidOperationException($"Generated ports list contains duplicate numbers: {string.Join(", ", ports)}");
                }

                // Call retryFunc inside the mutex so the ports are bound before we release.
                // This prevents another test from grabbing the same ports.
                await retryFunc(ports);

                // Success - exit the retry loop
                return;
            }
            catch (Exception ex)
            {
                retryCount++;

                if (retryCount >= RetryCount)
                {
                    throw;
                }

                logger.LogError(ex, "Error running test {retryCount}. Retrying.", retryCount);
            }
            finally
            {
                mutex.ReleaseMutex();
            }
        }
    }

    /// <summary>
    /// Finds an available port starting from nextPortAttempt, verifies it by binding, and updates nextPortAttempt.
    /// </summary>
    private static int GetAvailablePort(ref int nextPortAttempt, ILogger logger)
    {
        logger.LogInformation("Searching for free port starting at {nextPortAttempt}.", nextPortAttempt);

        var unavailableEndpoints = new List<IPEndPoint>();

        var properties = IPGlobalProperties.GetIPGlobalProperties();

        // Ignore active connections
        AddEndpoints(nextPortAttempt, unavailableEndpoints, properties.GetActiveTcpConnections().Select(c => c.LocalEndPoint));

        // Ignore active tcp listeners
        AddEndpoints(nextPortAttempt, unavailableEndpoints, properties.GetActiveTcpListeners());

        // Ignore active UDP listeners
        AddEndpoints(nextPortAttempt, unavailableEndpoints, properties.GetActiveUdpListeners());

        logger.LogInformation("Found {count} unavailable endpoints.", unavailableEndpoints.Count);

        while (nextPortAttempt < ushort.MaxValue)
        {
            var port = nextPortAttempt;

            // Always increase nextPortAttempt by a random amount to reduce the risk of port collisions.
            // Allocating consecutive ports (gap of 0) can lead to conflicts if the OS or other processes
            // allocate ports in the same range. The random gap further reduces the chance of collision.
            nextPortAttempt = port + Random.Shared.Next(10, 100);

            var match = unavailableEndpoints.FirstOrDefault(ep => ep.Port == port);
            if (match is not null)
            {
                logger.LogInformation("Port {port} in use. End point: {match}", port, match);
                continue;
            }

            // Port appears free, verify by actually binding to it.
            // This catches cases where IPGlobalProperties doesn't report the port as in use,
            // but it's actually unavailable (e.g., TIME_WAIT state on some platforms).
            if (TryBindPort(port, logger))
            {
                logger.LogInformation("Port {port} free and verified.", port);
                return port;
            }
            else
            {
                logger.LogInformation("Port {port} appeared free but failed to bind. Continuing search.", port);
            }
        }

        throw new InvalidOperationException($"Exhausted all available ports. Couldn't find a free port after {nextPortAttempt}.");

        static void AddEndpoints(int startingPort, List<IPEndPoint> endpoints, IEnumerable<IPEndPoint> activeEndpoints)
        {
            foreach (IPEndPoint endpoint in activeEndpoints)
            {
                if (endpoint.Port >= startingPort)
                {
                    endpoints.Add(endpoint);
                }
            }
        }

        static bool TryBindPort(int port, ILogger logger)
        {
            // Since we only bind without calling Listen() or Connect(), we never enter TIME_WAIT.
            // The port is released immediately on dispose.
            try
            {
                using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                socket.Bind(new IPEndPoint(IPAddress.Loopback, port));
                // Successfully bound, port is available.
                return true;
            }
            catch (SocketException ex) when (ex.SocketErrorCode == SocketError.AddressAlreadyInUse)
            {
                logger.LogInformation("Port {port} bind check failed: address already in use.", port);
                return false;
            }
            catch (SocketException ex)
            {
                logger.LogWarning(ex, "Port {port} bind check failed with unexpected error.", port);
                return false;
            }
        }
    }
}
