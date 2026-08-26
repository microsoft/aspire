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

    [Fact]
    public async Task WithAzureFileShare_GeneratesManagedIdentityStaticVolume()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, workspace.Path);

        var aks = builder.AddAzureKubernetesEnvironment("aks");
        var storage = builder.AddAzureStorage("storage");
        var files = storage.AddFiles("files");
        var share = files.AddFileShare("media-share", "media");
        var volume = aks.AddPersistentVolume("media-volume")
            .WithAzureFileShare(share)
            .WithCapacity("100Gi");

        builder.AddContainer("app", "nginx")
            .WithPersistentVolume(volume, "/srv/media");

        var app = builder.Build();
        app.Run();

        var volumePath = Path.Combine(workspace.Path, "templates", "media-volume", "pv.yaml");
        var claimPath = Path.Combine(workspace.Path, "templates", "media-volume", "media-volume.yaml");
        var valuesPath = Path.Combine(workspace.Path, "values.yaml");

        Assert.True(File.Exists(volumePath), $"Expected persistent volume YAML at {volumePath}.");
        Assert.True(File.Exists(claimPath), $"Expected persistent volume claim YAML at {claimPath}.");
        Assert.True(File.Exists(valuesPath), $"Expected Helm values YAML at {valuesPath}.");

        var storageManifest = await AzureManifestUtils.GetManifestWithBicep(storage.Resource);
        var aksManifest = await AzureManifestUtils.GetManifestWithBicep(aks.Resource);

        await Verify(await File.ReadAllTextAsync(volumePath), "yaml")
            .AppendContentAsFile(await File.ReadAllTextAsync(claimPath), "yaml")
            .AppendContentAsFile(await File.ReadAllTextAsync(valuesPath), "yaml")
            .AppendContentAsFile(storageManifest.BicepText, "bicep")
            .AppendContentAsFile(aksManifest.BicepText, "bicep");
    }

    [Fact]
    public void WithAzureFileShare_RejectsNonAksPersistentVolume()
    {
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var k8s = builder.AddKubernetesEnvironment("k8s");
        var storage = builder.AddAzureStorage("storage");
        var share = storage.AddFiles("files").AddFileShare("media-share", "media");
        var volume = k8s.AddPersistentVolume("media-volume");

        var exception = Assert.Throws<InvalidOperationException>(() => volume.WithAzureFileShare(share));

        Assert.Contains("must belong to an Azure Kubernetes Service environment", exception.Message);
    }

    [Fact]
    public async Task WithAzureFileShare_MultipleVolumesEmitOneKubeletRoleAssignment()
    {
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var aks = builder.AddAzureKubernetesEnvironment("aks");
        var storage = builder.AddAzureStorage("storage");
        var files = storage.AddFiles("files");

        aks.AddPersistentVolume("media-volume")
            .WithAzureFileShare(files.AddFileShare("media-share", "media"));
        aks.AddPersistentVolume("documents-volume")
            .WithAzureFileShare(files.AddFileShare("documents-share", "documents"));

        var storageManifest = await AzureManifestUtils.GetManifestWithBicep(storage.Resource);

        Assert.Equal(1, CountOccurrences(storageManifest.BicepText, "resource aksFilesRole_aks "));
    }

    [Fact]
    public async Task WithAzureFileShare_ExistingStorageRemainsReadOnly()
    {
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var accountName = builder.AddParameter("storage-account-name");
        var resourceGroup = builder.AddParameter("storage-resource-group");
        var aks = builder.AddAzureKubernetesEnvironment("aks");
        var storage = builder.AddAzureStorage("storage")
            .AsExisting(accountName, resourceGroup);
        var share = storage.AddFiles("files").AddFileShare("media-share", "media");

        aks.AddPersistentVolume("media-volume")
            .WithAzureFileShare(share);

        var storageManifest = await AzureManifestUtils.GetManifestWithBicep(storage.Resource);

        Assert.Contains("resource storage 'Microsoft.Storage/storageAccounts@2025-06-01' existing", storageManifest.BicepText);
        Assert.Contains("resource files 'Microsoft.Storage/storageAccounts/fileServices@2025-06-01' existing", storageManifest.BicepText);
        Assert.Contains("resource media_share 'Microsoft.Storage/storageAccounts/fileServices/shares@2025-06-01' existing", storageManifest.BicepText);
        Assert.Contains("resource aksFilesRole_aks 'Microsoft.Authorization/roleAssignments@2022-04-01'", storageManifest.BicepText);
        Assert.DoesNotContain("azureFilesIdentityBasedAuthentication", storageManifest.BicepText);
        Assert.DoesNotContain("allowSharedKeyAccess", storageManifest.BicepText);
    }

    private static int CountOccurrences(string value, string search)
    {
        var count = 0;
        var index = 0;
        while ((index = value.IndexOf(search, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += search.Length;
        }

        return count;
    }
}
