// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.RegularExpressions;
using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.Kubernetes;

/// <summary>
/// Provides options for configuring Helm chart deployment settings on a <see cref="KubernetesEnvironmentResource"/>.
/// </summary>
/// <remarks>
/// This class is used as the configuration callback parameter for
/// <see cref="KubernetesEnvironmentExtensions.WithHelm(IResourceBuilder{KubernetesEnvironmentResource}, Action{HelmChartOptions})"/>.
/// Each method adds a corresponding annotation to the environment resource.
/// </remarks>
[AspireExport(ExposeMethods = true)]
public sealed partial class HelmChartOptions
{
    private const int KubernetesNamespaceMaxLength = 63;
    private const int HelmReleaseNameMaxLength = 53;

    internal IResourceBuilder<KubernetesEnvironmentResource> EnvironmentBuilder { get; }

    internal HelmChartOptions(IResourceBuilder<KubernetesEnvironmentResource> environmentBuilder)
    {
        EnvironmentBuilder = environmentBuilder;
    }

    /// <summary>
    /// Sets the target Kubernetes namespace for deployment.
    /// </summary>
    /// <param name="namespace">The namespace name.</param>
    /// <returns>This <see cref="HelmChartOptions"/> for chaining.</returns>
    public HelmChartOptions WithNamespace(string @namespace)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(@namespace);
        ValidateDnsLabel(@namespace, "Kubernetes namespace", KubernetesNamespaceMaxLength, nameof(@namespace));

        var expression = ReferenceExpression.Create($"{@namespace}");
        EnvironmentBuilder.WithAnnotation(new KubernetesNamespaceAnnotation(expression), ResourceAnnotationMutationBehavior.Replace);
        return this;
    }

    /// <summary>
    /// Sets the target Kubernetes namespace for deployment using a parameter that will be prompted at deploy time.
    /// </summary>
    /// <param name="namespace">A parameter resource builder for the namespace value.</param>
    /// <returns>This <see cref="HelmChartOptions"/> for chaining.</returns>
    public HelmChartOptions WithNamespace(IResourceBuilder<ParameterResource> @namespace)
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
    /// <returns>This <see cref="HelmChartOptions"/> for chaining.</returns>
    public HelmChartOptions WithReleaseName(string releaseName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(releaseName);
        ValidateDnsLabel(releaseName, "Helm release name", HelmReleaseNameMaxLength, nameof(releaseName));

        var expression = ReferenceExpression.Create($"{releaseName}");
        EnvironmentBuilder.WithAnnotation(new HelmReleaseNameAnnotation(expression), ResourceAnnotationMutationBehavior.Replace);
        return this;
    }

    /// <summary>
    /// Sets the Helm release name for deployment using a parameter that will be prompted at deploy time.
    /// </summary>
    /// <param name="releaseName">A parameter resource builder for the release name value.</param>
    /// <returns>This <see cref="HelmChartOptions"/> for chaining.</returns>
    public HelmChartOptions WithReleaseName(IResourceBuilder<ParameterResource> releaseName)
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
    /// <returns>This <see cref="HelmChartOptions"/> for chaining.</returns>
    public HelmChartOptions WithChartVersion(string version)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(version);
        ValidateChartVersion(version);

        var expression = ReferenceExpression.Create($"{version}");
        EnvironmentBuilder.WithAnnotation(new HelmChartVersionAnnotation(expression), ResourceAnnotationMutationBehavior.Replace);
        return this;
    }

    /// <summary>
    /// Sets the Helm chart version for deployment using a parameter that will be prompted at deploy time.
    /// </summary>
    /// <param name="version">A parameter resource builder for the chart version value.</param>
    /// <returns>This <see cref="HelmChartOptions"/> for chaining.</returns>
    public HelmChartOptions WithChartVersion(IResourceBuilder<ParameterResource> version)
    {
        ArgumentNullException.ThrowIfNull(version);

        var expression = ReferenceExpression.Create($"{version.Resource}");
        EnvironmentBuilder.WithAnnotation(new HelmChartVersionAnnotation(expression), ResourceAnnotationMutationBehavior.Replace);
        return this;
    }

    private static void ValidateDnsLabel(string value, string target, int maxLength, string paramName)
    {
        if (value.Length > maxLength)
        {
            throw new ArgumentException($"{target} '{value}' is invalid. It must be {maxLength} characters or fewer.", paramName);
        }

        if (!DnsLabelPattern().IsMatch(value))
        {
            throw new ArgumentException($"{target} '{value}' is invalid. Use lowercase letters, numbers, and hyphens, and start and end with an alphanumeric character.", paramName);
        }
    }

    private static void ValidateChartVersion(string version)
    {
        if (!Aspire.SemVersion.TryParse(version, Aspire.SemVersionStyles.Strict, out _))
        {
            throw new ArgumentException($"Helm chart version '{version}' is invalid. Use a semantic version such as '1.0.0' or '1.0.0-beta.1'.", nameof(version));
        }
    }

    [GeneratedRegex("^[a-z0-9]([-a-z0-9]*[a-z0-9])?$")]
    private static partial Regex DnsLabelPattern();
}
