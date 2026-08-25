// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics.CodeAnalysis;

namespace Aspire.Hosting.ApplicationModel;

/// <summary>
/// Defines a group of deployment targets that share a concurrency limit.
/// </summary>
/// <remarks>
/// Share the same group instance across the <see cref="DeploymentTargetAnnotation"/> values whose
/// deployment operations consume the same limited capacity. A deployment target can participate in
/// multiple groups, and publishers should enforce every applicable group. In an in-process application
/// model, group membership is determined by reference identity. <see cref="Name"/> provides a portable
/// identity for serialized deployment artifacts and must be unique across distinct group instances.
/// </remarks>
[Experimental("ASPIRECOMPUTE004", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
public sealed class DeploymentConcurrencyGroup
{
    /// <summary>
    /// Initializes a new instance of the <see cref="DeploymentConcurrencyGroup"/> class.
    /// </summary>
    /// <param name="name">
    /// The stable name used to identify the group in diagnostics and serialized deployment artifacts.
    /// The name must be unique within the application model.
    /// </param>
    /// <param name="maxConcurrentDeployments">
    /// The maximum number of deployment operations in the group that can execute concurrently.
    /// </param>
    /// <exception cref="ArgumentNullException">
    /// Thrown when <paramref name="name"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// Thrown when <paramref name="name"/> is empty or consists only of white-space characters.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when <paramref name="maxConcurrentDeployments"/> is less than one.
    /// </exception>
    public DeploymentConcurrencyGroup(string name, int maxConcurrentDeployments)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxConcurrentDeployments, 1);

        Name = name;
        MaxConcurrentDeployments = maxConcurrentDeployments;
    }

    /// <summary>
    /// Gets the stable name used to identify the group.
    /// </summary>
    public string Name { get; }

    /// <summary>
    /// Gets the maximum number of deployment operations in the group that can execute concurrently.
    /// </summary>
    public int MaxConcurrentDeployments { get; }
}
