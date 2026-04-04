// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Globalization;

namespace Aspire.Hosting.ApplicationModel;

/// <summary>
/// Represents an endpoint reference for a resource with endpoints.
/// </summary>
[AspireExport(ExposeProperties = true, ExposeMethods = true)]
[DebuggerDisplay("Resource = {Resource.Name}, EndpointName = {EndpointName}, IsAllocated = {IsAllocated}")]
public sealed class EndpointReference : IExpressionValue, IManifestExpressionProvider, IValueProvider, IValueWithReferences
{
    // A reference to the endpoint annotation if it exists.
    private EndpointAnnotation? _endpointAnnotation;
    private bool? _isAllocated;
    private readonly NetworkIdentifier? _contextNetworkID;

    /// <summary>
    /// Gets the endpoint annotation associated with the endpoint reference.
    /// </summary>
    public EndpointAnnotation EndpointAnnotation => GetEndpointAnnotation() ?? throw new InvalidOperationException(ErrorMessage ?? $"The endpoint `{EndpointName}` is not defined for the resource `{Resource.Name}`.");

    /// <summary>
    /// Gets the resource owner of the endpoint reference.
    /// </summary>
    public IResourceWithEndpoints Resource { get; }

    IEnumerable<object> IValueWithReferences.References => [Resource];

    /// <summary>
    /// Gets the name of the endpoint associated with the endpoint reference.
    /// </summary>
    public string EndpointName { get; }

    /// <summary>
    /// Gets or sets a custom error message to be thrown when the endpoint annotation is not found.
    /// </summary>
    public string? ErrorMessage { get; init; }

    /// <summary>
    /// Gets a value indicating whether the endpoint is allocated.
    /// </summary>
    public bool IsAllocated => _isAllocated ??= GetAllocatedEndpoint() is not null;

    /// <summary>
    /// Gets a value indicating whether the endpoint exists.
    /// </summary>
    public bool Exists => GetEndpointAnnotation() is not null;

    /// <summary>
    /// Gets a value indicating whether the endpoint uses HTTP scheme.
    /// </summary>
    public bool IsHttp => string.Equals(Scheme, "http", StringComparisons.EndpointAnnotationUriScheme);

    /// <summary>
    /// Gets a value indicating whether the endpoint uses HTTPS scheme.
    /// </summary>
    public bool IsHttps => string.Equals(Scheme, "https", StringComparisons.EndpointAnnotationUriScheme);

    /// <summary>
    /// Gets a value indicating whether TLS is enabled for this endpoint.
    /// </summary>
    /// <remarks>
    /// Returns <see langword="false"/> if the endpoint annotation has not been added to the resource yet.
    /// Once the annotation exists, this property delegates to <see cref="EndpointAnnotation.TlsEnabled"/>.
    /// </remarks>
    public bool TlsEnabled => Exists && EndpointAnnotation.TlsEnabled;

    /// <summary>
    /// Gets a value indicating whether this endpoint is excluded from the default set when referencing the resource's endpoints.
    /// </summary>
    /// <remarks>
    /// Returns <see langword="false"/> if the endpoint annotation has not been added to the resource yet.
    /// Once the annotation exists, this property delegates to <see cref="EndpointAnnotation.ExcludeReferenceEndpoint"/>.
    /// </remarks>
    public bool ExcludeReferenceEndpoint => Exists && EndpointAnnotation.ExcludeReferenceEndpoint;

    string IManifestExpressionProvider.ValueExpression => GetExpression();

    /// <summary>
    /// Gets the URL of the endpoint asynchronously. Waits for the endpoint to be allocated if necessary.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The URL of the endpoint.</returns>
    [AspireExport("getValueAsync", Description = "Gets the URL of the endpoint asynchronously")]
    public ValueTask<string?> GetValueAsync(CancellationToken cancellationToken = default) => Property(EndpointProperty.Url).GetValueAsync(cancellationToken);

    /// <summary>
    /// Gets the URL of the endpoint asynchronously. Waits for the endpoint to be allocated if necessary.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <param name="context">The context for value resolution.</param>
    /// <returns>The URL of the endpoint.</returns>
    [AspireExportIgnore]
    public ValueTask<string?> GetValueAsync(ValueProviderContext context, CancellationToken cancellationToken = default) => Property(EndpointProperty.Url).GetValueAsync(context, cancellationToken);

    /// <summary>
    /// The ID of the network that serves as the context for the EndpointReference.
    /// The reference will be resolved in the context of this network, which may be different
    /// from the network associated with the default network of the referenced Endpoint.
    /// </summary>
    public NetworkIdentifier? ContextNetworkID => _contextNetworkID;

