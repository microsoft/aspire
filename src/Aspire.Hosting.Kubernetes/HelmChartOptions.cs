// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.RegularExpressions;
using Aspire.Hosting.ApplicationModel;
using Semver;

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
    private const int HelmChartNameMaxLength = 250;
    private const int HelmChartDescriptionMaxLength = 1024;

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
    [AspireExportIgnore(Reason = "Polyglot app hosts use the union-based withNamespace dispatcher export.")]
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
    [AspireExportIgnore(Reason = "Polyglot app hosts use the union-based withNamespace dispatcher export.")]
    public HelmChartOptions WithNamespace(IResourceBuilder<ParameterResource> @namespace)
    {
        ArgumentNullException.ThrowIfNull(@namespace);

        var expression = ReferenceExpression.Create($"{@namespace.Resource}");
        EnvironmentBuilder.WithAnnotation(new KubernetesNamespaceAnnotation(expression), ResourceAnnotationMutationBehavior.Replace);
        return this;
    }

    [AspireExport(MethodName = "withNamespace", Description = "Sets the target Kubernetes namespace for deployment.")]
    internal HelmChartOptions WithNamespace([AspireUnion(typeof(string), typeof(IResourceBuilder<ParameterResource>))] object @namespace)
    {
        ArgumentNullException.ThrowIfNull(@namespace);

        return @namespace switch
        {
            string namespaceName => WithNamespace(namespaceName),
            IResourceBuilder<ParameterResource> namespaceParameter => WithNamespace(namespaceParameter),
            _ => throw new ArgumentException("Namespace must be a string or a parameter resource builder.", nameof(@namespace))
        };
    }

    /// <summary>
    /// Sets the Helm release name for deployment.
    /// </summary>
    /// <param name="releaseName">The release name.</param>
    /// <returns>This <see cref="HelmChartOptions"/> for chaining.</returns>
    [AspireExportIgnore(Reason = "Polyglot app hosts use the union-based withReleaseName dispatcher export.")]
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
    [AspireExportIgnore(Reason = "Polyglot app hosts use the union-based withReleaseName dispatcher export.")]
    public HelmChartOptions WithReleaseName(IResourceBuilder<ParameterResource> releaseName)
    {
        ArgumentNullException.ThrowIfNull(releaseName);

        var expression = ReferenceExpression.Create($"{releaseName.Resource}");
        EnvironmentBuilder.WithAnnotation(new HelmReleaseNameAnnotation(expression), ResourceAnnotationMutationBehavior.Replace);
        return this;
    }

    [AspireExport(MethodName = "withReleaseName", Description = "Sets the Helm release name for deployment.")]
    internal HelmChartOptions WithReleaseName([AspireUnion(typeof(string), typeof(IResourceBuilder<ParameterResource>))] object releaseName)
    {
        ArgumentNullException.ThrowIfNull(releaseName);

        return releaseName switch
        {
            string releaseNameValue => WithReleaseName(releaseNameValue),
            IResourceBuilder<ParameterResource> releaseNameParameter => WithReleaseName(releaseNameParameter),
            _ => throw new ArgumentException("Release name must be a string or a parameter resource builder.", nameof(releaseName))
        };
    }

    /// <summary>
    /// Sets the Helm chart version for deployment.
    /// </summary>
    /// <param name="version">The chart version (e.g., "1.0.0").</param>
    /// <returns>This <see cref="HelmChartOptions"/> for chaining.</returns>
    [AspireExportIgnore(Reason = "Polyglot app hosts use the union-based withChartVersion dispatcher export.")]
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
    [AspireExportIgnore(Reason = "Polyglot app hosts use the union-based withChartVersion dispatcher export.")]
    public HelmChartOptions WithChartVersion(IResourceBuilder<ParameterResource> version)
    {
        ArgumentNullException.ThrowIfNull(version);

        var expression = ReferenceExpression.Create($"{version.Resource}");
        EnvironmentBuilder.WithAnnotation(new HelmChartVersionAnnotation(expression), ResourceAnnotationMutationBehavior.Replace);
        return this;
    }

    [AspireExport(MethodName = "withChartVersion", Description = "Sets the Helm chart version for deployment.")]
    internal HelmChartOptions WithChartVersion([AspireUnion(typeof(string), typeof(IResourceBuilder<ParameterResource>))] object version)
    {
        ArgumentNullException.ThrowIfNull(version);

        return version switch
        {
            string versionValue => WithChartVersion(versionValue),
            IResourceBuilder<ParameterResource> versionParameter => WithChartVersion(versionParameter),
            _ => throw new ArgumentException("Chart version must be a string or a parameter resource builder.", nameof(version))
        };
    }

    /// <summary>
    /// Sets the Helm chart name written to the generated <c>Chart.yaml</c>.
    /// </summary>
    /// <param name="name">The chart name. Must match Helm's chart-name format (alphanumeric, <c>-</c>, <c>_</c>, or <c>.</c>).</param>
    /// <returns>This <see cref="HelmChartOptions"/> for chaining.</returns>
    [AspireExportIgnore(Reason = "Polyglot app hosts use the union-based withChartName dispatcher export.")]
    public HelmChartOptions WithChartName(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ValidateChartName(name, nameof(name));

        var expression = ReferenceExpression.Create($"{name}");
        EnvironmentBuilder.WithAnnotation(new HelmChartNameAnnotation(expression), ResourceAnnotationMutationBehavior.Replace);
        return this;
    }

    /// <summary>
    /// Sets the Helm chart name written to the generated <c>Chart.yaml</c> using a parameter that will be prompted at deploy time.
    /// </summary>
    /// <param name="name">A parameter resource builder for the chart name value.</param>
    /// <returns>This <see cref="HelmChartOptions"/> for chaining.</returns>
    [AspireExportIgnore(Reason = "Polyglot app hosts use the union-based withChartName dispatcher export.")]
    public HelmChartOptions WithChartName(IResourceBuilder<ParameterResource> name)
    {
        ArgumentNullException.ThrowIfNull(name);

        var expression = ReferenceExpression.Create($"{name.Resource}");
        EnvironmentBuilder.WithAnnotation(new HelmChartNameAnnotation(expression), ResourceAnnotationMutationBehavior.Replace);
        return this;
    }

    [AspireExport(MethodName = "withChartName", Description = "Sets the Helm chart name written to the generated Chart.yaml.")]
    internal HelmChartOptions WithChartName([AspireUnion(typeof(string), typeof(IResourceBuilder<ParameterResource>))] object name)
    {
        ArgumentNullException.ThrowIfNull(name);

        return name switch
        {
            string nameValue => WithChartName(nameValue),
            IResourceBuilder<ParameterResource> nameParameter => WithChartName(nameParameter),
            _ => throw new ArgumentException("Chart name must be a string or a parameter resource builder.", nameof(name))
        };
    }

    /// <summary>
    /// Sets the Helm chart description written to the generated <c>Chart.yaml</c>.
    /// </summary>
    /// <param name="description">The chart description.</param>
    /// <returns>This <see cref="HelmChartOptions"/> for chaining.</returns>
    [AspireExportIgnore(Reason = "Polyglot app hosts use the union-based withChartDescription dispatcher export.")]
    public HelmChartOptions WithChartDescription(string description)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(description);
        ValidateChartDescription(description, nameof(description));

        var expression = ReferenceExpression.Create($"{description}");
        EnvironmentBuilder.WithAnnotation(new HelmChartDescriptionAnnotation(expression), ResourceAnnotationMutationBehavior.Replace);
        return this;
    }

    /// <summary>
    /// Sets the Helm chart description written to the generated <c>Chart.yaml</c> using a parameter that will be prompted at deploy time.
    /// </summary>
    /// <param name="description">A parameter resource builder for the chart description value.</param>
    /// <returns>This <see cref="HelmChartOptions"/> for chaining.</returns>
    [AspireExportIgnore(Reason = "Polyglot app hosts use the union-based withChartDescription dispatcher export.")]
    public HelmChartOptions WithChartDescription(IResourceBuilder<ParameterResource> description)
    {
        ArgumentNullException.ThrowIfNull(description);

        var expression = ReferenceExpression.Create($"{description.Resource}");
        EnvironmentBuilder.WithAnnotation(new HelmChartDescriptionAnnotation(expression), ResourceAnnotationMutationBehavior.Replace);
        return this;
    }

    [AspireExport(MethodName = "withChartDescription", Description = "Sets the Helm chart description written to the generated Chart.yaml.")]
    internal HelmChartOptions WithChartDescription([AspireUnion(typeof(string), typeof(IResourceBuilder<ParameterResource>))] object description)
    {
        ArgumentNullException.ThrowIfNull(description);

        return description switch
        {
            string descriptionValue => WithChartDescription(descriptionValue),
            IResourceBuilder<ParameterResource> descriptionParameter => WithChartDescription(descriptionParameter),
            _ => throw new ArgumentException("Chart description must be a string or a parameter resource builder.", nameof(description))
        };
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
        if (!SemVersion.TryParse(version, SemVersionStyles.Strict, out _))
        {
            throw new ArgumentException($"Helm chart version '{version}' is invalid. Use a semantic version such as '1.0.0' or '1.0.0-beta.1'.", nameof(version));
        }
    }

    internal static void ValidateChartName(string name, string paramName)
    {
        if (name.Length > HelmChartNameMaxLength)
        {
            throw new ArgumentException($"Helm chart name '{name}' is invalid. It must be {HelmChartNameMaxLength} characters or fewer.", paramName);
        }

        if (!HelmChartNamePattern().IsMatch(name))
        {
            throw new ArgumentException($"Helm chart name '{name}' is invalid. Use alphanumeric characters, '-', '_', or '.'.", paramName);
        }
    }

    internal static void ValidateChartDescription(string description, string paramName)
    {
        if (description.Length > HelmChartDescriptionMaxLength)
        {
            throw new ArgumentException($"Helm chart description is invalid. It must be {HelmChartDescriptionMaxLength} characters or fewer.", paramName);
        }
    }

    [GeneratedRegex("^[a-z0-9]([-a-z0-9]*[a-z0-9])?$")]
    private static partial Regex DnsLabelPattern();

    [GeneratedRegex("^[a-zA-Z0-9._-]+$")]
    private static partial Regex HelmChartNamePattern();
}
