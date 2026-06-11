// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using Aspire.Hosting.ApplicationModel;
using Azure.Provisioning;
using Azure.Provisioning.Storage;

namespace Aspire.Hosting.Azure;

/// <summary>
/// A resource that represents an Azure DataLake Storage container.
/// </summary>
/// <param name="name">The name of the resource.</param>
/// <param name="dataLakeFileSystemName">The name of the data lake file system.</param>
/// <param name="parent">The <see cref="AzureDataLakeStorageResource"/> that the resource is stored in.</param>
public class AzureDataLakeStorageFileSystemResource(string name, string dataLakeFileSystemName, AzureDataLakeStorageResource parent) : Resource(name),
    IResourceWithConnectionString,
    IResourceWithParent<AzureDataLakeStorageResource>
{
    /// <summary>
    /// Gets the data lake file system name.
    /// </summary>
    public string DataLakeFileSystemName { get; } = ThrowIfNullOrEmpty(dataLakeFileSystemName);

    /// <summary>
    /// Gets the connection string template for the manifest for the Azure DataLake Storage file system resource.
    /// </summary>
    public ReferenceExpression ConnectionStringExpression => Parent.GetConnectionString(DataLakeFileSystemName);

    /// <summary>
    /// Gets the parent <see cref="AzureDataLakeStorageResource"/> of this <see cref="AzureDataLakeStorageFileSystemResource"/>.
    /// </summary>
    public AzureDataLakeStorageResource Parent => parent ?? throw new ArgumentNullException(nameof(parent));

    /// <summary>
    /// Converts the current instance to a provisioning entity.
    /// </summary>
    /// <returns>A <see cref="BlobContainer"/> instance.</returns>
    internal BlobContainer ToProvisioningEntity()
    {
        BlobContainer blobContainer = new(Infrastructure.NormalizeBicepIdentifier(Name))
        {
            Name = DataLakeFileSystemName
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

        yield return new("DataLakeFileSystemName", ReferenceExpression.Create($"{DataLakeFileSystemName}"));
    }
}
