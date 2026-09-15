// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.ApplicationModel;

/// <summary>
/// Represents a well-known RabbitMQ plugin that can be enabled via
/// <see cref="RabbitMQBuilderExtensions.WithPlugin(IResourceBuilder{RabbitMQServerResource}, RabbitMQPlugin)"/>.
/// </summary>
public enum RabbitMQPlugin
{
    /// <summary>The <c>rabbitmq_management</c> plugin.</summary>
    Management,

    /// <summary>The <c>rabbitmq_management_agent</c> plugin.</summary>
    ManagementAgent,

    /// <summary>The <c>rabbitmq_web_dispatch</c> plugin.</summary>
    WebDispatch,

    /// <summary>The <c>rabbitmq_shovel</c> plugin.</summary>
    Shovel,

    /// <summary>The <c>rabbitmq_shovel_management</c> plugin.</summary>
    ShovelManagement,

    /// <summary>The <c>rabbitmq_federation</c> plugin.</summary>
    Federation,

    /// <summary>The <c>rabbitmq_federation_management</c> plugin.</summary>
    FederationManagement,

    /// <summary>The <c>rabbitmq_stream</c> plugin.</summary>
    Stream,

    /// <summary>The <c>rabbitmq_stream_management</c> plugin.</summary>
    StreamManagement,

    /// <summary>The <c>rabbitmq_mqtt</c> plugin.</summary>
    Mqtt,

    /// <summary>The <c>rabbitmq_stomp</c> plugin.</summary>
    Stomp,

    /// <summary>The <c>rabbitmq_web_mqtt</c> plugin.</summary>
    WebMqtt,

    /// <summary>The <c>rabbitmq_web_stomp</c> plugin.</summary>
    WebStomp,

    /// <summary>The <c>rabbitmq_prometheus</c> plugin.</summary>
    Prometheus,

    /// <summary>The <c>rabbitmq_amqp1_0</c> plugin.</summary>
    Amqp10
}

/// <summary>
/// Provides the canonical broker plugin name for each <see cref="RabbitMQPlugin"/> value.
/// </summary>
internal static class RabbitMQPluginNames
{
    /// <summary>
    /// Returns the canonical broker plugin name (e.g. <c>rabbitmq_management</c>) for the given <paramref name="plugin"/>.
    /// </summary>
    /// <param name="plugin">The plugin enum value.</param>
    /// <returns>The broker-level plugin name string.</returns>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="plugin"/> is not a recognised value.</exception>
    internal static string ToPluginName(this RabbitMQPlugin plugin) => plugin switch
    {
        RabbitMQPlugin.Management => "rabbitmq_management",
        RabbitMQPlugin.ManagementAgent => "rabbitmq_management_agent",
        RabbitMQPlugin.WebDispatch => "rabbitmq_web_dispatch",
        RabbitMQPlugin.Shovel => "rabbitmq_shovel",
        RabbitMQPlugin.ShovelManagement => "rabbitmq_shovel_management",
        RabbitMQPlugin.Federation => "rabbitmq_federation",
        RabbitMQPlugin.FederationManagement => "rabbitmq_federation_management",
        RabbitMQPlugin.Stream => "rabbitmq_stream",
        RabbitMQPlugin.StreamManagement => "rabbitmq_stream_management",
        RabbitMQPlugin.Mqtt => "rabbitmq_mqtt",
        RabbitMQPlugin.Stomp => "rabbitmq_stomp",
        RabbitMQPlugin.WebMqtt => "rabbitmq_web_mqtt",
        RabbitMQPlugin.WebStomp => "rabbitmq_web_stomp",
        RabbitMQPlugin.Prometheus => "rabbitmq_prometheus",
        RabbitMQPlugin.Amqp10 => "rabbitmq_amqp1_0",
        _ => throw new ArgumentOutOfRangeException(nameof(plugin), plugin, null)
    };
}
