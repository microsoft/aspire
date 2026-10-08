// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using Aspire.Hosting.ApplicationModel;
using Azure.Provisioning;
using ProvisioningFileShare = Azure.Provisioning.Storage.FileShare;

namespace Aspire.Hosting.Azure;

/// <summary>
/// Represents an Azure file share.
/// </summary>
/// <param name="name">The name of the resource.</param>
/// <param name="fileShareName">The name of the Azure file share.</param>
/// <param name="parent">The Azure Files service that contains the share.</param>
[DebuggerDisplay("Type = {GetType().Name,nq}, Name = {Name}, FileShare = {FileShareName}")]
public class AzureFileStorageShareResource(string name, string fileShareName, AzureFileStorageResource parent) : Resource(name),
    IResourceWithConnectionString,
    IResourceWithParent<AzureFileStorageResource>
{
    /// <summary>
    /// Gets the name of the Azure file share.
    /// </summary>
    public string FileShareName { get; } = ThrowIfNullOrEmpty(fileShareName);

    /// <summary>
    /// Gets the connection string expression for the Azure file share.
    /// </summary>
    public ReferenceExpression ConnectionStringExpression => Parent.GetConnectionString(FileShareName);

    /// <summary>
    /// Gets the parent Azure Files service.
    /// </summary>
    public AzureFileStorageResource Parent => parent ?? throw new ArgumentNullException(nameof(parent));

    internal ProvisioningFileShare ToProvisioningEntity(bool isExisting)
    {
        var identifier = Infrastructure.NormalizeBicepIdentifier(Name);
        var fileShare = isExisting
            ? ProvisioningFileShare.FromExisting(identifier)
            : new ProvisioningFileShare(identifier);
        fileShare.Name = FileShareName;

        return fileShare;
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

        yield return new("FileShareName", ReferenceExpression.Create($"{FileShareName}"));
    }
}
