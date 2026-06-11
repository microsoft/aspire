// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.TestUtilities;
using Aspire.Hosting.Milvus;
using Testcontainers.Milvus;
using Xunit;
using Aspire.Components.Common.TestUtilities;

namespace Aspire.Milvus.Client.Tests;

public sealed class MilvusContainerFixture : IAsyncLifetime
{
    public MilvusContainer? Container { get; private set; }

    public string GetConnectionString() => $"Endpoint={Container?.GetEndpoint().AbsoluteUri};Key=root:Milvus" ??
        throw new InvalidOperationException("The test container was not initialized.");

    public async ValueTask InitializeAsync()
    {
        if (RequiresFeatureAttribute.IsFeatureSupported(TestFeature.Docker))
        {
            Container = new MilvusBuilder()
                .WithImage($"{ComponentTestConstants.AspireTestContainerRegistry}/{MilvusContainerImageTags.Image}:{MilvusContainerImageTags.Tag}")
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
