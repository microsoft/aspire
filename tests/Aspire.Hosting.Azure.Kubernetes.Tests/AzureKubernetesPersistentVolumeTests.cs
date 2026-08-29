// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREAZURE003, ASPIRECOMPUTE002

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Azure.Kubernetes;
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

        // Dependency discovery can request this template before the AKS prepare step adds FICs.
        _ = aks.Resource.GetBicepTemplateString();

        var app = builder.Build();
        app.Run();

        var volumePath = Path.Combine(workspace.Path, "templates", "media-volume", "pv.yaml");
        var claimPath = Path.Combine(workspace.Path, "templates", "media-volume", "media-volume.yaml");
        var statefulSetPath = Path.Combine(workspace.Path, "templates", "app", "statefulset.yaml");
        var serviceAccountPath = Path.Combine(workspace.Path, "templates", "app", "sa.yaml");
        var valuesPath = Path.Combine(workspace.Path, "values.yaml");

        Assert.True(File.Exists(volumePath), $"Expected persistent volume YAML at {volumePath}.");
        Assert.True(File.Exists(claimPath), $"Expected persistent volume claim YAML at {claimPath}.");
        Assert.True(File.Exists(statefulSetPath), $"Expected workload YAML at {statefulSetPath}.");
        Assert.True(File.Exists(serviceAccountPath), $"Expected service account YAML at {serviceAccountPath}.");
        Assert.True(File.Exists(valuesPath), $"Expected Helm values YAML at {valuesPath}.");
        Assert.True(volume.Resource.TryGetLastAnnotation<AzureFileSharePersistentVolumeAnnotation>(out var azureFiles));
        Assert.Equal(TimeSpan.FromMinutes(15), aks.Resource.KubernetesEnvironment.HelmDeploymentTimeout);
        Assert.Contains(azureFiles.Identity, aks.Resource.References);

        var identityManifest = await AzureManifestUtils.GetManifestWithBicep(azureFiles.Identity);
        var storageManifest = await AzureManifestUtils.GetManifestWithBicep(storage.Resource);
        var aksManifest = await AzureManifestUtils.GetManifestWithBicep(aks.Resource);

        await Verify(await File.ReadAllTextAsync(volumePath), "yaml")
            .AppendContentAsFile(await File.ReadAllTextAsync(claimPath), "yaml")
            .AppendContentAsFile(await File.ReadAllTextAsync(statefulSetPath), "yaml")
            .AppendContentAsFile(await File.ReadAllTextAsync(serviceAccountPath), "yaml")
            .AppendContentAsFile(await File.ReadAllTextAsync(valuesPath), "yaml")
            .AppendContentAsFile(identityManifest.BicepText, "bicep")
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
    public async Task WithAzureFileShare_MultipleVolumesUseSeparateIdentitiesAndShareScopedRoles()
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

        Assert.Equal(2, builder.Resources.OfType<AzureUserAssignedIdentityResource>().Count());
        Assert.Equal(2, CountOccurrences(storageManifest.BicepText, "resource azureFilesRole_"));
        Assert.Equal(2, CountOccurrences(storageManifest.BicepText, "scope: storage"));
        Assert.DoesNotContain("aksKubeletPrincipalId", storageManifest.BicepText);
    }

    [Fact]
    public async Task WithAzureFileShare_SharedVolumeFederatesEveryConsumer()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, workspace.Path);
        var aks = builder.AddAzureKubernetesEnvironment("aks");
        var share = builder.AddAzureStorage("storage")
            .AddFiles("files")
            .AddFileShare("shared-files", "shared");
        var volume = aks.AddPersistentVolume("shared-volume")
            .WithAzureFileShare(share);

        builder.AddContainer("first", "nginx")
            .WithPersistentVolume(volume, "/srv/shared");
        builder.AddContainer("second", "nginx")
            .WithPersistentVolume(volume, "/srv/shared");

        var app = builder.Build();
        app.Run();

        var aksManifest = await AzureManifestUtils.GetManifestWithBicep(aks.Resource);
        Assert.Contains("resource fedcred_first__shared_volume ", aksManifest.BicepText);
        Assert.Contains("resource fedcred_second__shared_volume ", aksManifest.BicepText);
        Assert.Contains("subject: 'system:serviceaccount:default:first-sa'", aksManifest.BicepText);
        Assert.Contains("subject: 'system:serviceaccount:default:second-sa'", aksManifest.BicepText);
        Assert.Equal(1, CountOccurrences(aksManifest.BicepText, "dependsOn:"));

        var firstServiceAccount = await File.ReadAllTextAsync(Path.Combine(workspace.Path, "templates", "first", "sa.yaml"));
        var secondServiceAccount = await File.ReadAllTextAsync(Path.Combine(workspace.Path, "templates", "second", "sa.yaml"));
        Assert.Contains("name: \"first-sa\"", firstServiceAccount);
        Assert.Contains("name: \"second-sa\"", secondServiceAccount);
    }

    [Fact]
    public async Task WithAzureFileShare_UsesApplicationServiceAccountForVolumeFederation()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, workspace.Path);
        var aks = builder.AddAzureKubernetesEnvironment("aks");
        var appIdentity = builder.AddAzureUserAssignedIdentity("app-identity");
        var share = builder.AddAzureStorage("storage")
            .AddFiles("files")
            .AddFileShare("shared-files", "shared");
        var volume = aks.AddPersistentVolume("shared-volume")
            .WithAzureFileShare(share);

        builder.AddContainer("orders-api", "nginx")
            .WithAzureUserAssignedIdentity(appIdentity)
            .WithPersistentVolume(volume, "/srv/shared");

        var app = builder.Build();
        app.Run();

        var aksManifest = await AzureManifestUtils.GetManifestWithBicep(aks.Resource);
        Assert.Contains("resource fedcred_orders_api ", aksManifest.BicepText);
        Assert.Contains("resource fedcred_orders_api__shared_volume ", aksManifest.BicepText);
        Assert.Equal(2, CountOccurrences(aksManifest.BicepText, "subject: 'system:serviceaccount:default:orders-api-sa'"));

        var serviceAccount = await File.ReadAllTextAsync(Path.Combine(workspace.Path, "templates", "orders-api", "sa.yaml"));
        Assert.Contains("azure.workload.identity/client-id", serviceAccount);
        Assert.Contains(".Values.parameters.orders_api.identityClientId", serviceAccount);
        Assert.Equal(1, CountOccurrences(serviceAccount, "kind: \"ServiceAccount\""));

        var values = await File.ReadAllTextAsync(Path.Combine(workspace.Path, "values.yaml"));
        Assert.Contains("orders_api:", values);
    }

    [Fact]
    public async Task WithAzureFileShare_NormalizesServiceAccountName()
    {
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var aks = builder.AddAzureKubernetesEnvironment("aks");
        var share = builder.AddAzureStorage("storage")
            .AddFiles("files")
            .AddFileShare("shared-files", "shared");
        var volume = aks.AddPersistentVolume("SharedVolume")
            .WithAzureFileShare(share);

        builder.AddContainer("Api", "nginx")
            .WithPersistentVolume(volume, "/srv/shared");

        var app = builder.Build();
        app.Run();

        var aksManifest = await AzureManifestUtils.GetManifestWithBicep(aks.Resource);
        Assert.Contains("subject: 'system:serviceaccount:default:api-sa'", aksManifest.BicepText);
    }

    [Fact]
    public void WithAzureFileShare_DerivesValidIdentityNameFromMaximumLengthVolumeName()
    {
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var aks = builder.AddAzureKubernetesEnvironment("aks");
        var share = builder.AddAzureStorage("storage")
            .AddFiles("files")
            .AddFileShare("shared-files", "shared");
        var volumeName = $"v{new string('a', 63)}";

        var volume = aks.AddPersistentVolume(volumeName)
            .WithAzureFileShare(share);

        Assert.True(volume.Resource.TryGetLastAnnotation<AzureFileSharePersistentVolumeAnnotation>(out var azureFiles));
        Assert.Equal(64, azureFiles.Identity.Name.Length);
        Assert.EndsWith("-identity", azureFiles.Identity.Name);
    }

    [Fact]
    public void WithAzureFileShare_DerivedIdentityNameDoesNotContainConsecutiveHyphens()
    {
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var aks = builder.AddAzureKubernetesEnvironment("aks");
        var share = builder.AddAzureStorage("storage")
            .AddFiles("files")
            .AddFileShare("shared-files", "shared");
        var volumeName = $"{new string('a', 45)}-{new string('b', 18)}";

        var volume = aks.AddPersistentVolume(volumeName)
            .WithAzureFileShare(share);

        Assert.True(volume.Resource.TryGetLastAnnotation<AzureFileSharePersistentVolumeAnnotation>(out var azureFiles));
        Assert.DoesNotContain("--", azureFiles.Identity.Name);
    }

    [Fact]
    public async Task WithAzureFileShare_RepeatedCallReusesVolumeIdentity()
    {
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var aks = builder.AddAzureKubernetesEnvironment("aks");
        var storage = builder.AddAzureStorage("storage");
        var share = storage.AddFiles("files").AddFileShare("shared-files", "shared");
        var volume = aks.AddPersistentVolume("shared-volume")
            .WithAzureFileShare(share)
            .WithAzureFileShare(share);

        Assert.True(volume.Resource.TryGetLastAnnotation<AzureFileSharePersistentVolumeAnnotation>(out var azureFiles));
        Assert.Single(builder.Resources.OfType<AzureUserAssignedIdentityResource>());

        var storageManifest = await AzureManifestUtils.GetManifestWithBicep(storage.Resource);
        Assert.Equal(1, CountOccurrences(storageManifest.BicepText, "resource azureFilesRole_shared_volume "));
        Assert.Equal("shared-volume-identity", azureFiles.Identity.Name);
    }

    [Fact]
    public async Task WithAzureFileShare_NormalizesLegacyServiceAccountAlias()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, workspace.Path);
        var aks = builder.AddAzureKubernetesEnvironment("aks");
        var share = builder.AddAzureStorage("storage")
            .AddFiles("files")
            .AddFileShare("shared-files", "shared");
        var volume = aks.AddPersistentVolume("shared-volume")
            .WithAzureFileShare(share);

        builder.AddContainer("app", "nginx")
            .PublishAsKubernetesService(resource => resource.Workload!.PodTemplate.Spec.ServiceAccount = "app-sa")
            .WithPersistentVolume(volume, "/srv/shared");

        var app = builder.Build();
        app.Run();

        var statefulSet = await File.ReadAllTextAsync(Path.Combine(workspace.Path, "templates", "app", "statefulset.yaml"));
        Assert.Contains("serviceAccountName: \"app-sa\"", statefulSet);
        Assert.DoesNotContain("serviceAccount: \"", statefulSet);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void WithAzureFileShare_CoScopesGeneratedIdentityWithExistingAks(bool configureExistingFirst)
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish, workspace.Path);
        var aksName = builder.AddParameter("aks-name");
        var aksResourceGroup = builder.AddParameter("aks-resource-group");
        var aks = builder.AddAzureKubernetesEnvironment("aks");
        if (configureExistingFirst)
        {
            aks.AsExisting(aksName, aksResourceGroup);
        }
        var share = builder.AddAzureStorage("storage")
            .AddFiles("files")
            .AddFileShare("shared-files", "shared");

        var volume = aks.AddPersistentVolume("shared-volume")
            .WithAzureFileShare(share);
        if (!configureExistingFirst)
        {
            aks.AsExisting(aksName, aksResourceGroup);
        }

        builder.AddContainer("app", "nginx")
            .WithPersistentVolume(volume, "/srv/shared");

        var app = builder.Build();
        app.Run();

        Assert.True(volume.Resource.TryGetLastAnnotation<AzureFileSharePersistentVolumeAnnotation>(out var azureFiles));
        Assert.NotNull(azureFiles.Identity.Scope);
        Assert.Same(aksResourceGroup.Resource, azureFiles.Identity.Scope.ResourceGroup);
    }

    [Fact]
    public void WithAzureFileShare_RejectsParameterizedKubernetesNamespace()
    {
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var namespaceParameter = builder.AddParameter("kubernetes-namespace");
        var aks = builder.AddAzureKubernetesEnvironment("aks");
        builder.CreateResourceBuilder(aks.Resource.KubernetesEnvironment)
            .WithHelm(options => options.WithNamespace(namespaceParameter));
        var identity = builder.AddAzureUserAssignedIdentity("volume-identity");
        aks.Resource.WorkloadIdentities[new AksWorkloadIdentityBindingKey("app", "volume")] =
            new AksWorkloadIdentityBinding("app-sa", "app-fedcred", identity.Resource);

        var exception = Assert.Throws<InvalidOperationException>(aks.Resource.GetBicepTemplateString);
        Assert.Contains("requires a literal Kubernetes namespace", exception.Message);
    }

    [Fact]
    public void AksWithoutWorkloadIdentity_AllowsParameterizedKubernetesNamespace()
    {
        var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var namespaceParameter = builder.AddParameter("kubernetes-namespace");
        var aks = builder.AddAzureKubernetesEnvironment("aks");
        builder.CreateResourceBuilder(aks.Resource.KubernetesEnvironment)
            .WithHelm(options => options.WithNamespace(namespaceParameter));

        var bicep = aks.Resource.GetBicepTemplateString();

        Assert.DoesNotContain("federatedIdentityCredentials", bicep);
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
        Assert.Contains("resource azureFilesRole_media_volume 'Microsoft.Authorization/roleAssignments@2022-04-01'", storageManifest.BicepText);
        Assert.Contains("scope: storage", storageManifest.BicepText);
        Assert.DoesNotContain("aksKubeletPrincipalId", storageManifest.BicepText);
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
