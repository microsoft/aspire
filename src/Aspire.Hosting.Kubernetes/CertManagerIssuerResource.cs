// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.Kubernetes;

/// <summary>
/// Represents a cert-manager <c>ClusterIssuer</c> resource in the Aspire application model.
/// </summary>
/// <remarks>
/// <para>
/// At deploy time, an issuer is rendered to a <c>cert-manager.io/v1 ClusterIssuer</c>
/// YAML document and applied to the cluster with <c>kubectl apply</c> after the
/// cert-manager Helm chart is installed and its admission webhook is reachable. The
/// manifest is not baked into the helm chart output; it is applied directly so the
/// chart and its issuers can be managed and torn down independently.
/// </para>
/// <para>
/// Cluster-scoped issuers can be referenced by gateways and ingresses across all
/// namespaces, which matches the typical multi-namespace deployment pattern for
/// Aspire applications.
/// </para>
/// <para>
/// Namespace-scoped <c>Issuer</c> resources are intentionally not modeled in the initial
/// release. <see cref="CertManagerExtensions.AddIssuer"/> always produces a cluster-scoped
/// <c>ClusterIssuer</c>.
/// </para>
/// </remarks>
[AspireExport]
public sealed class CertManagerIssuerResource : Resource, IResourceWithParent<CertManagerResource>
{
    /// <summary>
    /// Initializes a new instance of <see cref="CertManagerIssuerResource"/>.
    /// </summary>
    /// <param name="name">The Aspire resource name. Also used as the <c>metadata.name</c> of
    /// the generated <c>ClusterIssuer</c>, so it must be a valid DNS-1123 label.</param>
    /// <param name="parent">The parent cert-manager installation.</param>
    public CertManagerIssuerResource(string name, CertManagerResource parent) : base(name)
    {
        ArgumentNullException.ThrowIfNull(parent);

        Parent = parent;
    }

    /// <summary>
    /// Gets the parent cert-manager installation.
    /// </summary>
    public CertManagerResource Parent { get; }

    /// <summary>
    /// Gets or sets the issuer specification (ACME, self-signed, CA, ...).
    /// </summary>
    /// <remarks>
    /// Set indirectly via <see cref="CertManagerExtensions.WithLetsEncryptProduction(IResourceBuilder{CertManagerIssuerResource}, string)"/>,
    /// <see cref="CertManagerExtensions.WithLetsEncryptStaging(IResourceBuilder{CertManagerIssuerResource}, string)"/>,
    /// or <see cref="CertManagerExtensions.WithAcmeServer(IResourceBuilder{CertManagerIssuerResource}, string, string)"/>.
    /// Only one issuer kind may be configured per resource; the most recent <c>WithXxx</c>
    /// call wins and replaces any prior spec.
    /// </remarks>
    internal CertManagerIssuerSpec? Spec { get; set; }

    /// <summary>
    /// Gets the configured ACME challenge solvers in the order they were added via
    /// <see cref="CertManagerExtensions.WithHttp01Solver"/>.
    /// </summary>
    /// <remarks>
    /// At least one solver is required for ACME issuers. The list is order-preserving so
    /// users can declare priority across multiple solvers in a future release.
    /// </remarks>
    internal List<CertManagerSolverConfig> Solvers { get; } = [];
}

/// <summary>
/// Base type for cert-manager issuer specifications. The subtype determines the
/// <c>spec.*</c> structure of the generated <c>ClusterIssuer</c> manifest.
/// </summary>
internal abstract record CertManagerIssuerSpec;

/// <summary>
/// Configures the issuer as an ACME (RFC 8555) issuer such as Let's Encrypt.
/// </summary>
/// <param name="ServerUrl">The ACME directory URL.</param>
/// <param name="Email">The contact email registered with the ACME account.</param>
internal sealed record CertManagerAcmeIssuerSpec(
    ReferenceExpression ServerUrl,
    ReferenceExpression Email) : CertManagerIssuerSpec;

/// <summary>
/// Base type for an ACME challenge solver configuration.
/// </summary>
internal abstract record CertManagerSolverConfig;

/// <summary>
/// Configures an HTTP-01 ACME solver. cert-manager will provision an
/// <c>HTTPRoute</c> (Gateway API) or <c>Ingress</c> at <c>/.well-known/acme-challenge/</c>
/// to satisfy the ACME challenge.
/// </summary>
internal sealed record CertManagerHttp01SolverConfig : CertManagerSolverConfig;
