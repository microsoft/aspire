// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.Kubernetes;

/// <summary>
/// An annotation that configures ingress for resources deployed to Kubernetes.
/// </summary>
/// <remarks>
/// <para>
/// When attached to a <see cref="KubernetesEnvironmentResource"/>, the callback is invoked
/// for every resource in that environment that has external HTTP endpoints, providing the
/// default ingress behavior for the environment.
/// </para>
/// <para>
/// When attached to an individual <see cref="IComputeResource"/>, the callback overrides
/// the environment-level default for that specific resource.
/// </para>
/// </remarks>
/// <param name="configure">
/// The callback that configures ingress. Typically adds <see cref="Resources.Ingress"/>
/// objects to <see cref="KubernetesResource.AdditionalResources"/>.
/// </param>
public sealed class KubernetesIngressConfigurationAnnotation(
    Func<KubernetesIngressContext, Task> configure) : IResourceAnnotation
{
    /// <summary>
    /// Gets the callback that configures ingress for a Kubernetes resource.
    /// </summary>
    public Func<KubernetesIngressContext, Task> Configure { get; } = configure ?? throw new ArgumentNullException(nameof(configure));
}
