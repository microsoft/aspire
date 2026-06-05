// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.TestUtilities;
using Aspire.Hosting.Postgres;
using Testcontainers.PostgreSql;
using Xunit;
using Aspire.Components.Common.TestUtilities;
using DotNet.Testcontainers;

namespace Aspire.Npgsql.Tests;

public sealed class PostgreSQLContainerFixture : IAsyncLifetime
{
    public PostgreSqlContainer? Container { get; private set; }

    public PostgreSQLContainerFixture()
    {
        // Enable TestContainers debug logging
        ConsoleLogger.Instance.DebugLogLevelEnabled = true;
    }

    public string GetConnectionString() => Container?.GetConnectionString() ??
        throw new InvalidOperationException("The test container was not initialized.");

    public async ValueTask InitializeAsync()
    {
        if (RequiresFeatureAttribute.IsFeatureSupported(TestFeature.Docker))
        {
            Container = new PostgreSqlBuilder()
                .WithImage($"{ComponentTestConstants.AspireTestContainerRegistry}/{PostgresContainerImageTags.Image}:{PostgresContainerImageTags.Tag}")
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
}
