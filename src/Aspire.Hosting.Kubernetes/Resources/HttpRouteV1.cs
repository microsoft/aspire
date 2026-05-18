// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using YamlDotNet.Serialization;

namespace Aspire.Hosting.Kubernetes.Resources;

/// <summary>
/// Represents an HTTPRoute resource in Kubernetes (gateway.networking.k8s.io/v1).
/// HTTPRoute defines HTTP routing rules that attach to a Gateway.
/// </summary>
[YamlSerializable]
public sealed class HttpRouteV1() : BaseKubernetesResource("gateway.networking.k8s.io/v1", "HTTPRoute")
{
    /// <summary>
    /// Gets or sets the specification of the HTTPRoute resource.
    /// </summary>
    [YamlMember(Alias = "spec")]
    public HttpRouteSpecV1 Spec { get; set; } = new();
}

/// <summary>
/// Represents the specification of an HTTPRoute resource.
/// </summary>
[YamlSerializable]
public sealed class HttpRouteSpecV1
{
    /// <summary>
    /// Gets the parent references that this route attaches to (typically Gateway resources).
    /// </summary>
    [YamlMember(Alias = "parentRefs")]
    public List<HttpRouteParentRefV1> ParentRefs { get; } = [];

    /// <summary>
    /// Gets the hostnames that this route matches. If empty, matches all hostnames.
    /// </summary>
    [YamlMember(Alias = "hostnames")]
    public List<string> Hostnames { get; } = [];

    /// <summary>
    /// Gets the routing rules for this HTTPRoute.
    /// </summary>
    [YamlMember(Alias = "rules")]
    public List<HttpRouteRuleV1> Rules { get; } = [];
}

/// <summary>
/// A reference to a parent resource (typically a Gateway) that an HTTPRoute attaches to.
/// </summary>
[YamlSerializable]
public sealed class HttpRouteParentRefV1
{
    /// <summary>
    /// Gets or sets the name of the parent Gateway resource.
    /// </summary>
    [YamlMember(Alias = "name")]
    public string Name { get; set; } = null!;

    /// <summary>
    /// Gets or sets the optional listener section the route binds to. When set,
    /// the route only attaches to listeners with a matching <c>name</c>. Used to
    /// bind the HTTP→HTTPS redirect route to the <c>http</c> listener only without
    /// also attaching it to the <c>https</c> listener (which would cause an infinite
    /// redirect loop).
    /// </summary>
    [YamlMember(Alias = "sectionName")]
    public string? SectionName { get; set; }
}

/// <summary>
/// A single routing rule in an HTTPRoute. Each rule matches requests and forwards
/// them to one or more backend services, optionally applying filters along the way.
/// </summary>
[YamlSerializable]
public sealed class HttpRouteRuleV1
{
    /// <summary>
    /// Gets the match conditions for this rule. A request must satisfy all conditions
    /// in at least one match to be routed by this rule.
    /// </summary>
    [YamlMember(Alias = "matches")]
    public List<HttpRouteMatchV1> Matches { get; } = [];

    /// <summary>
    /// Gets the filters applied to matched requests/responses. Common filters include
    /// <c>RequestRedirect</c> (used to redirect HTTP→HTTPS) and <c>ResponseHeaderModifier</c>
    /// (used to add HSTS headers).
    /// </summary>
    [YamlMember(Alias = "filters")]
    public List<HttpRouteFilterV1> Filters { get; } = [];

    /// <summary>
    /// Gets the backend references that matched requests are forwarded to.
    /// </summary>
    [YamlMember(Alias = "backendRefs")]
    public List<HttpRouteBackendRefV1> BackendRefs { get; } = [];
}

/// <summary>
/// Defines match conditions for an HTTPRoute rule.
/// </summary>
[YamlSerializable]
public sealed class HttpRouteMatchV1
{
    /// <summary>
    /// Gets or sets the path match condition.
    /// </summary>
    [YamlMember(Alias = "path")]
    public HttpRoutePathMatchV1? Path { get; set; }

    /// <summary>
    /// Gets the header match conditions.
    /// </summary>
    [YamlMember(Alias = "headers")]
    public List<HttpRouteHeaderMatchV1> Headers { get; } = [];
}

/// <summary>
/// Defines a path match condition for an HTTPRoute rule.
/// </summary>
[YamlSerializable]
public sealed class HttpRoutePathMatchV1
{
    /// <summary>
    /// Gets or sets the type of path matching. Values: <c>"PathPrefix"</c>, <c>"Exact"</c>.
    /// </summary>
    [YamlMember(Alias = "type")]
    public string Type { get; set; } = "PathPrefix";

    /// <summary>
    /// Gets or sets the path value to match.
    /// </summary>
    [YamlMember(Alias = "value")]
    public string Value { get; set; } = null!;
}

/// <summary>
/// Defines a header match condition for an HTTPRoute rule.
/// </summary>
[YamlSerializable]
public sealed class HttpRouteHeaderMatchV1
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
/// A reference to a backend service that receives matched traffic.
/// </summary>
[YamlSerializable]
public sealed class HttpRouteBackendRefV1
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
