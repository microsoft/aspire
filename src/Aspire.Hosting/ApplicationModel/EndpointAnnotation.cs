// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Collections;

namespace Aspire.Hosting.ApplicationModel;

/// <summary>
/// Represents an endpoint annotation that describes how a service should be bound to a network.
/// </summary>
/// <remarks>
/// This class is used to specify the network protocol, port, URI scheme, transport, and other details for a service.
/// </remarks>
[DebuggerDisplay("Type = {GetType().Name,nq}, Name = {Name}")]
public sealed class EndpointAnnotation : IResourceAnnotation
{
    private string? _transport;
    private int? _port;
    private bool _portSetToNull;
    private int? _targetPort;
    private bool _targetPortSetToNull;
    private bool? _tlsEnabled;
    private readonly NetworkIdentifier _networkID;

    /// <summary>
    /// Initializes a new instance of <see cref="EndpointAnnotation"/>.
    /// </summary>
    /// <param name="protocol">Network protocol: TCP or UDP are supported today, others possibly in future.</param>
    /// <param name="uriScheme">If a service is URI-addressable, this is the URI scheme to use for constructing service URI.</param>
    /// <param name="transport">Transport that is being used (e.g. http, http2, http3 etc).</param>
    /// <param name="name">Name of the service.</param>
    /// <param name="port">Desired port for the service.</param>
    /// <param name="targetPort">This is the port the resource is listening on. If the endpoint is used for the container, it is the container port.</param>
    /// <param name="isExternal">Indicates that this endpoint should be exposed externally at publish time.</param>
    /// <param name="isProxied">Specifies if the endpoint will be proxied by DCP. Defaults to true.</param>
    public EndpointAnnotation(
        ProtocolType protocol,
        string? uriScheme = null,
        string? transport = null,
        [EndpointName] string? name = null,
        int? port = null,
        int? targetPort = null,
        bool? isExternal = null,
        bool isProxied = true
    ) : this(
        protocol,
        null,
        uriScheme,
        transport,
        name,
        port,
        targetPort,
        isExternal,
        isProxied
    )
    { }

    /// <summary>
    /// Initializes a new instance of <see cref="EndpointAnnotation"/>.
    /// </summary>
    /// <param name="protocol">Network protocol: TCP or UDP are supported today, others possibly in future.</param>
    /// <param name="networkID">The ID of the network that is the "default" network for the Endpoint.
    /// <param name="uriScheme">If a service is URI-addressable, this is the URI scheme to use for constructing service URI.</param>
    /// <param name="transport">Transport that is being used (e.g. http, http2, http3 etc).</param>
    /// <param name="name">Name of the service.</param>
    /// <param name="port">Desired port for the service.</param>
    /// <param name="targetPort">This is the port the resource is listening on. If the endpoint is used for the container, it is the container port.</param>
    /// <param name="isExternal">Indicates that this endpoint should be exposed externally at publish time.</param>
    /// <param name="isProxied">Specifies if the endpoint will be proxied by DCP. Defaults to true.</param>
    /// Clients connected to the same network can reach the endpoint without any routing or network address translation.</param>
    public EndpointAnnotation(
        ProtocolType protocol,
        NetworkIdentifier? networkID,
        string? uriScheme = null,
        string? transport = null,
        [EndpointName] string? name = null,
        int? port = null,
        int? targetPort = null,
        bool? isExternal = null,
        bool isProxied = true
    )
    {
        // If the URI scheme is null, we'll adopt either udp:// or tcp:// based on the
        // protocol. If the name is null, we'll use the URI scheme as the default. This
        // is because we eventually always need these values to be populated so lets do
        // it up front.

        uriScheme ??= protocol.ToString().ToLowerInvariant();
        name ??= uriScheme;

        ModelName.ValidateName(nameof(EndpointAnnotation), name);

        Protocol = protocol;
        UriScheme = uriScheme;
        _transport = transport;
        Name = name;
        _port = port;
        _targetPort = targetPort;
        IsExternal = isExternal ?? false;
        IsProxied = isProxied;
        _networkID = networkID ?? KnownNetworkIdentifiers.LocalhostNetwork;
#pragma warning disable CS0618 // Type or member is obsolete
        AllAllocatedEndpoints.TryAdd(_networkID, AllocatedEndpointSnapshot);
#pragma warning restore CS0618 // Type or member is obsolete
    }

    /// <summary>
    ///  Name of the service
    /// </summary>
    public string Name { get; set; }

    /// <summary>
    /// Network protocol: TCP or UDP are supported today, others possibly in future.
    /// </summary>
    public ProtocolType Protocol { get; set; }

