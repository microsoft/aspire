// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics.CodeAnalysis;
using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.Azure;

/// <summary>
/// Represents an Azure resource that can be associated with a Network Security Perimeter.
/// </summary>
/// <remarks>
/// Implement this interface on PaaS resources (such as Storage, Key Vault, Cosmos DB, SQL)
/// that support Network Security Perimeter association via the
/// <c>Microsoft.Network/networkSecurityPerimeters/resourceAssociations</c> resource type.
/// </remarks>
[Experimental("ASPIREAZURE003", UrlFormat = "https://aka.ms/aspire/diagnostics#{0}")]
public interface IAzureNspAssociationTarget : IResource
{
    /// <summary>
    /// Gets the "id" output reference from the Azure resource.
    /// </summary>
    BicepOutputReference Id { get; }
}
