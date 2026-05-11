// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.Kubernetes;

/// <summary>
/// Configuration options for a <see cref="K3sClusterResource"/>.
/// </summary>
/// <remarks>
/// Mirrors the structure of a Kind cluster config YAML and YARP-style options builders.
/// Each property translates to one or more <c>k3s server</c> command-line arguments.
/// </remarks>
public sealed class K3sClusterOptions
{
    /// <summary>
    /// Additional Subject Alternative Names added to the k3s TLS certificate.
    /// Defaults to <c>host.docker.internal</c>, <c>localhost</c>, and <c>0.0.0.0</c>
    /// so the kubeconfig is valid from both the host and other containers.
    /// Maps to <c>--tls-san</c>.
    /// </summary>
    public List<string> TlsSans { get; } = ["host.docker.internal", "localhost", "0.0.0.0"];

    /// <summary>
    /// k3s built-in components to disable at startup.
    /// Defaults to <c>traefik</c> and <c>servicelb</c> to keep the cluster lightweight.
    /// Maps to one <c>--disable=X</c> argument per entry.
    /// </summary>
    public List<string> DisabledComponents { get; } = ["traefik", "servicelb"];

    /// <summary>
    /// Raw additional arguments appended to the <c>k3s server</c> command after all derived arguments.
    /// </summary>
    public List<string> ExtraArgs { get; } = [];

    /// <summary>
    /// CIDR range for pod networking. Maps to <c>--cluster-cidr</c>.
    /// Equivalent to Kind's <c>networking.podSubnet</c>.
    /// </summary>
    public string? PodSubnet { get; set; }

    /// <summary>
    /// CIDR range for service IPs. Maps to <c>--service-cidr</c>.
    /// Equivalent to Kind's <c>networking.serviceSubnet</c>.
    /// </summary>
    public string? ServiceSubnet { get; set; }

    /// <summary>
    /// The <c>rancher/k3s</c> image tag to use.
    /// Defaults to <c>latest</c>; pin to a specific version for reproducible environments.
    /// </summary>
    public string ImageTag { get; set; } = K3sContainerImageTags.Tag;
}