    /// <summary>
    /// Desired port for the service
    /// </summary>
    public int? Port
    {
        // For proxy-less Endpoints the client port and target port should be the same.
        // Note that this is just a "sensible default"--the consumer of the EndpointAnnotation is free
        // to change Port and TargetPort after the annotation is created, but if the final values are inconsistent,
        // the associated resource may fail to run.
        // It also depends on what the EndpointAnnotation is applied to.
        // In the Container case the TargetPort is the port that the process listens on inside the container,
        //  and the Port is the host interface port, so it is fine for them to be different.
        get => _port ?? (IsProxied || _portSetToNull ? null : _targetPort);
        set
        {
            _port = value;
            _portSetToNull = value == null;
        }
    }

    /// <summary>
    /// This is the port the resource is listening on. If the endpoint is used for the container, it is the container port.
    /// </summary>
    /// <remarks>
    /// Defaults to <see cref="Port"/>.
    /// </remarks>
    public int? TargetPort
    {
        // See comment on the Port setter, as this is the reciprocal logic
        get => _targetPort ?? (IsProxied || _targetPortSetToNull ? null : _port);
        set
        {
            _targetPort = value;
            _targetPortSetToNull = value == null;
        }
    }

    /// <summary>
    /// If a service is URI-addressable, this property will contain the URI scheme to use for constructing service URI.
    /// </summary>
    public string UriScheme { get; set; }

    /// <summary>
    /// This is the address the resource is listening on. By default it is localhost.
    /// </summary>
    public string TargetHost { get; set; } = "localhost";

    /// <summary>
    /// Transport that is being used (e.g. http, http2, http3 etc).
    /// </summary>
    public string Transport
    {
        get => _transport ?? (string.Equals(UriScheme, "http", StringComparisons.EndpointAnnotationUriScheme) || string.Equals(UriScheme, "https", StringComparisons.EndpointAnnotationUriScheme) ? "http" : Protocol.ToString().ToLowerInvariant());
        set => _transport = value;
    }

    /// <summary>
    /// Indicates that this endpoint should be exposed externally at publish time.
    /// </summary>
    public bool IsExternal { get; set; }

    /// <summary>
    /// Indicates that this endpoint should be managed by DCP. This means it can be replicated and use a different port internally than the one publicly exposed.
    /// Setting to false means the endpoint will be handled and exposed by the resource.
    /// </summary>
    /// <remarks>Defaults to <c>true</c>.</remarks>
    public bool IsProxied { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether TLS is enabled for this endpoint.
    /// </summary>
    /// <remarks>
    /// This property is used to track TLS state on the endpoint so that connection string expressions
    /// can dynamically include TLS-related parameters (e.g., <c>ssl=true</c> for Redis) at resolution time
    /// rather than at expression build time. For HTTP-based endpoints, the <see cref="UriScheme"/> property
    /// being set to <c>https</c> already implies TLS. This property is primarily useful for non-HTTP protocols
    /// (e.g., Redis, databases) that need explicit TLS configuration in their connection strings.
    /// </remarks>
    public bool TlsEnabled
    {
        get => _tlsEnabled ?? string.Equals(UriScheme, "https", StringComparisons.EndpointAnnotationUriScheme);
        set => _tlsEnabled = value;
    }

    /// <summary>
    /// Gets or sets a value indicating whether the endpoint is from a launch profile.
    /// </summary>
    internal bool FromLaunchProfile { get; set; }

    /// <summary>
    /// The environment variable that contains the target port. Setting prevents a variable from flowing into ASPNETCORE_URLS for project resources.
    /// </summary>
    internal string? TargetPortEnvironmentVariable { get; set; }

    /// <summary>
    /// Gets the ID of the network that is the "default" network for the Endpoint (the one the Endpoint is associated with and can be reached without routing or network address translation).
    /// </summary>
    public NetworkIdentifier DefaultNetworkID => _networkID;

    /// <summary>
    /// Gets or sets the default <see cref="AllocatedEndpoint"/> for this Endpoint.
    /// </summary>
    public AllocatedEndpoint? AllocatedEndpoint
    {
#pragma warning disable CS0618 // Type or member is obsolete (AllocatedEndpointSnapshot)
        get
        {

            if (!AllocatedEndpointSnapshot.IsValueSet)
            {
                return null;
            }

            // This looks bad *BUT* we check if the value is set before resolving.
            // This preserves the semantics that if the value is not set, we return null to
            // the caller.
            return AllocatedEndpointSnapshot.GetValueAsync().GetAwaiter().GetResult();
        }
        set
        {
            if (value is null)
            {
                // Setting null will proactively set an exception on the snapshot.
                AllocatedEndpointSnapshot.SetException(new InvalidOperationException($"The endpoint `{Name}` is not allocated"));
            }
            else
            {
                if (_networkID != value.NetworkID)
                {
                    throw new InvalidOperationException($"The default AllocatedEndpoint's network ID must match the EndpointAnnotation network ID ('{_networkID}'). The attempted AllocatedEndpoint belongs to '{value.NetworkID}'.");
                }
                AllocatedEndpointSnapshot.SetValue(value);
            }
        }
#pragma warning restore CS0618 // Type or member is obsolete
    }

    /// <summary>
    /// Gets the <see cref="AllocatedEndpointSnapshot"/> for the default <see cref="AllocatedEndpoint"/>.
    /// </summary>
    [Obsolete("This property will be marked as internal in future Aspire release. Use AllocatedEndpoint and AllAllocatedEndpoints properties to access and change allocated endpoints associated with an EndpointAnnotation.")]
    public ValueSnapshot<AllocatedEndpoint> AllocatedEndpointSnapshot { get; } = new();

    /// <summary>
    /// Gets the list of all AllocatedEndpoints associated with this Endpoint.
    /// </summary>
    public NetworkEndpointSnapshotList AllAllocatedEndpoints { get; } = new();
}

/// <summary>
/// Represents an AllocatedEndpoint snapshot associated with a specific network.
/// </summary>
/// <param name="Snapshot">AllocatedEndpoint snapshot</param>
/// <param name="NetworkID">The ID of the network that is associated with the AllocatedEndpoint snapshot.</param>
[DebuggerDisplay("NetworkID = {NetworkID}, Endpoint = {Snapshot}")]
public record class NetworkEndpointSnapshot(ValueSnapshot<AllocatedEndpoint> Snapshot, NetworkIdentifier NetworkID);

/// <summary>
/// Holds a list of <see cref="NetworkEndpointSnapshot"/> for an Endpoint, providing thread-safe enumeration and addition.
/// </summary>
public class NetworkEndpointSnapshotList : IEnumerable<NetworkEndpointSnapshot>
{
    [DebuggerBrowsable(DebuggerBrowsableState.RootHidden)]
    private readonly ConcurrentBag<NetworkEndpointSnapshot> _snapshots = new();

