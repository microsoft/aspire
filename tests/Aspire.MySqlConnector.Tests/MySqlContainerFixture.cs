// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.TestUtilities;
using Aspire.Hosting.MySql;
using Testcontainers.MySql;
using Xunit;
using Aspire.Components.Common.TestUtilities;

namespace Aspire.MySqlConnector.Tests;

public sealed class MySqlContainerFixture : IAsyncLifetime
{
    public MySqlContainer? Container { get; private set; }

    public string GetConnectionString() => Container?.GetConnectionString() ??
        throw new InvalidOperationException("The test container was not initialized.");

    public async ValueTask InitializeAsync()
    {
        if (RequiresFeatureAttribute.IsFeatureSupported(TestFeature.Docker))
        {
            Container = new MySqlBuilder()
                .WithImage($"{ComponentTestConstants.AspireTestContainerRegistry}/{MySqlContainerImageTags.Image}:{MySqlContainerImageTags.Tag}")
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
