// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.RabbitMQ;

/// <summary>
/// Represents a RabbitMQ plugin.
/// </summary>
public enum RabbitMQPlugin
{
    /// <summary>
    /// The rabbitmq_management plugin.
    /// </summary>
    Management,

    /// <summary>
    /// The rabbitmq_management_agent plugin.
    /// </summary>
    ManagementAgent,

    /// <summary>
    /// The rabbitmq_shovel plugin.
    /// </summary>
    Shovel,

    /// <summary>
    /// The rabbitmq_shovel_management plugin.
    /// </summary>
    ShovelManagement,

    /// <summary>
    /// The rabbitmq_federation plugin.
    /// </summary>
    Federation,

    /// <summary>
    /// The rabbitmq_federation_management plugin.
    /// </summary>
    FederationManagement,

    /// <summary>
    /// The rabbitmq_stream plugin.
    /// </summary>
    Stream,

    /// <summary>
    /// The rabbitmq_stream_management plugin.
    /// </summary>
    StreamManagement,

    /// <summary>
    /// The rabbitmq_mqtt plugin.
    /// </summary>
    Mqtt,

    /// <summary>
    /// The rabbitmq_stomp plugin.
    /// </summary>
    Stomp,

    /// <summary>
    /// The rabbitmq_web_mqtt plugin.
    /// </summary>
    WebMqtt,

    /// <summary>
    /// The rabbitmq_web_stomp plugin.
    /// </summary>
    WebStomp,

    /// <summary>
    /// The rabbitmq_prometheus plugin.
    /// </summary>
    Prometheus,

    /// <summary>
    /// The rabbitmq_amqp1_0 plugin.
    /// </summary>
    Amqp10
}
