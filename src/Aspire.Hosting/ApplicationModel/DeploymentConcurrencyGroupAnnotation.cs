// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics.CodeAnalysis;

namespace Aspire.Hosting.ApplicationModel;

/// <summary>
/// Associates a resource's deployment operation with a shared deployment concurrency group.
/// </summary>
/// <remarks>
/// Add one annotation for each concurrency constraint that applies to the resource's deployment
/// operation. Resources that reference the same <see cref="DeploymentConcurrencyGroup"/> instance
/// must not deploy concurrently. Publishers should enforce every group associated with a resource.
/// </remarks>
/// <param name="group">The deployment concurrency group associated with the resource.</param>
/// <exception cref="ArgumentNullException">Thrown when <paramref name="group"/> is <see langword="null"/>.</exception>
[Experimental("ASPIRECOMPUTE004", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
public sealed class DeploymentConcurrencyGroupAnnotation(DeploymentConcurrencyGroup group) : IResourceAnnotation
{
    /// <summary>
    /// Gets the deployment concurrency group associated with the resource.
    /// </summary>
    public DeploymentConcurrencyGroup Group { get; } = group ?? throw new ArgumentNullException(nameof(group));
}
