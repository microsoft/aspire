// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics.CodeAnalysis;

namespace Aspire.Hosting.ApplicationModel;

/// <summary>
/// Limits concurrent deployment operations for compute resources assigned to a compute environment.
/// </summary>
/// <remarks>
/// Attach this annotation to an <see cref="IComputeEnvironmentResource"/> when its deployment
/// provider restricts how many compute resources can be created or updated concurrently.
/// Deployment publishers can inspect the annotation through
/// <see cref="DeploymentTargetAnnotation.ComputeEnvironment"/> and enforce the limit without
/// introducing application dependency relationships between otherwise independent resources.
/// The absence of this annotation means the compute environment does not declare a concurrency limit.
/// </remarks>
[Experimental("ASPIRECOMPUTE004", UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
public sealed class DeploymentConcurrencyAnnotation : IResourceAnnotation
{
    /// <summary>
    /// Initializes a new instance of the <see cref="DeploymentConcurrencyAnnotation"/> class.
    /// </summary>
    /// <param name="maxConcurrentDeployments">
    /// The maximum number of compute-resource deployment operations that can execute concurrently.
    /// </param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when <paramref name="maxConcurrentDeployments"/> is less than one.
    /// </exception>
    public DeploymentConcurrencyAnnotation(int maxConcurrentDeployments)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxConcurrentDeployments, 1);
        MaxConcurrentDeployments = maxConcurrentDeployments;
    }

    /// <summary>
    /// Gets the maximum number of compute-resource deployment operations that can execute concurrently.
    /// </summary>
    public int MaxConcurrentDeployments { get; }
}
