// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.TestUtilities;
using Aspire.Hosting.MongoDB;
using Testcontainers.MongoDb;
using Xunit;
using Aspire.Components.Common.TestUtilities;

namespace Aspire.MongoDB.Driver.Tests;

public sealed class MongoDbContainerFixture : IAsyncLifetime
{
    public MongoDbContainer? Container { get; private set; }

    public string GetConnectionString() => Container?.GetConnectionString() ??
        throw new InvalidOperationException("The test container was not initialized.");

    public async ValueTask InitializeAsync()
    {
        if (RequiresFeatureAttribute.IsFeatureSupported(TestFeature.Docker))
        {
            // testcontainers uses mongo:mongo by default,
            // resetting that for tests
            Container = new MongoDbBuilder()
                .WithImage($"{ComponentTestConstants.AspireTestContainerRegistry}/{MongoDBContainerImageTags.Image}:{MongoDBContainerImageTags.Tag}")
                .WithUsername(null)
                .WithPassword(null)
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
