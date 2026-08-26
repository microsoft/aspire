// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics.CodeAnalysis;

namespace Aspire.Hosting.ApplicationModel;

/// <summary>
/// Defines a group of resource deployment operations that share a concurrency limit.
/// </summary>
/// <remarks>
/// Share the same group instance across the <see cref="DeploymentConcurrencyGroupAnnotation"/> values
/// whose deployment operations consume the same limited capacity. A resource can participate in multiple
/// groups, and publishers should enforce every applicable group. Group membership is determined by
/// reference identity. Publishers lower group membership into their native scheduling dependencies before
/// serializing deployment artifacts, so group identity does not become an artifact-level contract.
/// </remarks>
[Experimental("ASPIRECOMPUTE004", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
public sealed class DeploymentConcurrencyGroup
{
    /// <summary>
    /// Initializes a new instance of the <see cref="DeploymentConcurrencyGroup"/> class.
    /// </summary>
    /// <param name="maxConcurrentDeployments">
    /// The maximum number of deployment operations in the group that can execute concurrently.
    /// </param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when <paramref name="maxConcurrentDeployments"/> is less than one.
    /// </exception>
    public DeploymentConcurrencyGroup(int maxConcurrentDeployments)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxConcurrentDeployments, 1);

        MaxConcurrentDeployments = maxConcurrentDeployments;
    }

    /// <summary>
    /// Gets the maximum number of deployment operations in the group that can execute concurrently.
    /// </summary>
    public int MaxConcurrentDeployments { get; }
}
