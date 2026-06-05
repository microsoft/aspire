// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using Aspire.Hosting.ApplicationModel;
using Azure.Provisioning;

namespace Aspire.Hosting.Azure;

/// <summary>
/// A resource that represents an Azure Blob Storage container.
/// </summary>
/// <param name="name">The name of the resource.</param>
/// <param name="blobContainerName">The name of the blob container.</param>
/// <param name="parent">The <see cref="AzureBlobStorageResource"/> that the resource is stored in.</param>
public class AzureBlobStorageContainerResource(string name, string blobContainerName, AzureBlobStorageResource parent) : Resource(name),
    IResourceWithConnectionString,
    IResourceWithParent<AzureBlobStorageResource>
{
    /// <summary>
    /// Gets the blob container name.
    /// </summary>
    public string BlobContainerName { get; } = ThrowIfNullOrEmpty(blobContainerName);

    /// <summary>
    /// Gets the connection string template for the manifest for the Azure Blob Storage container resource.
    /// </summary>
    public ReferenceExpression ConnectionStringExpression => Parent.GetConnectionString(BlobContainerName);

    /// <summary>
    /// Gets the parent <see cref="AzureBlobStorageResource"/> of this <see cref="AzureBlobStorageContainerResource"/>.
    /// </summary>
    public AzureBlobStorageResource Parent => parent ?? throw new ArgumentNullException(nameof(parent));

    /// <summary>
    /// Converts the current instance to a provisioning entity.
    /// </summary>
    /// <returns>A <see cref="global::Azure.Provisioning.Storage.BlobContainer"/> instance.</returns>
    internal global::Azure.Provisioning.Storage.BlobContainer ToProvisioningEntity()
    {
        global::Azure.Provisioning.Storage.BlobContainer blobContainer = new(Infrastructure.NormalizeBicepIdentifier(Name))
        {
            Name = BlobContainerName
        };

        return blobContainer;
    }

    private static string ThrowIfNullOrEmpty([NotNull] string? argument, [CallerArgumentExpression(nameof(argument))] string? paramName = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(argument, paramName);
        return argument;
    }

    IEnumerable<KeyValuePair<string, ReferenceExpression>> IResourceWithConnectionString.GetConnectionProperties()
    {
        foreach (var property in ((IResourceWithConnectionString)Parent).GetConnectionProperties())
        {
            yield return property;
        }

        yield return new("BlobContainerName", ReferenceExpression.Create($"{BlobContainerName}"));
    }
}
