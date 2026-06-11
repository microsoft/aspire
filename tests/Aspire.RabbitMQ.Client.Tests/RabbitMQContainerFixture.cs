// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.TestUtilities;
using Aspire.Hosting.RabbitMQ;
using Testcontainers.RabbitMq;
using Xunit;
using Aspire.Components.Common.TestUtilities;

namespace Aspire.RabbitMQ.Client.Tests;

public sealed class RabbitMQContainerFixture : IAsyncLifetime
{
    private RabbitMqContainer? _container;

    public string GetConnectionString() => _container?.GetConnectionString() ??
        throw new InvalidOperationException("The test container was not initialized.");

    public async ValueTask InitializeAsync()
    {
        if (RequiresFeatureAttribute.IsFeatureSupported(TestFeature.Docker))
        {
            _container = await CreateContainerAsync();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_container is not null)
        {
            await _container.DisposeAsync();
        }
    }

    public static async Task<RabbitMqContainer> CreateContainerAsync()
    {
        var container = new RabbitMqBuilder()
            .WithImage($"{ComponentTestConstants.AspireTestContainerRegistry}/{RabbitMQContainerImageTags.Image}:{RabbitMQContainerImageTags.Tag}")
            .Build();
        await container.StartAsync();

        return container;
    }
}