    /// <summary>
    /// Provides a thread-safe enumerator over the network endpoint snapshots.
    /// </summary>
    public IEnumerator<NetworkEndpointSnapshot> GetEnumerator()
    {
        return _snapshots.GetEnumerator();
    }

    IEnumerator IEnumerable.GetEnumerator()
    {
        return GetEnumerator();
    }

    /// <summary>
    /// Adds an AllocatedEndpoint snapshot for a specific network if one does not already exist.
    /// </summary>
    [Obsolete("This method is for internal use only and will be marked internal in a future Aspire release. Use AddOrUpdateAllocatedEndpoint instead.")]
    public bool TryAdd(NetworkIdentifier networkID, ValueSnapshot<AllocatedEndpoint> snapshot)
    {
        lock (_snapshots)
        {
            if (_snapshots.Any(s => s.NetworkID.Equals(networkID)))
            {
                return false;
            }
            _snapshots.Add(new NetworkEndpointSnapshot(snapshot, networkID));
            return true;
        }
    }

    /// <summary>
    /// Adds or updates an AllocatedEndpoint value associated with a specific network in the snapshot list.
    /// </summary>
    public void AddOrUpdateAllocatedEndpoint(NetworkIdentifier networkID, AllocatedEndpoint endpoint)
    {
        if (endpoint.NetworkID != networkID)
        {
            throw new ArgumentException($"AllocatedEndpoint must use the same network as the {nameof(networkID)} parameter", nameof(endpoint));
        }
        var nes = GetSnapshotFor(networkID);
        nes.Snapshot.SetValue(endpoint);
    }

    /// <summary>
    /// Gets an AllocatedEndpoint for a given network ID, waiting for it to appear if it is not already present.
    /// </summary>
    public Task<AllocatedEndpoint> GetAllocatedEndpointAsync(NetworkIdentifier networkID, CancellationToken cancellationToken = default)
    {
        var nes = GetSnapshotFor(networkID);
        return nes.Snapshot.GetValueAsync(cancellationToken);
    }

    private NetworkEndpointSnapshot GetSnapshotFor(NetworkIdentifier networkID)
    {
        lock (_snapshots)
        {
            var nes = _snapshots.FirstOrDefault(s => s.NetworkID.Equals(networkID));
            if (nes is null)
            {
                nes = new NetworkEndpointSnapshot(new ValueSnapshot<AllocatedEndpoint>(), networkID);
                _snapshots.Add(nes);
            }
            return nes;
        }
    }

}
