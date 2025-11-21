// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting;

/// <summary>
/// A resource that represents a Kafka UI container.
/// </summary>
/// <param name="name"></param>
public sealed class KafkaSchemaRegistryResource(string name) : ContainerResource(name), IResourceWithConnectionString
{

    // This endpoint is used for host processes Kafka schema registry communication.
    private const string PrimaryEndpointName = "primary";
    private EndpointReference? _primaryEndpoint;

    /// <summary>
    /// Gets the primary endpoint for the Kafka schema registry
    /// </summary>
    public EndpointReference PrimaryEndpoint => _primaryEndpoint ??= new(this, PrimaryEndpointName);

    /// <summary>
    /// Gets the connection string expression for the Kafka schema registry.
    /// </summary>
    public ReferenceExpression ConnectionStringExpression =>
        ReferenceExpression.Create($"http://{PrimaryEndpoint.Property(EndpointProperty.HostAndPort)}");
}
