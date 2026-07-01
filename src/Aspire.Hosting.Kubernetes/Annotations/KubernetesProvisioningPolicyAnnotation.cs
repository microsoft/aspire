// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting;

/// <summary>
/// Represents an annotation for customizing the PVC provisioning policy of a Kubernetes resource.
/// </summary>
/// <param name="shouldDynamicallyProvision">Whether or not PVs should be dynamically provisioned.</param>
public sealed class KubernetesProvisioningPolicyAnnotation(bool shouldDynamicallyProvision) : IResourceAnnotation
{
    /// <summary>
    /// Gets or sets whether or not PVs should be dynamically provisioned for the given resource.
    /// If this is set to true, *-pv.yaml files will not be generated for the resource.
    /// </summary>
    public bool ShouldDynamicallyProvision { get; } = shouldDynamicallyProvision;
}

/// <summary>
/// Extension methods for configuring the provisioning policy of a Kubernetes resource using <see cref="KubernetesProvisioningPolicyAnnotation"/>.
/// </summary>
public static class KubernetesProvisioningPolicyAnnotationExtensions
{
    /// <summary>
    /// Configures whether or not PVs should be dynamically provisioned for the given resource.
    /// </summary>
    /// <typeparam name="T"></typeparam>
    /// <param name="builder">The resource builder.</param>
    /// <param name="shouldDynamicallyProvision">Whether or not PVs should be dynamically provisioned. If set to true, *-pv.yaml files will not be generated for the resource.</param>
    /// <returns>The resource builder.</returns>
    [AspireExport]
    public static IResourceBuilder<T> WithDynamicProvisioning<T>(this IResourceBuilder<T> builder, bool shouldDynamicallyProvision = false) where T : IComputeResource
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.WithAnnotation(new KubernetesProvisioningPolicyAnnotation(shouldDynamicallyProvision), ResourceAnnotationMutationBehavior.Replace);
        return builder;
    }
}