    /// <summary>
    /// Gets the specified property expression of the endpoint. Defaults to the URL if no property is specified.
    /// </summary>
    internal string GetExpression(EndpointProperty property = EndpointProperty.Url)
    {
        return property switch
        {
            EndpointProperty.Url => Binding("url"),
            EndpointProperty.Host or EndpointProperty.IPV4Host => Binding("host"),
            EndpointProperty.Port => Binding("port"),
            EndpointProperty.Scheme => Binding("scheme"),
            EndpointProperty.TargetPort => Binding("targetPort"),
            EndpointProperty.HostAndPort => $"{Binding("host")}:{Binding("port")}",
            EndpointProperty.TlsEnabled => Binding("tlsEnabled"),
            _ => throw new InvalidOperationException($"The property '{property}' is not supported for the endpoint '{EndpointName}'.")
        };

        string Binding(string prop) => $"{{{Resource.Name}.bindings.{EndpointName}.{prop}}}";
    }

    /// <summary>
    /// Gets the specified property expression of the endpoint. Defaults to the URL if no property is specified.
    /// </summary>
    /// <param name="property">The <see cref="EndpointProperty"/> enum value to use in the reference.</param>
    /// <returns>An <see cref="EndpointReferenceExpression"/> representing the specified <see cref="EndpointProperty"/>.</returns>
    [AspireExportIgnore]
    public EndpointReferenceExpression Property(EndpointProperty property)
    {
        return new(this, property);
    }

    /// <summary>
    /// Creates a conditional <see cref="ReferenceExpression"/> that resolves to <paramref name="enabledValue"/> when
    /// <see cref="EndpointAnnotation.TlsEnabled"/> is <see langword="true"/> on this endpoint, or to
    /// <paramref name="disabledValue"/> otherwise.
    /// </summary>
    /// <remarks>
    /// The returned expression evaluates the TLS state lazily each time its value is resolved, making it
    /// safe to embed in a <see cref="ReferenceExpression"/> that is built before TLS is configured
    /// (e.g., before <c>BeforeStartEvent</c> fires). Because the condition and branches are declarative,
    /// polyglot code generators can translate this into native conditional constructs in any target language.
    /// </remarks>
    /// <param name="enabledValue">The expression to evaluate when TLS is enabled (e.g., <c>",ssl=true"</c>).</param>
    /// <param name="disabledValue">The expression to evaluate when TLS is not enabled.</param>
    /// <returns>A conditional <see cref="ReferenceExpression"/> whose value tracks the TLS state of this endpoint.</returns>
    [AspireExport(Description = "Gets a conditional expression that resolves to the enabledValue when TLS is enabled on the endpoint, or to the disabledValue otherwise.")]
    public ReferenceExpression GetTlsValue(ReferenceExpression enabledValue, ReferenceExpression disabledValue)
    {
        return ReferenceExpression.CreateConditional(
            Property(EndpointProperty.TlsEnabled),
            bool.TrueString,
            enabledValue,
            disabledValue);
    }

    /// <summary>
    /// Gets the port for this endpoint.
    /// </summary>
    public int Port => AllocatedEndpoint.Port;

    /// <summary>
    /// Gets the target port for this endpoint. If the port is dynamically allocated, this will return <see langword="null"/>.
    /// </summary>
    public int? TargetPort => EndpointAnnotation.TargetPort;

    /// <summary>
    /// Gets the host for this endpoint.
    /// </summary>
    public string Host => AllocatedEndpoint.Address ?? KnownHostNames.Localhost;

    /// <summary>
    /// Gets the scheme for this endpoint.
    /// </summary>
    public string Scheme => EndpointAnnotation.UriScheme;

    /// <summary>
    /// Gets the URL for this endpoint.
    /// </summary>
    public string Url => AllocatedEndpoint.UriString;

#pragma warning disable CS0618 // Type or member is obsolete
    internal ValueSnapshot<AllocatedEndpoint> AllocatedEndpointSnapshot =>
        EndpointAnnotation.AllocatedEndpointSnapshot;
#pragma warning restore CS0618 // Type or member is obsolete

    internal AllocatedEndpoint AllocatedEndpoint =>
        GetAllocatedEndpoint()
        ?? throw new InvalidOperationException($"The endpoint `{EndpointName}` is not allocated for the resource `{Resource.Name}`.");

    private EndpointAnnotation? GetEndpointAnnotation()
    {
        if (_endpointAnnotation is not null)
        {
            return _endpointAnnotation;
        }

        _endpointAnnotation ??= Resource.Annotations.OfType<EndpointAnnotation>()
            .SingleOrDefault(a => string.Equals(a.Name, EndpointName, StringComparisons.EndpointAnnotationName));
        return _endpointAnnotation;
    }

