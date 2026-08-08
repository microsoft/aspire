// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIRECOMPUTE002

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Radius.Publishing;
using Aspire.Hosting.Radius.ResourceMapping;
using Aspire.Hosting.Utils;
using Microsoft.Extensions.DependencyInjection;

namespace Aspire.Hosting.Radius.Tests.Publishing;

/// <summary>
/// Guards the invariants that keep https://github.com/microsoft/aspire/issues/18935 fixed:
/// the connection-schema table stays total over the resource types the mapper emits, and the
/// endpoint guard behaves identically no matter which entry point reaches it.
/// </summary>
public class BackingResourceContractTests
{
    /// <summary>
    /// Every backing type <see cref="ResourceTypeMapper"/> can emit must have a connection schema.
    /// </summary>
    /// <remarks>
    /// Without this, adding a resource mapping — or dropping a <c>LegacyFallbackType</c> so a
    /// <c>Radius.*</c> UDT becomes the emitted type — would leave the new type with no schema. The
    /// publisher would then wire no credential at all and every consumer would silently receive the
    /// password Aspire generated for local run mode, which is precisely the reported defect. Failing
    /// here makes that a compile-and-test-time decision rather than a deploy-time surprise.
    /// </remarks>
    [Fact]
    public void EveryEmittedBackingType_HasAConnectionSchema()
    {
        var missing = ResourceTypeMapper.GetEmittedBackingTypes()
            .Where(t => RadiusBackingConnections.GetSchema(t.EmittedType) is null)
            .Select(t => $"{t.MappingKey} -> {t.EmittedType}")
            .OrderBy(t => t, StringComparer.Ordinal)
            .ToList();

        Assert.Empty(missing);
    }

    /// <summary>
    /// The schema table must not describe a type the mapper never emits: a stale row is a claim
    /// about a schema nobody verifies, and it silences the guard above if that type is ever mapped.
    /// </summary>
    [Fact]
    public void ConnectionSchemaTable_DescribesOnlyEmittedBackingTypes()
    {
        var emitted = ResourceTypeMapper.GetEmittedBackingTypes()
            .Select(t => t.EmittedType)
            .ToHashSet(StringComparer.Ordinal);

        // Dapr's UDT names are listed alongside their legacy names so the table stays total if the
        // legacy fallback is dropped; both spellings are explicitly "not projected", so neither can
        // regress a connection string.
        var allowedNonEmitted = new HashSet<string>(StringComparer.Ordinal)
        {
            RadiusResourceTypes.DaprStateStores,
            RadiusResourceTypes.DaprPubSubBrokers,
        };

        var stale = RadiusBackingConnections.KnownTypes
            .Where(t => !emitted.Contains(t) && !allowedNonEmitted.Contains(t))
            .OrderBy(t => t, StringComparer.Ordinal)
            .ToList();

        Assert.Empty(stale);
    }

    /// <summary>
    /// The guard applies to every entry point, so a Kubernetes, Azure Container Apps, or Azure App
    /// Service consumer that reaches a Radius-owned backing resource through
    /// <c>ComputeEnvironmentEndpointResolver</c> gets the same accurate failure the Radius publisher
    /// does.
    /// </summary>
    /// <remarks>
    /// The resolver only delegates to a Radius environment for resources <em>that environment owns</em>,
    /// so this can never be a false positive: the address really is underivable. Suppressing it
    /// would substitute a container-shaped <c>{name}-{name}.{ns}.svc.cluster.local</c> FQDN that
    /// resolves to nothing, reintroducing the reported defect in a different publisher.
    /// </remarks>
    [Theory]
    [InlineData(EndpointProperty.Host)]
    [InlineData(EndpointProperty.Port)]
    [InlineData(EndpointProperty.Url)]
    [InlineData(EndpointProperty.HostAndPort)]
    [InlineData(EndpointProperty.TargetPort)]
    public void CrossEnvironmentEndpointResolution_FailsWithThePublicException(EndpointProperty property)
    {
        var (environment, cache) = CreateRadiusOwnedRedis();
        var endpoint = cache.GetEndpoint("tcp");

        var ex = Assert.Throws<RadiusBackingResourceEndpointException>(
            () => ((IComputeEnvironmentResource)environment).GetEndpointPropertyExpression(endpoint.Property(property)));

        Assert.Same(cache.Resource, ex.Resource);
        Assert.Contains("provisioned by a Radius recipe", ex.Message, StringComparison.Ordinal);
        Assert.Contains("ASPIRERADIUS060", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The public <see cref="RadiusEnvironmentResource.GetHostAddressExpression"/> — which
    /// <c>Aspire.Hosting.Azure.FrontDoor</c> calls directly — must fail the same way, so behaviour
    /// does not depend on which entry point the caller happened to use.
    /// </summary>
    [Fact]
    public void GetHostAddressExpression_FailsIdenticallyToTheInterfacePath()
    {
        var (environment, cache) = CreateRadiusOwnedRedis();

        var direct = Assert.Throws<RadiusBackingResourceEndpointException>(
            () => environment.GetHostAddressExpression(cache.GetEndpoint("tcp")));
        var viaInterface = Assert.Throws<RadiusBackingResourceEndpointException>(
            () => ((IComputeEnvironmentResource)environment)
                .GetEndpointPropertyExpression(cache.GetEndpoint("tcp").Property(EndpointProperty.Host)));

        Assert.Equal(viaInterface.Message, direct.Message);
    }

    /// <summary>
    /// A database child is represented by its parent in the Radius model, so referencing the child's
    /// endpoint must be classified the same way as referencing the server's.
    /// </summary>
    [Fact]
    public void ChildResourceEndpoint_IsClassifiedByItsParent()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var environment = builder.AddRadiusEnvironment("myenv").Resource;
        var pg = builder.AddPostgres("pg");
        pg.AddDatabase("pgdb");

        var ex = Assert.Throws<RadiusBackingResourceEndpointException>(
            () => environment.GetHostAddressExpression(pg.GetEndpoint("tcp")));

        Assert.Equal("pg", ex.Resource.Name);
    }

    /// <summary>
    /// A container workload is still addressed through the Radius container recipe's ClusterIP
    /// Service, so the guard must not widen to compute resources.
    /// </summary>
    [Fact]
    public void ContainerEndpoint_IsStillResolvedThroughServiceDiscovery()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var environment = builder.AddRadiusEnvironment("myenv").Resource;
        var backend = builder.AddContainer("backend", "myapp/backend", "latest").WithHttpEndpoint(targetPort: 8080);

        var expression = environment.GetHostAddressExpression(backend.GetEndpoint("http"));

        Assert.Equal("backend-backend.default.svc.cluster.local", expression.Format);
    }

    private static (RadiusEnvironmentResource Environment, IResourceBuilder<IResourceWithEndpoints> Cache) CreateRadiusOwnedRedis()
    {
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var environment = builder.AddRadiusEnvironment("myenv").Resource;
        var cache = builder.AddRedis("cache");

        // Build so endpoint annotations are materialized; the builder is intentionally not disposed
        // here because the returned resources outlive it and hold no disposable state.
        _ = builder.Build().Services.GetRequiredService<DistributedApplicationModel>();

        return (environment, cache);
    }
}
