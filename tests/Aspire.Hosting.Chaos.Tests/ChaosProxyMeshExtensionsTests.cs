// <copyright file="ChaosProxyMeshExtensionsTests.cs" company="Microsoft Corporation">
// Copyright (c) Microsoft Corporation. All rights reserved.
// </copyright>

using System.Diagnostics.CodeAnalysis;
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Chaos;

namespace Aspire.Hosting.Chaos.UnitTests;

[SuppressMessage("AspireExperimental", "ASPIRECHAOS001", Justification = "test")]
public class ChaosProxyMeshExtensionsTests
{
    private static IDistributedApplicationBuilder CreateBuilder()
    {
        return DistributedApplication.CreateBuilder(new DistributedApplicationOptions
        {
            DisableDashboard = true,
            AssemblyName = typeof(ChaosProxyMeshExtensionsTests).Assembly.GetName().Name,
        });
    }

    private static IResourceBuilder<ContainerResource> AddFakeService(IDistributedApplicationBuilder builder, string name)
    {
        // Real Aspire resource with an http endpoint - that's all the mesh code checks
        // for ('IResourceWithEndpoints' + 'http' endpoint).
        return builder.AddContainer(name, "fake-image")
            .WithHttpEndpoint(targetPort: 8080, name: "http");
    }

    [Fact]
    public void AddChaosProxyMesh_NoExistingReferences_AddsNoMeshProxies()
    {
        var builder = CreateBuilder();
        AddFakeService(builder, "client");
        AddFakeService(builder, "target");

        builder.AddChaosProxyMesh();

        Assert.DoesNotContain(builder.Resources, r => r.Name.StartsWith("mesh-", StringComparison.Ordinal));
    }

    [Fact]
    public void AddChaosProxyMesh_OneReferenceEdge_AddsOneMeshProxy()
    {
        var builder = CreateBuilder();
        var target = AddFakeService(builder, "target");
        AddFakeService(builder, "client").WithReference(target.GetEndpoint("http"));

        builder.AddChaosProxyMesh();

        var meshProxies = builder.Resources.Where(r => r.Name.StartsWith("mesh-", StringComparison.Ordinal)).ToList();
        Assert.Single(meshProxies);
        Assert.Equal("mesh-client-to-target", meshProxies[0].Name);
        Assert.IsAssignableFrom<ChaosProxyResource>(meshProxies[0]);
    }

    [Fact]
    public void AddChaosProxyMesh_MultipleEdges_AddsOneMeshProxyPerEdge()
    {
        var builder = CreateBuilder();
        var a = AddFakeService(builder, "service-a");
        var b = AddFakeService(builder, "service-b");
        var c = AddFakeService(builder, "service-c");

        // frontend -> a, frontend -> b, c -> a
        AddFakeService(builder, "frontend")
            .WithReference(a.GetEndpoint("http"))
            .WithReference(b.GetEndpoint("http"));
        c.WithReference(a.GetEndpoint("http"));

        builder.AddChaosProxyMesh();

        var meshNames = builder.Resources
            .Where(r => r.Name.StartsWith("mesh-", StringComparison.Ordinal))
            .Select(r => r.Name)
            .OrderBy(n => n)
            .ToList();

        Assert.Equal(
            new[]
            {
                "mesh-frontend-to-service-a",
                "mesh-frontend-to-service-b",
                "mesh-service-c-to-service-a",
            },
            meshNames);
    }

    [Fact]
    public void AddChaosProxyMesh_Idempotent_CallingTwiceIsSafe()
    {
        var builder = CreateBuilder();
        var target = AddFakeService(builder, "target");
        AddFakeService(builder, "client").WithReference(target.GetEndpoint("http"));

        builder.AddChaosProxyMesh();
        builder.AddChaosProxyMesh();

        var meshProxies = builder.Resources.Where(r => r.Name.StartsWith("mesh-", StringComparison.Ordinal)).ToList();
        Assert.Single(meshProxies);
    }

    [Fact]
    public void AddChaosProxyMesh_ExcludeEdge_ExcludesMatchingEdges()
    {
        var builder = CreateBuilder();
        var a = AddFakeService(builder, "service-a");
        var b = AddFakeService(builder, "service-b");
        AddFakeService(builder, "frontend")
            .WithReference(a.GetEndpoint("http"))
            .WithReference(b.GetEndpoint("http"));

        // Exclude edges pointing at service-b (attribute/type-style predicate, not a name allowlist).
        builder.AddChaosProxyMesh(excludeEdge: (_, target) => target.Name == "service-b");

        var meshNames = builder.Resources
            .Where(r => r.Name.StartsWith("mesh-", StringComparison.Ordinal))
            .Select(r => r.Name)
            .ToList();

        Assert.Single(meshNames);
        Assert.Equal("mesh-frontend-to-service-a", meshNames[0]);
    }

    [Fact]
    public void AddChaosProxyMesh_DoesNotMeshChaosProxyTargets()
    {
        // Authors who wire AddChaosProxy + WithTarget manually then call AddChaosProxyMesh
        // shouldn't end up with chaos proxies meshing chaos proxies - infinite regress.
        var builder = CreateBuilder();
        var target = AddFakeService(builder, "real-target");
        var manualProxy = builder.AddChaosProxy("manual-proxy");
        manualProxy.WithTarget(target);
        AddFakeService(builder, "client").WithReference(manualProxy.GetEndpoint("http"));

        builder.AddChaosProxyMesh();

        // No mesh proxy should be added for the client->manual-proxy edge.
        var meshNames = builder.Resources
            .Where(r => r.Name.StartsWith("mesh-", StringComparison.Ordinal))
            .Select(r => r.Name)
            .ToList();

        Assert.DoesNotContain(meshNames, name => name.EndsWith("-to-manual-proxy", StringComparison.Ordinal));
    }
}
