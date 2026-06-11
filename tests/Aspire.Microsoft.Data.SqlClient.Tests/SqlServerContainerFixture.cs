// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.TestUtilities;
using Aspire.Hosting;
using Testcontainers.MsSql;
using Xunit;

namespace Aspire.Microsoft.Data.SqlClient.Tests;

public sealed class SqlServerContainerFixture : IAsyncLifetime
{
    public MsSqlContainer? Container { get; private set; }

    public string GetConnectionString() => Container?.GetConnectionString() ??
        throw new InvalidOperationException("The test container was not initialized.");

    public async ValueTask InitializeAsync()
    {
        if (RequiresFeatureAttribute.IsFeatureSupported(TestFeature.Docker))
        {
            Container = new MsSqlBuilder()
                            .WithImage($"{SqlServerContainerImageTags.Registry}/{SqlServerContainerImageTags.Image}:{SqlServerContainerImageTags.Tag}")
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
