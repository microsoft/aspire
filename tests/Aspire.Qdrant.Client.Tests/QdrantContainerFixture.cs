// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.TestUtilities;
using Aspire.Hosting.Qdrant;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Xunit;
using Aspire.Components.Common.TestUtilities;

namespace Aspire.Qdrant.Client.Tests;

public sealed class QdrantContainerFixture : IAsyncLifetime
{
    public IContainer? Container { get; private set; }

    private const int GrpcPort = 6334;

    public string GetConnectionString()
    {
        if (Container is null)
        {
            throw new InvalidOperationException("The test container was not initialized.");
        }
        var endpoint = new UriBuilder("http", Container.Hostname, Container.GetMappedPublicPort(GrpcPort)).ToString();
        return $"Endpoint={endpoint}";
    }

    public async ValueTask InitializeAsync()
    {
        if (RequiresFeatureAttribute.IsFeatureSupported(TestFeature.Docker))
        {
            Container = new ContainerBuilder()
              .WithImage($"{ComponentTestConstants.AspireTestContainerRegistry}/{QdrantContainerImageTags.Image}:{QdrantContainerImageTags.Tag}")
              .WithPortBinding(GrpcPort, true)
              .WithWaitStrategy(Wait.ForUnixContainer().UntilExternalTcpPortIsAvailable(GrpcPort))
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
