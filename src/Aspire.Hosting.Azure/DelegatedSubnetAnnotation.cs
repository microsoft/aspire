// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics.CodeAnalysis;
using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.Azure;

/// <summary>
/// Annotation that stores a reference to a subnet for an Azure resource that implements <see cref="IAzureDelegatedSubnetResource"/>.
/// </summary>
/// <param name="subnetId">The subnet ID reference expression.</param>
[Experimental("ASPIREAZURE003", UrlFormat = "https://aka.ms/dotnet/aspire/diagnostics#{0}")]
public sealed class DelegatedSubnetAnnotation(ReferenceExpression subnetId) : IResourceAnnotation
{
    /// <summary>
    /// Gets the subnet ID reference expression.
    /// </summary>
    public ReferenceExpression SubnetId { get; } = subnetId;
}
