// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.Kubernetes;

/// <summary>
/// Represents a cert-manager installation on a Kubernetes environment.
/// </summary>
/// <remarks>
/// <para>
/// cert-manager is a Kubernetes add-on that provisions and renews X.509
/// certificates from sources such as Let's Encrypt. Aspire models cert-manager
/// as a typed resource so that issuer resources (<see cref="CertManagerIssuerResource"/>)
/// can be parented to it and gateways/ingresses can reference issuers in a strongly-typed
/// way via <c>WithTls(issuer)</c>.
/// </para>
/// <para>
/// Under the covers, <see cref="CertManagerExtensions.AddCertManager"/> installs cert-manager
/// using a <see cref="KubernetesHelmChartResource"/> pointed at the upstream
/// <c>oci://quay.io/jetstack/charts/cert-manager</c> chart, with CRDs and Gateway API
/// support enabled. The chart resource is registered in the model under
/// <c>"{name}-chart"</c> (so the cert-manager wrapper itself can keep the natural
/// <c>"{name}"</c> identifier) and is exposed via <see cref="HelmChart"/> for advanced
/// configuration.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// var aks = builder.AddAzureKubernetesEnvironment("aks");
/// var certManager = aks.AddCertManager("cert-manager");
///
/// var letsencrypt = certManager.AddIssuer("letsencrypt-prod")
///     .WithLetsEncryptProduction("ops@contoso.com")
///     .WithHttp01Solver();
///
/// aks.AddGateway("gw")
///    .WithRoute("/api", api.GetEndpoint("http"))
///    .WithTls(letsencrypt);
/// </code>
/// </example>
[AspireExport]
public sealed class CertManagerResource : Resource, IResourceWithParent<KubernetesEnvironmentResource>
{
    /// <summary>
    /// Initializes a new instance of <see cref="CertManagerResource"/>.
    /// </summary>
    /// <param name="name">The Aspire resource name for this cert-manager installation.</param>
    /// <param name="environment">The parent Kubernetes environment.</param>
    /// <param name="helmChart">The underlying Helm chart resource that installs cert-manager.</param>
    public CertManagerResource(
        string name,
        KubernetesEnvironmentResource environment,
        KubernetesHelmChartResource helmChart) : base(name)
    {
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(helmChart);

        Parent = environment;
        HelmChart = helmChart;
    }

    /// <summary>
    /// Gets the parent Kubernetes environment that hosts cert-manager.
    /// </summary>
    public KubernetesEnvironmentResource Parent { get; }

    /// <summary>
    /// Gets the underlying Helm chart resource used to install cert-manager.
    /// Use this to layer additional Helm values via <c>WithHelmChartValues</c> or to
    /// inspect chart metadata. The chart name and version are fixed at construction
    /// time and cannot be changed through this property.
    /// </summary>
    public KubernetesHelmChartResource HelmChart { get; }

    /// <summary>
    /// Gets the issuers declared against this cert-manager installation.
    /// </summary>
    internal List<CertManagerIssuerResource> Issuers { get; } = [];
}
