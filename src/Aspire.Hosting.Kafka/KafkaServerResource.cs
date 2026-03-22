// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting;

/// <summary>
/// A resource that represents a Kafka broker.
/// </summary>
/// <param name="name">The name of the resource.</param>
[AspireExport(ExposeProperties = true)]
public class KafkaServerResource(string name) : ContainerResource(name), IResourceWithConnectionString, IResourceWithEnvironment
{
    // This endpoint is used for host processes Kafka broker communication.
    internal const string PrimaryEndpointName = "tcp";
    // This endpoint is used for container to broker communication.
    internal const string InternalEndpointName = "internal";

    private EndpointReference? _primaryEndpoint;
    private EndpointReference? _internalEndpoint;

    /// <summary>
    /// Gets the primary endpoint for the Kafka broker. This endpoint is used for host processes to Kafka broker communication.
    /// To connect to the Kafka broker from a container, use <see cref="InternalEndpoint"/>.
    /// </summary>
    public EndpointReference PrimaryEndpoint => _primaryEndpoint ??= new(this, PrimaryEndpointName, KnownNetworkIdentifiers.LocalhostNetwork);

    /// <summary>
    /// Gets the host endpoint reference for the primary endpoint.
    /// </summary>
    public EndpointReferenceExpression Host => PrimaryEndpoint.Property(EndpointProperty.Host);

    /// <summary>
    /// Gets the port endpoint reference for the primary endpoint.
    /// </summary>
    public EndpointReferenceExpression Port => PrimaryEndpoint.Property(EndpointProperty.Port);

    /// <summary>
    /// Gets the internal endpoint for the Kafka broker. This endpoint is used for container to broker communication.
    /// To connect to the Kafka broker from a host process, use <see cref="PrimaryEndpoint"/>.
    /// </summary>
    public EndpointReference InternalEndpoint => _internalEndpoint ??= new(this, InternalEndpointName, KnownNetworkIdentifiers.DefaultAspireContainerNetwork);

    /// <summary>
    /// Gets the connection string expression for the Kafka broker.
    /// </summary>
    public ReferenceExpression ConnectionStringExpression =>
       ReferenceExpression.Create($"{PrimaryEndpoint.Property(EndpointProperty.HostAndPort)}");

    IEnumerable<KeyValuePair<string, ReferenceExpression>> IResourceWithConnectionString.GetConnectionProperties()
    {
        yield return new("Host", ReferenceExpression.Create($"{Host}"));
        yield return new("Port", ReferenceExpression.Create($"{Port}"));
    }
}
