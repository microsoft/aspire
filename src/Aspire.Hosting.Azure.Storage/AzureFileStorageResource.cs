// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREAZURE003 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.

using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.Azure;

/// <summary>
/// Represents the Azure Files service in an Azure Storage account.
/// </summary>
/// <param name="name">The name of the resource.</param>
/// <param name="storage">The Azure Storage account that contains the Files service.</param>
public class AzureFileStorageResource(string name, AzureStorageResource storage) : Resource(name),
    IResourceWithConnectionString,
    IResourceWithParent<AzureStorageResource>,
    IAzurePrivateEndpointTarget
{
    /// <summary>
    /// Gets the parent Azure Storage account.
    /// </summary>
    public AzureStorageResource Parent => storage ?? throw new ArgumentNullException(nameof(storage));

    /// <summary>
    /// Gets the connection URI expression for the Azure Files service.
    /// </summary>
    public ReferenceExpression UriExpression => Parent.FileUriExpression;

    /// <summary>
    /// Gets the connection string expression for the Azure Files service.
    /// </summary>
    public ReferenceExpression ConnectionStringExpression => Parent.GetFileConnectionString();

    internal ReferenceExpression GetConnectionString(string? fileShareName)
    {
        if (string.IsNullOrEmpty(fileShareName))
        {
            return ConnectionStringExpression;
        }

        ReferenceExpressionBuilder builder = new();
        builder.Append($"Endpoint={ConnectionStringExpression}");
        builder.Append($";FileShareName={fileShareName}");

        return builder.Build();
    }

    BicepOutputReference IAzurePrivateEndpointTarget.Id => Parent.Id;

    IEnumerable<string> IAzurePrivateEndpointTarget.GetPrivateLinkGroupIds() => ["file"];

    IEnumerable<string> IAzurePrivateEndpointTarget.GetPrivateDnsZoneNames() => ["privatelink.file.core.windows.net"];

    IEnumerable<KeyValuePair<string, ReferenceExpression>> IResourceWithConnectionString.GetConnectionProperties()
    {
        yield return new("Uri", UriExpression);
    }
}
