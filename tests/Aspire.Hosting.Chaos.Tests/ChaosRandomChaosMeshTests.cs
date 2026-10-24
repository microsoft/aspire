// <copyright file="ChaosRandomChaosMeshTests.cs" company="Microsoft Corporation">
// Copyright (c) Microsoft Corporation. All rights reserved.
// </copyright>

using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Chaos;

namespace Aspire.Hosting.Chaos.UnitTests;

/// <summary>
/// Tests <c>WithRandomChaos</c> mesh + per-proxy wiring: auto profile assignment from the
/// edge's stamped resource kind, seeded per-proxy sub-seeds, per-profile intensity, and
/// explicit overrides.
/// </summary>
[SuppressMessage("AspireExperimental", "ASPIRECHAOS001", Justification = "test")]
public class ChaosRandomChaosMeshTests
{
    private static IDistributedApplicationBuilder CreateBuilder()
        => DistributedApplication.CreateBuilder(new DistributedApplicationOptions
        {
            DisableDashboard = true,
            AssemblyName = typeof(ChaosRandomChaosMeshTests).Assembly.GetName().Name,
        });

    private static IResourceBuilder<ProjectResource> AddProjectService(IDistributedApplicationBuilder builder, string name)
        => builder.AddResource(new ProjectResource(name)).WithHttpEndpoint(targetPort: 8080, name: "http");

    private static IResourceBuilder<ContainerResource> AddService(IDistributedApplicationBuilder builder, string name)
        => builder.AddContainer(name, "fake-image").WithHttpEndpoint(targetPort: 8080, name: "http");

    private static IResourceBuilder<FakeInfraResource> AddInfra(IDistributedApplicationBuilder builder, string name, string endpointName)
        => builder.AddResource(new FakeInfraResource(name)).WithEndpoint(targetPort: 9000, name: endpointName, scheme: "https");

    private static string? GetPoliciesJson(IResource resource)
    {
        var dict = new Dictionary<string, object>();
        foreach (var annotation in resource.Annotations.OfType<EnvironmentCallbackAnnotation>())
        {
            var ctx = new EnvironmentCallbackContext(
                new DistributedApplicationExecutionContext(DistributedApplicationOperation.Run),
                resource,
                dict,
                CancellationToken.None);
            annotation.Callback(ctx);
        }

        return dict.TryGetValue("CHAOS_POLICIES_JSON", out var value) ? value?.ToString() : null;
    }

    private static JsonElement? GetRandomFault(IResource proxy)
    {
        var json = GetPoliciesJson(proxy);
        if (json is null)
        {
            return null;
        }

        using var doc = JsonDocument.Parse(json);
        foreach (var policy in doc.RootElement.EnumerateArray())
        {
            if (policy.TryGetProperty("randomFault", out var rf))
            {
                return rf.Clone();
            }
        }

        return null;
    }

    [Fact]
    public void WithRandomChaos_ArmsServiceProfile_OnServiceEdge()
    {
        var builder = CreateBuilder();
        var target = AddProjectService(builder, "target");
        AddProjectService(builder, "client").WithReference(target.GetEndpoint("http"));

        builder.AddChaosProxyMesh().WithRandomChaos(intensity: 0.2, seed: 1234);

        var proxy = builder.Resources.Single(r => r.Name == "mesh-client-to-target");
        var rf = GetRandomFault(proxy);
        Assert.NotNull(rf);
        Assert.Equal("service.http", rf!.Value.GetProperty("profileId").GetString());
        Assert.Equal(0.2, rf.Value.GetProperty("intensity").GetDouble());
    }

    [Fact]
    public void WithRandomChaos_ArmsCosmosProfile_OnCosmosEdge()
    {
        var builder = CreateBuilder();
        var cosmos = AddInfra(builder, "cosmos", "emulator");
        AddService(builder, "client").WithReference(cosmos);

        builder.AddChaosProxyMesh().IncludeInfrastructure().WithRandomChaos(seed: 1);

        var proxy = builder.Resources.Single(r => r.Name == "mesh-client-to-cosmos");
        Assert.Equal("azure.cosmos", GetRandomFault(proxy)!.Value.GetProperty("profileId").GetString());
    }

