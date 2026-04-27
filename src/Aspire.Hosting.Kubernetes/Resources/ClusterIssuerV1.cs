// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using YamlDotNet.Serialization;

namespace Aspire.Hosting.Kubernetes.Resources;

/// <summary>
/// Represents a cert-manager ClusterIssuer resource.
/// </summary>
[YamlSerializable]
public sealed class ClusterIssuer() : BaseKubernetesResource("cert-manager.io/v1", "ClusterIssuer")
{
    /// <summary>
    /// Gets or sets the ClusterIssuer spec.
    /// </summary>
    [YamlMember(Alias = "spec")]
    public ClusterIssuerSpec Spec { get; set; } = new();
}

/// <summary>
/// Spec for a ClusterIssuer.
/// </summary>
[YamlSerializable]
public sealed class ClusterIssuerSpec
{
    /// <summary>
    /// Gets or sets the ACME issuer configuration.
    /// </summary>
    [YamlMember(Alias = "acme")]
    public AcmeIssuer Acme { get; set; } = new();
}

/// <summary>
/// ACME issuer configuration.
/// </summary>
[YamlSerializable]
public sealed class AcmeIssuer
{
    /// <summary>
    /// Gets or sets the ACME server URL.
    /// </summary>
    [YamlMember(Alias = "server")]
    public string Server { get; set; } = "https://acme-v02.api.letsencrypt.org/directory";

    /// <summary>
    /// Gets or sets the email for ACME registration.
    /// </summary>
    [YamlMember(Alias = "email")]
    public string Email { get; set; } = default!;

    /// <summary>
    /// Gets or sets the private key secret reference.
    /// </summary>
    [YamlMember(Alias = "privateKeySecretRef")]
    public SecretKeyRef PrivateKeySecretRef { get; set; } = new() { Name = "letsencrypt-private-key" };

    /// <summary>
    /// Gets the ACME challenge solvers.
    /// </summary>
    [YamlMember(Alias = "solvers")]
    public List<AcmeSolver> Solvers { get; } = [];
}

/// <summary>
/// Secret key reference.
/// </summary>
[YamlSerializable]
public sealed class SecretKeyRef
{
    /// <summary>
    /// Gets or sets the secret name.
    /// </summary>
    [YamlMember(Alias = "name")]
    public string Name { get; set; } = default!;
}

/// <summary>
/// ACME challenge solver.
/// </summary>
[YamlSerializable]
public sealed class AcmeSolver
{
    /// <summary>
    /// Gets or sets the HTTP-01 challenge configuration.
    /// </summary>
    [YamlMember(Alias = "http01")]
    public Http01Solver? Http01 { get; set; }
}

/// <summary>
/// HTTP-01 challenge solver.
/// </summary>
[YamlSerializable]
public sealed class Http01Solver
{
    /// <summary>
    /// Gets or sets the ingress configuration for the solver.
    /// </summary>
    [YamlMember(Alias = "ingress")]
    public Http01IngressSolver Ingress { get; set; } = new();
}

/// <summary>
/// HTTP-01 ingress solver configuration.
/// </summary>
[YamlSerializable]
public sealed class Http01IngressSolver
{
    /// <summary>
    /// Gets or sets the ingress class name.
    /// </summary>
    [YamlMember(Alias = "ingressClassName")]
    public string? IngressClassName { get; set; }
}
