// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics.CodeAnalysis;
using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.Azure;

/// <summary>
/// Represents an Azure resource that can be connected to via a private endpoint.
/// </summary>
[Experimental("ASPIREAZURE003", UrlFormat = "https://aka.ms/dotnet/aspire/diagnostics#{0}")]
public interface IAzurePrivateEndpointTarget : IResource
{
    /// <summary>
    /// Gets the "id" output reference from the Azure resource.
    /// </summary>
    BicepOutputReference Id { get; }

    /// <summary>
    /// Gets the group IDs for the private link service connection (e.g., "blob", "file" for storage).
    /// </summary>
    /// <returns>A collection of group IDs for the private link service connection.</returns>
    IEnumerable<string> GetPrivateLinkGroupIds();

    /// <summary>
    /// Gets the private DNS zone name for this resource type (e.g., "privatelink.blob.core.windows.net" for blob storage).
    /// </summary>
    /// <returns>The private DNS zone name for the private endpoint.</returns>
    string GetPrivateDnsZoneName();
}
