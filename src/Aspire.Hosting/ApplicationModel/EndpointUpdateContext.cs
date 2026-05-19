// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Net.Sockets;

namespace Aspire.Hosting.ApplicationModel;

/// <summary>
/// Provides a mutable callback context for updating an endpoint in polyglot app hosts.
/// </summary>
[AspireExport(ExposeProperties = true)]
internal sealed class EndpointUpdateContext(EndpointAnnotation endpointAnnotation)
{
    private readonly EndpointAnnotation _endpointAnnotation = endpointAnnotation ?? throw new ArgumentNullException(nameof(endpointAnnotation));

    /// <summary>
    /// Gets the endpoint name.
    /// </summary>
    public string Name => _endpointAnnotation.Name;

    /// <summary>
    /// Gets or sets the network protocol.
    /// </summary>
    public ProtocolType Protocol
    {
        get => _endpointAnnotation.Protocol;
        set => _endpointAnnotation.Protocol = value;
    }

    /// <summary>
    /// Gets or sets the desired host port.
    /// </summary>
    public int? Port
    {
        get => _endpointAnnotation.Port;
        set => _endpointAnnotation.Port = value;
    }

    /// <summary>
    /// Gets or sets the target port.
    /// </summary>
    public int? TargetPort
    {
        get => _endpointAnnotation.TargetPort;
        set => _endpointAnnotation.TargetPort = value;
    }

    /// <summary>
    /// Gets or sets the URI scheme.
    /// </summary>
    public string UriScheme
    {
        get => _endpointAnnotation.UriScheme;
        set => _endpointAnnotation.UriScheme = value;
    }

    /// <summary>
    /// Gets or sets the target host.
    /// </summary>
    public string TargetHost
    {
        get => _endpointAnnotation.TargetHost;
        set => _endpointAnnotation.TargetHost = value;
    }

    /// <summary>
    /// Gets or sets the transport.
    /// </summary>
    public string Transport
    {
        get => _endpointAnnotation.Transport;
        set => _endpointAnnotation.Transport = value;
    }

    /// <summary>
    /// Gets or sets a value indicating whether the endpoint is external.
    /// </summary>
    public bool IsExternal
    {
        get => _endpointAnnotation.IsExternal;
        set => _endpointAnnotation.IsExternal = value;
    }

    /// <summary>
    /// Gets or sets a value indicating whether the endpoint is proxied.
    /// </summary>
    public bool IsProxied
    {
        get => _endpointAnnotation.IsProxied;
        set => _endpointAnnotation.IsProxied = value;
    }

    /// <summary>
    /// Gets or sets a value indicating whether the endpoint is excluded from the default reference set.
    /// </summary>
    public bool ExcludeReferenceEndpoint
    {
        get => _endpointAnnotation.ExcludeReferenceEndpoint;
        set => _endpointAnnotation.ExcludeReferenceEndpoint = value;
    }

    /// <summary>
    /// Gets or sets a value indicating whether TLS is enabled.
    /// </summary>
    public bool TlsEnabled
    {
        get => _endpointAnnotation.TlsEnabled;
        set => _endpointAnnotation.TlsEnabled = value;
    }
}
