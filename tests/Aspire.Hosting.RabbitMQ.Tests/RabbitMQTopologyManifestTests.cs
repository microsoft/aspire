// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json.Nodes;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Utils;
using Microsoft.Extensions.DependencyInjection;

namespace Aspire.Hosting.RabbitMQ.Tests;

/// <summary>
/// Verifies that RabbitMQ topology child resources (virtual hosts, queues, exchanges, policies, shovels)
/// are INCLUDED in manifest output alongside the server. These resources are part of the application model
/// and must be represented in the manifest — they are not excluded from it.
/// </summary>
public class RabbitMQTopologyManifestTests
{
    [Fact]
    public async Task TopologyResourcesAreIncludedInManifest()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        // Build one of each topology child under a single server: a named vhost, a queue and an
        // exchange on that vhost, a policy on that vhost, and a shovel moving from the queue to the
        // exchange.
        var server = builder.AddRabbitMQ("rabbit");
        var vhost = server.AddVirtualHost("myvhost");
        var queue = vhost.AddQueue("myqueue");
        var exchange = vhost.AddExchange("myexchange");
        vhost.AddPolicy("mypolicy", "^my");
        vhost.AddShovel("myshovel", queue, exchange);

        using var app = builder.Build();
        var model = app.Services.GetRequiredService<DistributedApplicationModel>();

        var fullManifest = await ManifestUtils.GetManifestForModel(model);
        var resources = Assert.IsType<JsonObject>(fullManifest["resources"]);

        // The server itself is deployable and MUST be emitted as a manifest resource.
        Assert.True(resources.ContainsKey("rabbit"), "The RabbitMQ server should be present in the manifest.");
        Assert.Equal("container.v0", resources["rabbit"]!["type"]!.GetValue<string>());

        // All topology children must appear as keys in the manifest's resources object.
        Assert.True(resources.ContainsKey("myvhost"), "Virtual host should be included in the manifest.");
        Assert.True(resources.ContainsKey("myqueue"), "Queue should be included in the manifest.");
        Assert.True(resources.ContainsKey("myexchange"), "Exchange should be included in the manifest.");
        Assert.True(resources.ContainsKey("mypolicy"), "Policy should be included in the manifest.");
        Assert.True(resources.ContainsKey("myshovel"), "Shovel should be included in the manifest.");
    }

    [Fact]
    public async Task TopologyResourceManifestNodeIsNotNull()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);

        var server = builder.AddRabbitMQ("rabbit");
        var vhost = server.AddVirtualHost("myvhost");
        var queue = vhost.AddQueue("myqueue");

        // Topology resources are included in the manifest, so per-resource manifest generation emits a node.
        Assert.NotNull(await ManifestUtils.GetManifestOrNull(vhost.Resource));
        Assert.NotNull(await ManifestUtils.GetManifestOrNull(queue.Resource));
    }
}
