// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Model;
using Aspire.Hosting.Utils;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.Extensions.DependencyInjection;

namespace Aspire.Hosting.Tests;

[Trait("Partition", "5")]
public class AddConnectionStringTests
{
    [Fact]
    public async Task AddConnectionStringExpressionIsAValueInTheManifest()
    {
        var appBuilder = DistributedApplication.CreateBuilder();

        var endpoint = appBuilder.AddParameter("endpoint", "http://localhost:3452");
        var key = appBuilder.AddParameter("key", "secretKey", secret: true);

        // Get the service provider.
        appBuilder.AddConnectionString("mycs", ReferenceExpression.Create($"Endpoint={endpoint};Key={key}"));

        using var app = appBuilder.Build();

        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();
        var connectionStringResource = Assert.Single(appModel.Resources.OfType<ConnectionStringResource>());

        Assert.Equal("mycs", connectionStringResource.Name);
        var connectionStringManifest = await ManifestUtils.GetManifest(connectionStringResource).DefaultTimeout();

        var expectedManifest = $$"""
            {
              "type": "value.v0",
              "connectionString": "Endpoint={endpoint.value};Key={key.value}"
            }
            """;

        var s = connectionStringManifest.ToString();

        Assert.Equal(expectedManifest, s);
    }

    [Fact]
    public void ConnectionStringsAreVisibleByDefault()
    {
        var appBuilder = DistributedApplication.CreateBuilder();
        var endpoint = appBuilder.AddParameter("endpoint", "http://localhost:3452");
        var key = appBuilder.AddParameter("key", "secretKey", secret: true);

        appBuilder.AddConnectionString("testcs", ReferenceExpression.Create($"Endpoint={endpoint};Key={key}"));

        using var app = appBuilder.Build();

        var appModel = app.Services.GetRequiredService<DistributedApplicationModel>();

        var connectionStringResource = Assert.Single(appModel.Resources.OfType<ConnectionStringResource>());
        var annotation = connectionStringResource.Annotations.OfType<ResourceSnapshotAnnotation>().SingleOrDefault();

        Assert.NotNull(annotation);

        var state = annotation.InitialSnapshot;

        Assert.False(state.IsHidden);
        Assert.Equal(KnownResourceTypes.ConnectionString, state.ResourceType);
        Assert.Equal(KnownResourceStates.NotStarted, state.State?.Text);
    }

    [Fact]
    public void ConnectionStringResourceAddsWaitAnnotationsForReferencedResources()
    {
        using var builder = TestDistributedApplicationBuilder.Create();

        var redis = builder.AddContainer("c", "redis").WithEndpoint(name: "tcp");
        var key = builder.AddParameter("key", "secretKey", secret: true);
        var rwl = builder.AddResource(new ResourceWithoutLifetime("rwl"));

        var cs = builder.AddConnectionString("mycs",
            ReferenceExpression.Create($"Endpoint={redis.GetEndpoint("tcp")};key={key};{rwl}"));

        cs.Resource.TryGetAnnotationsOfType<WaitAnnotation>(out var waitAnnotations);

        Assert.NotNull(waitAnnotations);

        Assert.Collection(waitAnnotations,
            wa =>
            {
                Assert.Same(redis.Resource, wa.Resource);
                Assert.Equal(WaitType.WaitUntilStarted, wa.WaitType);
            },
            wa =>
            {
                Assert.Same(key.Resource, wa.Resource);
                Assert.Equal(WaitType.WaitUntilStarted, wa.WaitType);
            });
    }

    private sealed class ResourceWithoutLifetime(string name) : Resource(name), IResourceWithConnectionString, IResourceWithoutLifetime
    {
        public ReferenceExpression ConnectionStringExpression =>
            ReferenceExpression.Create($"ResourceWithoutLifetime");
    }
}