// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.TestUtilities;
using Docker.DotNet.Models;
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

    public string GetConnectionString() => Container?.GetConnectionString() ??
        throw new InvalidOperationException("The test container was not initialized.");

    public async ValueTask InitializeAsync()
    {
        if (RequiresFeatureAttribute.IsFeatureSupported(TestFeature.Testcontainers))
        {
            Container = new MsSqlBuilder($"{SqlServerContainerImageTags.Registry}/{SqlServerContainerImageTags.Image}:{SqlServerContainerImageTags.Tag}")
                .WithCreateParameterModifier(BindPublishedPortsToLoopback)
                .Build();

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
    // publication in one allocation pool, and the tests only ever connect through 127.0.0.1.
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
}
