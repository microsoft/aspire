// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting;

/// <summary>
/// A resource that represents a Kafka Schema Registry container.
/// </summary>
/// <param name="name">The name of the resource.</param>
/// <param name="kafkaServer">The Kafka server resource associated with this schema registry.</param>
public sealed class KafkaSchemaRegistryResource(string name, KafkaServerResource kafkaServer) : ContainerResource(name), IResourceWithConnectionString
{
    // This endpoint is used for host processes Kafka schema registry communication.
    internal const string PrimaryEndpointName = "primary";
    private EndpointReference? _primaryEndpoint;

    /// <summary>
    /// Gets the Kafka server resource associated with this schema registry.
    /// </summary>
    public KafkaServerResource KafkaServer { get; } = kafkaServer ?? throw new ArgumentNullException(nameof(kafkaServer));

    /// <summary>
    /// Gets the primary endpoint for the Kafka schema registry.
    /// </summary>
    public EndpointReference PrimaryEndpoint => _primaryEndpoint ??= new(this, PrimaryEndpointName);

    /// <summary>
    /// Gets the connection string expression for the Kafka schema registry.
    /// </summary>
    public ReferenceExpression ConnectionStringExpression => ReferenceExpression.Create($"{PrimaryEndpoint}");
}
