// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREAZURE003, ASPIRECOMPUTE002

using Aspire.Hosting.Utils;

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
}
