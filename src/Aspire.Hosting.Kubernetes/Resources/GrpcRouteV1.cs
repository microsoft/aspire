// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using YamlDotNet.Serialization;

namespace Aspire.Hosting.Kubernetes.Resources;

/// <summary>
/// Represents a GRPCRoute resource in Kubernetes (gateway.networking.k8s.io/v1).
/// GRPCRoute defines gRPC routing rules, matched by gRPC service and method, that attach to a Gateway.
/// </summary>
/// <remarks>
/// See <see href="https://gateway-api.sigs.k8s.io/reference/api-types/grpcroute/"/> for the full specification.
/// </remarks>
[YamlSerializable]
public sealed class GrpcRouteV1() : BaseKubernetesResource("gateway.networking.k8s.io/v1", "GRPCRoute")
{
    /// <summary>
    /// Gets or sets the specification of the GRPCRoute resource.
    /// </summary>
    [YamlMember(Alias = "spec")]
    public GrpcRouteSpecV1 Spec { get; set; } = new();
}

/// <summary>
/// Represents the specification of a GRPCRoute resource.
/// </summary>
[YamlSerializable]
public sealed class GrpcRouteSpecV1
{
    /// <summary>
    /// Gets the parent references that this route attaches to (typically Gateway resources).
    /// </summary>
    [YamlMember(Alias = "parentRefs")]
    public List<GrpcRouteParentRefV1> ParentRefs { get; } = [];

    /// <summary>
    /// Gets the hostnames that this route matches against the gRPC <c>Host</c> header. If empty, matches all hostnames.
    /// </summary>
    [YamlMember(Alias = "hostnames")]
    public List<string> Hostnames { get; } = [];

    /// <summary>
    /// Gets the routing rules for this GRPCRoute.
    /// </summary>
    [YamlMember(Alias = "rules")]
    public List<GrpcRouteRuleV1> Rules { get; } = [];
}

/// <summary>
/// A reference to a parent resource (typically a Gateway) that a GRPCRoute attaches to.
/// </summary>
[YamlSerializable]
public sealed class GrpcRouteParentRefV1
{
    /// <summary>
    /// Gets or sets the name of the parent Gateway resource.
    /// </summary>
    [YamlMember(Alias = "name")]
    public string Name { get; set; } = null!;
}

/// <summary>
/// A single routing rule in a GRPCRoute.
/// Each rule matches gRPC requests and forwards them to one or more backend services.
/// </summary>
[YamlSerializable]
public sealed class GrpcRouteRuleV1
{
    /// <summary>
    /// Gets the match conditions for this rule. The rule matches when any one of the matches is satisfied.
    /// If empty, the rule matches every gRPC request.
    /// </summary>
    [YamlMember(Alias = "matches")]
    public List<GrpcRouteMatchV1> Matches { get; } = [];

    /// <summary>
    /// Gets the backend references that matched requests are forwarded to.
    /// </summary>
    [YamlMember(Alias = "backendRefs")]
    public List<GrpcRouteBackendRefV1> BackendRefs { get; } = [];
}

/// <summary>
/// Defines match conditions for a GRPCRoute rule. All conditions within a single match must be satisfied.
/// </summary>
[YamlSerializable]
public sealed class GrpcRouteMatchV1
{
    /// <summary>
    /// Gets or sets the gRPC service/method match condition. If not set, all services and methods match.
    /// </summary>
    [YamlMember(Alias = "method")]
    public GrpcMethodMatchV1? Method { get; set; }

    /// <summary>
    /// Gets the header match conditions.
    /// </summary>
    [YamlMember(Alias = "headers")]
    public List<GrpcHeaderMatchV1> Headers { get; } = [];
}

/// <summary>
/// Defines a gRPC service/method match condition for a GRPCRoute rule.
/// At least one of <see cref="Service"/> or <see cref="Method"/> must be set.
/// </summary>
[YamlSerializable]
public sealed class GrpcMethodMatchV1
{
    /// <summary>
    /// Gets or sets the type of matching. Values: <c>"Exact"</c>, <c>"RegularExpression"</c>.
    /// </summary>
    [YamlMember(Alias = "type")]
    public string Type { get; set; } = "Exact";

    /// <summary>
    /// Gets or sets the fully qualified gRPC service name to match.
    /// If not set, matches any service.
    /// </summary>
    [YamlMember(Alias = "service")]
    public string? Service { get; set; }

    /// <summary>
    /// Gets or sets the gRPC method name to match. If not set, matches any method.
    /// </summary>
    [YamlMember(Alias = "method")]
    public string? Method { get; set; }
}

/// <summary>
/// Defines a header match condition for a GRPCRoute rule.
/// </summary>
[YamlSerializable]
public sealed class GrpcHeaderMatchV1
{
    /// <summary>
    /// Gets or sets the match type. Values: <c>"Exact"</c>, <c>"RegularExpression"</c>.
    /// </summary>
    [YamlMember(Alias = "type")]
    public string Type { get; set; } = "Exact";

    /// <summary>
    /// Gets or sets the header name.
    /// </summary>
    [YamlMember(Alias = "name")]
    public string Name { get; set; } = null!;

    /// <summary>
    /// Gets or sets the header value to match.
    /// </summary>
    [YamlMember(Alias = "value")]
    public string Value { get; set; } = null!;
}

/// <summary>
/// A reference to a backend service that receives matched gRPC traffic.
/// </summary>
[YamlSerializable]
public sealed class GrpcRouteBackendRefV1
{
    /// <summary>
    /// Gets or sets the name of the Kubernetes Service.
    /// </summary>
    [YamlMember(Alias = "name")]
    public string Name { get; set; } = null!;

    /// <summary>
    /// Gets or sets the port number on the service.
    /// </summary>
    [YamlMember(Alias = "port")]
    public int Port { get; set; }
}
