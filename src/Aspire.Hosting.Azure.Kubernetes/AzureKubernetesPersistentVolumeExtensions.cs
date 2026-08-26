// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics.CodeAnalysis;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Azure;
using Aspire.Hosting.Azure.Kubernetes;
using Aspire.Hosting.Kubernetes;
using Aspire.Hosting.Kubernetes.Annotations;
using Azure.Provisioning;
using Azure.Provisioning.Authorization;
using Azure.Provisioning.Expressions;
using Azure.Provisioning.Storage;

namespace Aspire.Hosting;

/// <summary>
/// Provides extension methods for adding Kubernetes persistent volumes to an
/// <see cref="AzureKubernetesEnvironmentResource"/>.
/// </summary>
[Experimental("ASPIRECOMPUTE002", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
public static class AzureKubernetesPersistentVolumeExtensions
{
    private const string AzureFileCsiDriver = "file.csi.azure.com";
    private const string StorageFileDataSmbMiAdminRoleId = "a235d3ee-5935-4cfb-8cc5-a3303ad5995e";

    /// <summary>
    /// Adds a Kubernetes PersistentVolumeClaim resource to the application model for the
    /// specified AKS environment.
    /// </summary>
    /// <ats-summary>Adds a Kubernetes PersistentVolumeClaim resource to an AKS environment</ats-summary>
    /// <param name="builder">The AKS environment resource builder.</param>
    /// <param name="name">The name of the persistent volume resource.</param>
    /// <returns>A builder for the new <see cref="KubernetesPersistentVolumeResource"/>.</returns>
    /// <ats-returns>The resource builder.</ats-returns>
    /// <remarks>
    /// <para>
    /// The persistent volume is associated with the AKS environment's underlying Kubernetes
    /// environment and generates a <c>v1.PersistentVolumeClaim</c> in the Helm chart output.
    /// </para>
    /// <para>
    /// When no storage class is configured, the generated claim omits
    /// <c>spec.storageClassName</c> so the cluster's default storage class is used. A standard
    /// AKS cluster dynamically provisions an Azure managed disk for such claims. Use
    /// <see cref="KubernetesPersistentVolumeExtensions.WithStorageClass(IResourceBuilder{KubernetesPersistentVolumeResource}, string)"/>
    /// to select a different storage class explicitly.
    /// </para>
    /// </remarks>
    /// <ats-remarks />
    /// <example>
    /// <code>
    /// var aks = builder.AddAzureKubernetesEnvironment("aks");
    ///
    /// var data = aks.AddPersistentVolume("data")
    ///     .WithCapacity("20Gi");
    ///
    /// builder.AddProject&lt;Projects.Api&gt;("api")
    ///     .WithPersistentVolume(data, "/data");
    /// </code>
    /// </example>
    [AspireExport]
    public static IResourceBuilder<KubernetesPersistentVolumeResource> AddPersistentVolume(
        this IResourceBuilder<AzureKubernetesEnvironmentResource> builder,
        [ResourceName] string name)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(name);

        var k8sEnvBuilder = builder.ApplicationBuilder.CreateResourceBuilder(builder.Resource.KubernetesEnvironment);
        return k8sEnvBuilder.AddPersistentVolume(name);
    }

    /// <summary>
    /// Configures an AKS persistent volume to use an existing Azure file share with
    /// managed identity authentication.
    /// </summary>
    /// <ats-summary>Backs an AKS persistent volume with an Azure file share</ats-summary>
    /// <param name="builder">The persistent volume resource builder.</param>
    /// <param name="share">The Azure file share resource builder.</param>
    /// <returns>The same persistent volume builder for chaining.</returns>
    /// <remarks>
    /// <para>
    /// The generated Kubernetes persistent volume uses the Azure Files CSI driver and
    /// authenticates with the AKS kubelet managed identity. No Kubernetes Secret or
    /// storage account key is generated.
    /// </para>
    /// <para>
    /// The AKS cluster must use Kubernetes 1.34 or later on Linux nodes. Aspire grants
    /// the kubelet identity the <c>Storage File Data SMB MI Admin</c> role on the storage
    /// account. Storage accounts provisioned by Aspire enable SMB OAuth and disable shared
    /// key authentication when Azure Files is added. Existing storage accounts must already
    /// have those settings configured.
    /// </para>
    /// </remarks>
    /// <example>
    /// <code>
    /// var files = builder.AddAzureStorage("storage").AddFiles("files");
    /// var share = files.AddFileShare("media-share", "media");
    ///
    /// var volume = aks.AddPersistentVolume("media-volume")
    ///     .WithAzureFileShare(share)
    ///     .WithCapacity("100Gi");
    /// </code>
    /// </example>
    [AspireExport]
    public static IResourceBuilder<KubernetesPersistentVolumeResource> WithAzureFileShare(
        this IResourceBuilder<KubernetesPersistentVolumeResource> builder,
        IResourceBuilder<AzureFileStorageShareResource> share)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(share);

        if (!ReferenceEquals(builder.ApplicationBuilder, share.ApplicationBuilder))
        {
            throw new ArgumentException("The persistent volume and Azure file share must belong to the same distributed application.", nameof(share));
        }

        if (builder.Resource.Parent.OwningComputeEnvironment is not AzureKubernetesEnvironmentResource aks)
        {
            throw new InvalidOperationException(
                $"Persistent volume '{builder.Resource.Name}' must belong to an Azure Kubernetes Service environment before it can use an Azure file share.");
        }

        var storage = share.Resource.Parent.Parent;
        if (aks.AzureFileStorageAccounts.TryAdd(storage.Name, storage))
        {
            ConfigureKubeletIdentityRoleAssignment(
                share.ApplicationBuilder.CreateResourceBuilder(storage),
                aks);
        }

        var volumeHandle = ReferenceExpression.Create(
            $"{storage.ResourceGroupName}#{storage.NameOutputReference}#{share.Resource.FileShareName}");
        var volumeAttributes = new Dictionary<string, ReferenceExpression>(StringComparer.Ordinal)
        {
            ["resourceGroup"] = ReferenceExpression.Create($"{storage.ResourceGroupName}"),
            ["storageAccount"] = ReferenceExpression.Create($"{storage.NameOutputReference}"),
            ["shareName"] = ReferenceExpression.Create($"{share.Resource.FileShareName}"),
            ["protocol"] = ReferenceExpression.Create($"smb"),
            ["mountWithManagedIdentity"] = ReferenceExpression.Create($"true"),
        };

        builder.WithAnnotation(
            new KubernetesCsiPersistentVolumeSourceAnnotation(
                driver: AzureFileCsiDriver,
                volumeHandle,
                volumeAttributes,
                mountOptions:
                [
                    "dir_mode=0777",
                    "file_mode=0777",
                    "uid=0",
                    "gid=0",
                    "mfsymlinks",
                    "cache=strict",
                    "nosharesock",
                    "actimeo=30",
                    "nobrl",
                ],
                defaultStorageClassName: "azurefile-csi",
                defaultAccessMode: PersistentVolumeAccessMode.ReadWriteMany),
            ResourceAnnotationMutationBehavior.Replace);

        return builder;
    }

    private static void ConfigureKubeletIdentityRoleAssignment(
        IResourceBuilder<AzureStorageResource> storage,
        AzureKubernetesEnvironmentResource aks)
    {
        var normalizedAksName = Infrastructure.NormalizeBicepIdentifier(aks.Name);
        var principalIdParameterName = $"aksKubeletPrincipalId_{normalizedAksName}";
        storage.Resource.Parameters[principalIdParameterName] = aks.KubeletIdentityObjectId;

        storage.ConfigureInfrastructure(infrastructure =>
        {
            var storageAccount = infrastructure.GetProvisionableResources().OfType<StorageAccount>().Single();
            var principalId = new ProvisioningParameter(principalIdParameterName, typeof(string));
            infrastructure.Add(principalId);

            var roleDefinitionId = BicepFunction.GetSubscriptionResourceId(
                "Microsoft.Authorization/roleDefinitions",
                StorageFileDataSmbMiAdminRoleId);
            var roleAssignment = new RoleAssignment($"aksFilesRole_{normalizedAksName}")
            {
                Name = BicepFunction.CreateGuid(storageAccount.Id, principalId, roleDefinitionId),
                Scope = new IdentifierExpression(storageAccount.BicepIdentifier),
                RoleDefinitionId = roleDefinitionId,
                PrincipalId = principalId,
                PrincipalType = RoleManagementPrincipalType.ServicePrincipal,
            };
            infrastructure.Add(roleAssignment);
        });
    }
}
