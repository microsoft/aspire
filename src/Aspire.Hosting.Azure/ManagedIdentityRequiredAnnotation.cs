// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics.CodeAnalysis;
using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.Azure;

/// <summary>
/// Indicates that a compute resource needs a user-assigned managed identity when it is published,
/// even if it has no Azure role assignments.
/// </summary>
/// <remarks>
/// Resources that reference Azure resources get a managed identity so that role assignments can be applied to it.
/// Some integrations need the identity for another reason, such as an Entra ID application registration that trusts it.
/// Add this annotation so the Azure resource preparer creates the identity, or reuses the one added with
/// <c>WithAzureUserAssignedIdentity</c>. The identity is available from the resource's <see cref="AppIdentityAnnotation"/>
/// once the preparer has run. Environments that do not support managed identities do not create one.
/// </remarks>
[Experimental("ASPIREAZURE003", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
public sealed class ManagedIdentityRequiredAnnotation : IResourceAnnotation
{
}
