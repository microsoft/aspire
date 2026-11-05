// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.TestUtilities;
using Docker.DotNet.Models;
using DotNet.Testcontainers.Configurations;
using Testcontainers.MsSql;

namespace Aspire.Hosting.Azure.Tests;

/// <summary>
/// Starts a single SQL Server container shared by every test in a class, so the T-SQL emitted by
/// <see cref="AzureSqlServerResource"/> can be executed against a real database engine.
/// </summary>
public sealed class SqlServerContainerFixture : IAsyncLifetime
{
    internal const string LoopbackHostIP = "127.0.0.1";

    public MsSqlContainer? Container { get; private set; }

    /// <summary>
    /// Whether the SQL Server port is published on the daemon's loopback address rather than on every address.
    /// </summary>
    internal bool PublishesPortOnLoopback { get; private set; }

    public string GetConnectionString() => Container?.GetConnectionString() ??
        throw new InvalidOperationException("The test container was not initialized.");

    public async ValueTask InitializeAsync()
    {
        if (RequiresFeatureAttribute.IsFeatureSupported(TestFeature.Testcontainers))
        {
            var builder = new MsSqlBuilder($"{SqlServerContainerImageTags.Registry}/{SqlServerContainerImageTags.Image}:{SqlServerContainerImageTags.Tag}");

            PublishesPortOnLoopback = CanReachDaemonLoopback();
            if (PublishesPortOnLoopback)
            {
                builder = builder.WithCreateParameterModifier(BindPublishedPortsToLoopback);
            }

            Container = builder.Build();

            await Container.StartAsync();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Container is not null)
        {
            await Container.DisposeAsync();
        }
    }

    // Publish the random SQL Server host port on loopback, the same address DCP uses for the containers
    // other tests in this assembly start concurrently. Docker Engine before 28.5 tracks random host ports
    // per host IP, so a wildcard (0.0.0.0) request can be handed ports that loopback publications already
    // hold; once ten consecutive candidates collide the daemon gives up with "failed to bind host port for
    // 0.0.0.0::<ip>:1433/tcp: address already in use". Sharing the loopback address keeps both kinds of
    // publication in one allocation pool. This only applies when the daemon's loopback is also the test
    // process's loopback; see CanReachDaemonLoopback.
    internal static void BindPublishedPortsToLoopback(CreateContainerParameters parameters)
    {
        foreach (var bindings in parameters.HostConfig?.PortBindings?.Values ?? [])
        {
            foreach (var binding in bindings)
            {
                binding.HostIP = LoopbackHostIP;
            }
        }
    }

    // Mirrors how Testcontainers resolves the container hostname: a local socket or pipe means the daemon
    // shares this process's loopback unless the tests themselves run inside a container (where Testcontainers
    // connects through the bridge gateway). For a TCP endpoint Testcontainers connects to the endpoint's host
    // verbatim, so it must be exactly the address the port is published on; [::1] or 127.0.0.2 would not reach it.
    private static bool CanReachDaemonLoopback()
    {
        if (!string.IsNullOrEmpty(TestcontainersSettings.DockerHostOverride))
        {
            return false;
        }

        var endpoint = TestcontainersSettings.OS.DockerEndpointAuthConfig.Endpoint;
        return endpoint.Scheme switch
        {
            "unix" or "npipe" => !File.Exists(Path.Combine(Path.GetPathRoot(Directory.GetCurrentDirectory()) ?? "/", ".dockerenv")),
            _ => endpoint.HostNameType == UriHostNameType.IPv4 && endpoint.Host == LoopbackHostIP
        };
    }
}
