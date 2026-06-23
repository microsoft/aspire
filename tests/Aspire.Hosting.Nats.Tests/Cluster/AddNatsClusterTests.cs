// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Tests.Utils;
using Aspire.Hosting.Utils;
using Microsoft.Extensions.DependencyInjection;

namespace Aspire.Hosting.Nats.Tests;

public class AddNatsClusterTests(ITestOutputHelper testOutputHelper)
{
    [Fact]
    public void AddNatsClusterAddsResourceToModel()
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);
        builder.AddNatsCluster("nats-cluster");

        using var app = builder.Build();
        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();

        var resource = Assert.Single(appModel.Resources.OfType<NatsClusterResource>());
        Assert.Equal("nats-cluster", resource.Name);
    }

    [Fact]
    public void AddNatsClusterAddsHealthCheckAnnotationToResource()
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);
        var cluster = builder.AddNatsCluster("cluster");
        Assert.Single(cluster.Resource.Annotations, a => a is HealthCheckAnnotation hca);
    }

    [Fact]
    public void WithMemberAddsWaitAnnotationForMember()
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);
        var natsNode = builder.AddNats("nats-1");
        var cluster = builder.AddNatsCluster("cluster")
            .WithMember(natsNode);

        Assert.Single(cluster.Resource.Annotations.OfType<WaitAnnotation>(),
            a => a.Resource == natsNode.Resource);
    }

    [Fact]
    public async Task WithMemberConfiguresServerWithClusterArg()
    {
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);
        var natsNode = builder.AddNats("nats-1");
        builder.AddNatsCluster("rs0")
            .WithMember(natsNode);

        var args = await ArgumentEvaluator.GetArgumentListAsync(natsNode.Resource);
        Assert.Contains("--cluster", args);
    }
}
