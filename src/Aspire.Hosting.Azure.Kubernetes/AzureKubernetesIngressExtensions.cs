// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Azure.Kubernetes;
using Aspire.Hosting.Kubernetes;

namespace Aspire.Hosting;

/// <summary>
/// Provides extension methods for adding Kubernetes Ingress and Gateway resources to AKS environments.
/// </summary>
public static class AzureKubernetesIngressExtensions
{
    /// <summary>
    /// Adds a Kubernetes Ingress resource to the application model, associated with the
    /// inner Kubernetes environment of the specified AKS environment. The ingress generates
    /// a <c>networking.k8s.io/v1 Ingress</c> resource in the Helm chart output at publish time.
    /// </summary>
    /// <param name="builder">The AKS environment resource builder.</param>
    /// <param name="name">The name of the ingress resource.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{KubernetesIngressResource}"/> for chaining.</returns>
    /// <remarks>
    /// <para>
    /// This method delegates to the inner <see cref="KubernetesEnvironmentResource"/> of the AKS
    /// environment. To use an AKS-specific ingress controller (e.g., Azure Application Gateway
    /// for Containers), call <see cref="KubernetesIngressExtensions.WithIngressClass(IResourceBuilder{KubernetesIngressResource}, string)"/> with the
    /// appropriate class name.
    /// </para>
    /// </remarks>
    /// <example>
    /// <code>
    /// var aks = builder.AddAzureKubernetesEnvironment("aks");
    /// var ingress = aks.AddIngress("public")
    ///     .WithIngressClass("azure-alb-external");
    ///
    /// var api = builder.AddProject&lt;MyApi&gt;("api");
    /// ingress.WithRoute("/api", api.GetEndpoint("http"));
    /// </code>
    /// </example>
    [AspireExport(Description = "Adds a Kubernetes Ingress resource to an AKS environment")]
    public static IResourceBuilder<KubernetesIngressResource> AddIngress(
        this IResourceBuilder<AzureKubernetesEnvironmentResource> builder,
        [ResourceName] string name)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(name);

        var k8sEnvBuilder = builder.ApplicationBuilder.CreateResourceBuilder(builder.Resource.KubernetesEnvironment);
        return k8sEnvBuilder.AddIngress(name);
    }

    /// <summary>
    /// Adds a Kubernetes Gateway API Gateway resource to the application model, associated with the
    /// inner Kubernetes environment of the specified AKS environment.
    /// </summary>
    /// <param name="builder">The AKS environment resource builder.</param>
    /// <param name="name">The name of the gateway resource.</param>
    /// <returns>A reference to the <see cref="IResourceBuilder{KubernetesGatewayResource}"/> for chaining.</returns>
    /// <example>
    /// <code>
    /// var aks = builder.AddAzureKubernetesEnvironment("aks");
    /// var gateway = aks.AddGateway("public")
    ///     .WithGatewayClass("azure-alb-external");
    ///
    /// var api = builder.AddProject&lt;MyApi&gt;("api");
    /// gateway.WithRoute("/api", api.GetEndpoint("http"));
    /// </code>
    /// </example>
    [AspireExport(Description = "Adds a Kubernetes Gateway API Gateway to an AKS environment")]
    public static IResourceBuilder<KubernetesGatewayResource> AddGateway(
        this IResourceBuilder<AzureKubernetesEnvironmentResource> builder,
        [ResourceName] string name)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(name);

        var k8sEnvBuilder = builder.ApplicationBuilder.CreateResourceBuilder(builder.Resource.KubernetesEnvironment);
        return k8sEnvBuilder.AddGateway(name);
    }

    /// <summary>
    /// Adds an external Helm chart to be installed in the AKS environment's inner Kubernetes
    /// environment. The chart is installed via <c>helm upgrade --install</c> as a pipeline step
    /// after the main application Helm chart is deployed.
    /// </summary>
    /// <param name="builder">The AKS environment resource builder.</param>
    /// <param name="name">The name of the Helm chart resource.</param>
    /// <param name="chartReference">
    /// The Helm chart reference. Can be an OCI registry URL (e.g., <c>oci://quay.io/jetstack/charts/cert-manager</c>)
    /// or a chart name from an added repository.
    /// </param>
    /// <param name="chartVersion">The chart version to install.</param>
    /// <returns>A resource builder for the Helm chart resource.</returns>
    /// <example>
    /// <code>
    /// var aks = builder.AddAzureKubernetesEnvironment("aks");
    ///
    /// // Install cert-manager
    /// aks.AddHelmChart("cert-manager", "oci://quay.io/jetstack/charts/cert-manager", "1.17.0")
    ///     .WithHelmValue("crds.enabled", "true");
    /// </code>
    /// </example>
    [AspireExport(Description = "Adds an external Helm chart to an AKS environment")]
    public static IResourceBuilder<KubernetesHelmChartResource> AddHelmChart(
        this IResourceBuilder<AzureKubernetesEnvironmentResource> builder,
        [ResourceName] string name,
        string chartReference,
        string chartVersion)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentException.ThrowIfNullOrEmpty(chartReference);
        ArgumentException.ThrowIfNullOrEmpty(chartVersion);

        var k8sEnvBuilder = builder.ApplicationBuilder.CreateResourceBuilder(builder.Resource.KubernetesEnvironment);
        return k8sEnvBuilder.AddHelmChart(name, chartReference, chartVersion);
    }
}
