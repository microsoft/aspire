// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.Kubernetes;

/// <summary>
/// Provides a builder for configuring Helm chart deployment settings on a <see cref="KubernetesEnvironmentResource"/>.
/// </summary>
/// <remarks>
/// This class is used as the configuration callback parameter for
/// <see cref="KubernetesEnvironmentExtensions.WithHelm(IResourceBuilder{KubernetesEnvironmentResource}, Action{HelmChartConfiguration})"/>.
/// Each method adds a corresponding annotation to the environment resource.
/// </remarks>
public sealed class HelmChartConfiguration
{
    internal IResourceBuilder<KubernetesEnvironmentResource> EnvironmentBuilder { get; }

    internal HelmChartConfiguration(IResourceBuilder<KubernetesEnvironmentResource> environmentBuilder)
    {
        EnvironmentBuilder = environmentBuilder;
    }

    /// <summary>
    /// Sets the target Kubernetes namespace for deployment.
    /// </summary>
    /// <param name="namespace">The namespace name.</param>
    /// <returns>This <see cref="HelmChartConfiguration"/> for chaining.</returns>
    public HelmChartConfiguration WithNamespace(string @namespace)
    {
        ArgumentException.ThrowIfNullOrEmpty(@namespace);

        var expression = ReferenceExpression.Create($"{@namespace}");
        EnvironmentBuilder.WithAnnotation(new KubernetesNamespaceAnnotation(expression), ResourceAnnotationMutationBehavior.Replace);
        return this;
    }

    /// <summary>
    /// Sets the target Kubernetes namespace for deployment using a parameter that will be prompted at deploy time.
    /// </summary>
    /// <param name="namespace">A parameter resource builder for the namespace value.</param>
    /// <returns>This <see cref="HelmChartConfiguration"/> for chaining.</returns>
    public HelmChartConfiguration WithNamespace(IResourceBuilder<ParameterResource> @namespace)
    {
        ArgumentNullException.ThrowIfNull(@namespace);

        var expression = ReferenceExpression.Create($"{@namespace.Resource}");
        EnvironmentBuilder.WithAnnotation(new KubernetesNamespaceAnnotation(expression), ResourceAnnotationMutationBehavior.Replace);
        return this;
    }

    /// <summary>
    /// Sets the Helm release name for deployment.
    /// </summary>
    /// <param name="releaseName">The release name.</param>
    /// <returns>This <see cref="HelmChartConfiguration"/> for chaining.</returns>
    public HelmChartConfiguration WithReleaseName(string releaseName)
    {
        ArgumentException.ThrowIfNullOrEmpty(releaseName);

        var expression = ReferenceExpression.Create($"{releaseName}");
        EnvironmentBuilder.WithAnnotation(new HelmReleaseNameAnnotation(expression), ResourceAnnotationMutationBehavior.Replace);
        return this;
    }

    /// <summary>
    /// Sets the Helm release name for deployment using a parameter that will be prompted at deploy time.
    /// </summary>
    /// <param name="releaseName">A parameter resource builder for the release name value.</param>
    /// <returns>This <see cref="HelmChartConfiguration"/> for chaining.</returns>
    public HelmChartConfiguration WithReleaseName(IResourceBuilder<ParameterResource> releaseName)
    {
        ArgumentNullException.ThrowIfNull(releaseName);

        var expression = ReferenceExpression.Create($"{releaseName.Resource}");
        EnvironmentBuilder.WithAnnotation(new HelmReleaseNameAnnotation(expression), ResourceAnnotationMutationBehavior.Replace);
        return this;
    }

    /// <summary>
    /// Sets the Helm chart version for deployment.
    /// </summary>
    /// <param name="version">The chart version (e.g., "1.0.0").</param>
    /// <returns>This <see cref="HelmChartConfiguration"/> for chaining.</returns>
    public HelmChartConfiguration WithChartVersion(string version)
    {
        ArgumentException.ThrowIfNullOrEmpty(version);

        var expression = ReferenceExpression.Create($"{version}");
        EnvironmentBuilder.WithAnnotation(new HelmChartVersionAnnotation(expression), ResourceAnnotationMutationBehavior.Replace);
        return this;
    }

    /// <summary>
    /// Sets the Helm chart version for deployment using a parameter that will be prompted at deploy time.
    /// </summary>
    /// <param name="version">A parameter resource builder for the chart version value.</param>
    /// <returns>This <see cref="HelmChartConfiguration"/> for chaining.</returns>
    public HelmChartConfiguration WithChartVersion(IResourceBuilder<ParameterResource> version)
    {
        ArgumentNullException.ThrowIfNull(version);

        var expression = ReferenceExpression.Create($"{version.Resource}");
        EnvironmentBuilder.WithAnnotation(new HelmChartVersionAnnotation(expression), ResourceAnnotationMutationBehavior.Replace);
        return this;
    }
}