    private AllocatedEndpoint? GetAllocatedEndpoint()
    {
        var endpointAnnotation = GetEndpointAnnotation();
        if (endpointAnnotation is null)
        {
            return null;
        }

        foreach (var nes in endpointAnnotation.AllAllocatedEndpoints)
        {
            if (string.Equals(nes.NetworkID.Value, (_contextNetworkID ?? KnownNetworkIdentifiers.LocalhostNetwork).Value, StringComparisons.NetworkID))
            {
                if (!nes.Snapshot.IsValueSet)
                {
                    continue;
                }

                return nes.Snapshot.GetValueAsync().GetAwaiter().GetResult();
            }
        }

        return null;
    }

    /// <summary>
    /// Creates a new instance of <see cref="EndpointReference"/> with the specified endpoint name.
    /// </summary>
    /// <param name="owner">The resource with endpoints that owns the referenced endpoint.</param>
    /// <param name="endpoint">The endpoint annotation.</param>
    /// <param name="contextNetworkID">The ID of the network that serves as the context for the EndpointReference.</param>
    /// <remarks>
    /// Most Aspire resources are accessed in the context of the "localhost" network (host processes calling other host processes,
    /// or host processes calling container via mapped ports). If a <see cref="NetworkIdentifier"/> is specified, the <see cref="EndpointReference"/>
    /// will always resolve in the context of that network. If the <see cref="NetworkIdentifier"/> is null, the reference will attempt to resolve itself
    /// based on the context of the requesting resource.
    /// </remarks>
    public EndpointReference(IResourceWithEndpoints owner, EndpointAnnotation endpoint, NetworkIdentifier? contextNetworkID)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(endpoint);

        Resource = owner;
        EndpointName = endpoint.Name;
        _endpointAnnotation = endpoint;
        _contextNetworkID = contextNetworkID;
    }

    /// <summary>
    /// Creates a new instance of <see cref="EndpointReference"/> with the specified endpoint name.
    /// </summary>
    /// <param name="owner">The resource with endpoints that owns the referenced endpoint.</param>
    /// <param name="endpoint">The endpoint annotation.</param>
    public EndpointReference(IResourceWithEndpoints owner, EndpointAnnotation endpoint): this(owner, endpoint, null)
    {
    }

    /// <summary>
    /// Creates a new instance of <see cref="EndpointReference"/> with the specified endpoint name.
    /// </summary>
    /// <param name="owner">The resource with endpoints that owns the referenced endpoint.</param>
    /// <param name="endpointName">The name of the endpoint.</param>
    /// <param name="contextNetworkID">The ID of the network that serves as the context for the EndpointReference.</param>
    /// <remarks>
    /// Most Aspire resources are accessed in the context of the "localhost" network (host proceses calling other host processes,
    /// or host processes calling container via mapped ports). This is why EndpointReference assumes this
    /// context unless specified otherwise. However, for container-to-container, or container-to-host communication,
    /// you must specify a container network context for the EndpointReference to be resolved correctly.
    /// </remarks>
    public EndpointReference(IResourceWithEndpoints owner, string endpointName, NetworkIdentifier? contextNetworkID = null)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(endpointName);

        Resource = owner;
        EndpointName = endpointName;
        _contextNetworkID = contextNetworkID;
    }

    /// <summary>
    /// Creates a new instance of <see cref="EndpointReference"/> with the specified endpoint name.
    /// </summary>
    /// <param name="owner">The resource with endpoints that owns the referenced endpoint.</param>
    /// <param name="endpointName">The name of the endpoint.</param>
    public EndpointReference(IResourceWithEndpoints owner, string endpointName): this(owner, endpointName, null)
    {
    }
}

/// <summary>
/// Represents a property expression for an endpoint reference.
/// </summary>
/// <param name="endpointReference">The endpoint reference.</param>
/// <param name="property">The property of the endpoint.</param>
[AspireExport(ExposeProperties = true)]
[DebuggerDisplay("EndpointExpression = {ValueExpression}, Property = {Property}, Endpoint = {Endpoint.EndpointName}")]
public class EndpointReferenceExpression(EndpointReference endpointReference, EndpointProperty property) : IExpressionValue, IManifestExpressionProvider, IValueProvider, IValueWithReferences
{
    /// <summary>
    /// Gets the <see cref="EndpointReference"/>.
    /// </summary>
    public EndpointReference Endpoint { get; } = endpointReference ?? throw new ArgumentNullException(nameof(endpointReference));

    /// <summary>
    /// Gets the <see cref="EndpointProperty"/> for the property expression.
    /// </summary>
    public EndpointProperty Property { get; } = property;

    /// <summary>
    /// Gets the expression of the property of the endpoint.
    /// </summary>
    public string ValueExpression =>
        Endpoint.GetExpression(Property);