    [Fact]
    public void WithRandomChaos_ArmsStorageQueueProfile_OnQueueEdge()
    {
        var builder = CreateBuilder();
        var queue = AddInfra(builder, "queue", "queue");
        AddService(builder, "client").WithReference(queue);

        builder.AddChaosProxyMesh().IncludeInfrastructure().WithRandomChaos(seed: 1);

        var proxy = builder.Resources.Single(r => r.Name == "mesh-client-to-queue");
        Assert.Equal("azure.storagequeue", GetRandomFault(proxy)!.Value.GetProperty("profileId").GetString());
    }

    [Fact]
    public void WithRandomChaos_SameGlobalSeed_ProducesStablePerProxySeed()
    {
        int BuildAndGetSeed()
        {
            var builder = CreateBuilder();
            var target = AddProjectService(builder, "target");
            AddProjectService(builder, "client").WithReference(target.GetEndpoint("http"));
            builder.AddChaosProxyMesh().WithRandomChaos(seed: 777);
            var proxy = builder.Resources.Single(r => r.Name == "mesh-client-to-target");
            return GetRandomFault(proxy)!.Value.GetProperty("seed").GetInt32();
        }

        Assert.Equal(BuildAndGetSeed(), BuildAndGetSeed());
    }

    [Fact]
    public void WithRandomChaos_ProfileIntensityOverride_AppliesPerProfile()
    {
        var builder = CreateBuilder();
        var cosmos = AddInfra(builder, "cosmos", "emulator");
        var svc = AddProjectService(builder, "svc");
        AddProjectService(builder, "client")
            .WithReference(svc.GetEndpoint("http"))
            .WithReference(cosmos);

        builder.AddChaosProxyMesh().IncludeInfrastructure().WithRandomChaos(
            intensity: 0.1,
            seed: 5,
            configure: o => o.ProfileIntensity["azure.cosmos"] = 0.5);

        var cosmosProxy = builder.Resources.Single(r => r.Name == "mesh-client-to-cosmos");
        var svcProxy = builder.Resources.Single(r => r.Name == "mesh-client-to-svc");

        Assert.Equal(0.5, GetRandomFault(cosmosProxy)!.Value.GetProperty("intensity").GetDouble());
        Assert.Equal(0.1, GetRandomFault(svcProxy)!.Value.GetProperty("intensity").GetDouble());
    }

    [Fact]
    public void PerProxy_WithRandomChaos_ExplicitProfileOverride_Wins()
    {
        var builder = CreateBuilder();
        var proxy = builder.AddChaosProxy("p").WithRandomChaos(profileId: "azure.keyvault", intensity: 0.3);

        var rf = GetRandomFault(proxy.Resource);
        Assert.NotNull(rf);
        Assert.Equal("azure.keyvault", rf!.Value.GetProperty("profileId").GetString());
        Assert.Equal(0.3, rf.Value.GetProperty("intensity").GetDouble());
    }

    [Fact]
    public void WithRandomChaos_ExcludePaths_DefaultToHealthProbes()
    {
        var builder = CreateBuilder();
        var target = AddProjectService(builder, "target");
        AddProjectService(builder, "client").WithReference(target.GetEndpoint("http"));

        builder.AddChaosProxyMesh().WithRandomChaos(seed: 1);

        var proxy = builder.Resources.Single(r => r.Name == "mesh-client-to-target");
        var excludes = GetRandomFault(proxy)!.Value.GetProperty("excludePaths").EnumerateArray()
            .Select(e => e.GetString()).ToList();
        Assert.Contains("/health", excludes);
    }

    private sealed class FakeInfraResource : Resource, IResourceWithConnectionString, IResourceWithEndpoints
    {
        public FakeInfraResource(string name)
            : base(name)
        {
        }

        public ReferenceExpression ConnectionStringExpression => ReferenceExpression.Create($"UseDevelopmentStorage=true");
    }
}
