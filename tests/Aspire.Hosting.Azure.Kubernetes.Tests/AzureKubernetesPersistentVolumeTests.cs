// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREAZURE003, ASPIRECOMPUTE002

using Aspire.Hosting.Utils;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Kubernetes;
using Aspire.Hosting.Tests.Utils;
using Microsoft.Extensions.DependencyInjection;

namespace Aspire.Hosting.Azure.Tests;

public class AzureKubernetesPersistentVolumeTests(ITestOutputHelper outputHelper)
{
    [Fact]
    public void AksAddPersistentVolume_HasCorrectParent()
    {
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var aks = builder.AddAzureKubernetesEnvironment("aks");

        var volume = aks.AddPersistentVolume("data");

        Assert.Same(aks.Resource.KubernetesEnvironment, volume.Resource.Parent);
    }

    [Fact]
    public async Task AksAddPersistentVolume_GeneratesClaimUsingClusterDefaults()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, workspace.Path);

        var aks = builder.AddAzureKubernetesEnvironment("aks");
        aks.AddPersistentVolume("data")
            .WithCapacity("20Gi");

        var app = builder.Build();
        app.Run();

        var claimPath = Path.Combine(workspace.Path, "templates", "data", "data.yaml");
        Assert.True(File.Exists(claimPath), $"Expected persistent volume claim YAML at {claimPath}.");

        var content = await File.ReadAllTextAsync(claimPath);
        await Verify(content, "yaml");
    }

    [Fact]
    public async Task AksPersistentVolumeEnvironmentUsesAspireStoreInRunMode()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Run);
        var aks = builder.AddAzureKubernetesEnvironment("aks");
        var volume = aks.AddPersistentVolume("data");
        var executable = builder.AddExecutable("executable", "test-command", ".")
            .WithPersistentVolume(volume, "/srv/data", env: "DATA_PATH");

        using var app = builder.Build();
        var store = app.Services.GetRequiredService<IAspireStore>();
        var expectedPath = KubernetesPersistentVolumeLocalStorage.GetPath(store, volume.Resource);
        var environment = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(
            executable.Resource,
            serviceProvider: app.Services);

        Assert.Equal(expectedPath, environment["DATA_PATH"]);
        Assert.True(Directory.Exists(expectedPath));
    }

    [Fact]
    public async Task AksPersistentVolume_PublishesWhenWorkloadImplicitlyTargetsSoleEnvironment()
    {
        // AKS is the only compute environment here, so the workload is never explicitly bound with
        // WithComputeEnvironment. EnsureComputeEnvironmentAnnotationsApplied implements the
        // "single compute environment is the default" convention before the before-start pipeline
        // runs, so by the time the publish-mode binding validation executes the workload resolves to
        // the AKS resource rather than null. This mirrors the AKS deployment E2E AppHost, which
        // binds a persistent volume without calling WithComputeEnvironment.
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, workspace.Path);

        var aks = builder.AddAzureKubernetesEnvironment("aks");
        var volume = aks.AddPersistentVolume("data").WithCapacity("5Gi");

        builder.AddContainer("service", "nginx")
            .WithPersistentVolume(volume, "/srv/data", env: "DATA_PATH");

        var app = builder.Build();
        app.Run();

        var claimPath = Path.Combine(workspace.Path, "templates", "data", "data.yaml");
        Assert.True(File.Exists(claimPath), $"Expected persistent volume claim YAML at {claimPath}.");

        // A workload bound to a persistent volume renders as a StatefulSet rather than a Deployment.
        var statefulSetPath = Path.Combine(workspace.Path, "templates", "service", "statefulset.yaml");
        Assert.True(File.Exists(statefulSetPath), $"Expected workload YAML at {statefulSetPath}.");

        var statefulSetContent = await File.ReadAllTextAsync(statefulSetPath);
        Assert.Contains("/srv/data", statefulSetContent, StringComparison.Ordinal);
    }
}
