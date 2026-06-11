// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Components.Common.TestUtilities;
using Aspire.Hosting.Nats;
using Aspire.TestUtilities;
using Testcontainers.Nats;
using Xunit;

namespace Aspire.NATS.Net.Tests;

public sealed class NatsContainerFixture : IAsyncLifetime
{
    public NatsContainer? Container { get; private set; }

    public string GetConnectionString() => Container?.GetConnectionString() ??
        throw new InvalidOperationException("The test container was not initialized.");

    public async ValueTask InitializeAsync()
    {
        if (RequiresFeatureAttribute.IsFeatureSupported(TestFeature.Docker))
        {
            Container = new NatsBuilder()
                .WithImage($"{ComponentTestConstants.AspireTestContainerRegistry}/{NatsContainerImageTags.Image}:{NatsContainerImageTags.Tag}")
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
