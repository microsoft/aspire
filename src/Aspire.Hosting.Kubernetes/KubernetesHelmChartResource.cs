// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.Kubernetes;

/// <summary>
/// Represents an external Helm chart to be installed into a Kubernetes environment.
/// This resource models a Helm chart from a repository (OCI or HTTP) that will be
/// installed as a pipeline step after the main application Helm chart is deployed.
/// </summary>
/// <param name="name">The name of the Helm chart resource.</param>
/// <param name="environment">The parent Kubernetes environment resource.</param>
/// <remarks>
/// <para>
/// Create a Helm chart resource using <see cref="KubernetesHelmChartExtensions.AddHelmChart"/>
/// and configure it with values using <see cref="KubernetesHelmChartExtensions.WithHelmValue"/>.
/// </para>
/// <para>
/// At deploy time, the chart is installed via <c>helm upgrade --install</c> as a pipeline step
/// that runs after the main application Helm chart is deployed. The chart is installed into
/// the specified namespace (defaulting to the chart name).
/// </para>
/// </remarks>
/// <example>
/// <code>
/// var k8s = builder.AddKubernetesEnvironment("k8s");
///
/// // Install NGINX Ingress Controller from OCI registry
/// k8s.AddHelmChart("nginx", "oci://ghcr.io/nginx/charts/nginx-ingress", "1.5.0");
///
/// // Install cert-manager with custom values
/// k8s.AddHelmChart("cert-manager", "oci://quay.io/jetstack/charts/cert-manager", "1.17.0")
///     .WithHelmValue("crds.enabled", "true")
///     .WithHelmValue("config.enableGatewayAPI", "true");
/// </code>
/// </example>
[AspireExport]
public class KubernetesHelmChartResource(
    string name,
    KubernetesEnvironmentResource environment) : Resource(name), IResourceWithParent<KubernetesEnvironmentResource>
{
    /// <summary>
    /// Gets the parent Kubernetes environment resource.
    /// </summary>
    public KubernetesEnvironmentResource Parent { get; } = environment ?? throw new ArgumentNullException(nameof(environment));

    /// <summary>
    /// Gets or sets the Helm chart reference. This can be an OCI registry URL
    /// (e.g., <c>oci://quay.io/jetstack/charts/cert-manager</c>) or a chart name
    /// from an added repository.
    /// </summary>
    public string? ChartReference { get; set; }

    /// <summary>
    /// Gets or sets the chart version to install.
    /// </summary>
    public string? ChartVersion { get; set; }

    /// <summary>
    /// Gets or sets the Kubernetes namespace to install the chart into.
    /// Defaults to the chart resource name if not specified.
    /// </summary>
    public string? Namespace { get; set; }

    /// <summary>
    /// Gets or sets the Helm release name. Defaults to the resource name.
    /// </summary>
    public string? ReleaseName { get; set; }

    /// <summary>
    /// Gets the Helm values to set during installation.
    /// Keys are dot-separated paths (e.g., <c>config.enableGatewayAPI</c>),
    /// values are the string values to set.
    /// </summary>
    internal Dictionary<string, string> Values { get; } = [];
}