    /// <summary>
    /// Gets the value of the property of the endpoint.
    /// </summary>
    /// <param name="cancellationToken">A <see cref="CancellationToken"/>.</param>
    /// <returns>A <see cref="string"/> containing the selected <see cref="EndpointProperty"/> value.</returns>
    /// <exception cref="InvalidOperationException">Throws when the selected <see cref="EndpointProperty"/> enumeration is not known.</exception>
    public ValueTask<string?> GetValueAsync(CancellationToken cancellationToken = default)
    {
        return GetValueAsync(new(), cancellationToken);
    }

    /// <summary>
    /// Gets the value of the property of the endpoint.
    /// </summary>
    /// <param name="context">The context to use when resolving the endpoint property.</param>
    /// <param name="cancellationToken">A <see cref="CancellationToken"/>.</param>
    /// <returns>A <see cref="string"/> containing the selected <see cref="EndpointProperty"/> value.</returns>
    /// <exception cref="InvalidOperationException">Throws when the selected <see cref="EndpointProperty"/> enumeration is not known.</exception>
    public async ValueTask<string?> GetValueAsync(ValueProviderContext context, CancellationToken cancellationToken = default)
    {
        // If the EndpointReference was for a specific network context, then use that. Otherwise, use the network context from the ValueProviderContext.
        // This allows the EndpointReference to be resolved in the context of the caller's network if it was not explicitly set.
        var networkContext = Endpoint.ContextNetworkID ?? context.GetNetworkIdentifier();

        return Property switch
        {
            EndpointProperty.Scheme => new(Endpoint.Scheme),
            EndpointProperty.TlsEnabled => Endpoint.TlsEnabled ? bool.TrueString : bool.FalseString,
            EndpointProperty.IPV4Host when networkContext == KnownNetworkIdentifiers.LocalhostNetwork => "127.0.0.1",
            EndpointProperty.TargetPort when Endpoint.TargetPort is int port => new(port.ToString(CultureInfo.InvariantCulture)),
            _ => await ResolveValueWithAllocatedAddress().ConfigureAwait(false)
        };

        async ValueTask<string?> ResolveValueWithAllocatedAddress()
        {
            var endpointSnapshots = Endpoint.EndpointAnnotation.AllAllocatedEndpoints;
            var allocatedEndpoint = await endpointSnapshots.GetAllocatedEndpointAsync(networkContext, cancellationToken).ConfigureAwait(false);

            return Property switch
            {
                EndpointProperty.Url => new(allocatedEndpoint.UriString),
                EndpointProperty.Host => new(allocatedEndpoint.Address),
                EndpointProperty.IPV4Host => new(allocatedEndpoint.Address),
                EndpointProperty.Port => new(allocatedEndpoint.Port.ToString(CultureInfo.InvariantCulture)),
                EndpointProperty.TargetPort => new(ComputeTargetPort(allocatedEndpoint)),
                EndpointProperty.HostAndPort => new($"{allocatedEndpoint.Address}:{allocatedEndpoint.Port.ToString(CultureInfo.InvariantCulture)}"),
                _ => throw new InvalidOperationException($"The property '{Property}' is not supported for the endpoint '{Endpoint.EndpointName}'.")
            };
        }
    }

    private static string? ComputeTargetPort(AllocatedEndpoint allocatedEndpoint)
    {
        // There is no way to resolve the value of the target port until runtime. Even then, replicas make this very complex because
        // the target port is not known until the replica is allocated.
        // Instead, we return an expression that will be resolved at runtime by the orchestrator.
        return allocatedEndpoint.TargetPortExpression
            ?? throw new InvalidOperationException("The endpoint does not have an associated TargetPortExpression from the orchestrator.");
    }

    IEnumerable<object> IValueWithReferences.References => [Endpoint];
}

/// <summary>
/// Represents the properties of an endpoint that can be referenced.
/// </summary>
public enum EndpointProperty
{
    /// <summary>
    /// The entire URL of the endpoint.
    /// </summary>
    Url,
    /// <summary>
    /// The host of the endpoint.
    /// </summary>
    Host,
    /// <summary>
    /// The IPv4 address of the endpoint.
    /// </summary>
    IPV4Host,
    /// <summary>
    /// The port of the endpoint.
    /// </summary>
    Port,
    /// <summary>
    /// The scheme of the endpoint.
    /// </summary>
    Scheme,
    /// <summary>
    /// The target port of the endpoint.
    /// </summary>
    TargetPort,

    /// <summary>
    /// The host and port of the endpoint in the format `{Host}:{Port}`.
    /// </summary>
    HostAndPort,

    /// <summary>
    /// Whether TLS is enabled on the endpoint. Returns <see cref="bool.TrueString"/> or <see cref="bool.FalseString"/>.
    /// </summary>
    TlsEnabled
}
