// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using YamlDotNet.Serialization;

namespace Aspire.Hosting.Kubernetes.Resources;

/// <summary>
/// Represents a Gateway resource in Kubernetes (gateway.networking.k8s.io/v1).
/// </summary>
[YamlSerializable]
public sealed class GatewayV1() : BaseKubernetesResource("gateway.networking.k8s.io/v1", "Gateway")
{
    /// <summary>
    /// Gets or sets the specification of the Gateway resource.
    /// </summary>
    [YamlMember(Alias = "spec")]
    public GatewaySpecV1 Spec { get; set; } = new();
}

/// <summary>
/// Represents the specification of a Gateway resource.
/// </summary>
[YamlSerializable]
public sealed class GatewaySpecV1
{
    /// <summary>
    /// Gets or sets the name of the GatewayClass that this Gateway is associated with.
    /// </summary>
    [YamlMember(Alias = "gatewayClassName")]
    public string GatewayClassName { get; set; } = null!;

    /// <summary>
    /// Gets the listeners associated with this Gateway. Each listener defines a port,
    /// protocol, and optional TLS configuration.
    /// </summary>
    [YamlMember(Alias = "listeners")]
    public List<GatewayListenerV1> Listeners { get; } = [];
}

/// <summary>
/// Represents a listener on a Gateway. A listener defines how the Gateway receives traffic
/// on a specific port and protocol.
/// </summary>
[YamlSerializable]
public sealed class GatewayListenerV1
{
    /// <summary>
    /// Gets or sets the name of this listener. Must be unique within the Gateway.
    /// </summary>
    [YamlMember(Alias = "name")]
    public string Name { get; set; } = null!;

    /// <summary>
    /// Gets or sets the protocol for this listener (e.g., <c>"HTTP"</c>, <c>"HTTPS"</c>, <c>"TLS"</c>).
    /// </summary>
    [YamlMember(Alias = "protocol")]
    public string Protocol { get; set; } = null!;

    /// <summary>
    /// Gets or sets the network port for this listener.
    /// </summary>
    [YamlMember(Alias = "port")]
    public int Port { get; set; }

    /// <summary>
    /// Gets or sets the optional hostname for this listener. When set, only requests
    /// matching this hostname are handled by this listener.
    /// </summary>
    [YamlMember(Alias = "hostname")]
    public string? Hostname { get; set; }

    /// <summary>
    /// Gets or sets the TLS configuration for this listener. Required when protocol is HTTPS or TLS.
    /// </summary>
    [YamlMember(Alias = "tls")]
    public GatewayTlsConfigV1? Tls { get; set; }

    /// <summary>
    /// Gets or sets the allowed routes configuration for this listener.
    /// </summary>
    [YamlMember(Alias = "allowedRoutes")]
    public GatewayAllowedRoutesV1? AllowedRoutes { get; set; }
}

/// <summary>
/// TLS configuration for a Gateway listener.
/// </summary>
[YamlSerializable]
public sealed class GatewayTlsConfigV1
{
    /// <summary>
    /// Gets or sets the TLS mode. Common values are <c>"Terminate"</c> (Gateway decrypts)
    /// and <c>"Passthrough"</c> (backend decrypts).
    /// </summary>
    [YamlMember(Alias = "mode")]
    public string Mode { get; set; } = "Terminate";

    /// <summary>
    /// Gets the certificate references for TLS termination.
    /// </summary>
    [YamlMember(Alias = "certificateRefs")]
    public List<GatewayCertificateRefV1> CertificateRefs { get; } = [];
}

/// <summary>
/// A reference to a TLS certificate stored as a Kubernetes Secret.
/// </summary>
[YamlSerializable]
public sealed class GatewayCertificateRefV1
{
    /// <summary>
    /// Gets or sets the name of the Kubernetes Secret containing the TLS certificate.
    /// </summary>
    [YamlMember(Alias = "name")]
    public string Name { get; set; } = null!;
}

/// <summary>
/// Defines which routes are allowed to attach to a Gateway listener.
/// </summary>
[YamlSerializable]
public sealed class GatewayAllowedRoutesV1
{
    /// <summary>
    /// Gets or sets the namespace selector for allowed routes.
    /// </summary>
    [YamlMember(Alias = "namespaces")]
    public GatewayRouteNamespacesV1? Namespaces { get; set; }
}

/// <summary>
/// Defines which namespaces routes can come from.
/// </summary>
[YamlSerializable]
public sealed class GatewayRouteNamespacesV1
{
    /// <summary>
    /// Gets or sets the namespace selection policy. Values: <c>"Same"</c>, <c>"All"</c>, <c>"Selector"</c>.
    /// </summary>
    [YamlMember(Alias = "from")]
    public string From { get; set; } = "Same";
}
