// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using RabbitMQ.Client;

namespace Aspire.Hosting.RabbitMQ.Provisioning;

internal interface IRabbitMQProvisioningClient : IAsyncDisposable
{
    // AMQP connection (used by health checks)
    ValueTask<IConnection> GetOrCreateConnectionAsync(string vhost, CancellationToken ct);
    Task<bool> CanConnectAsync(string vhost, CancellationToken ct);

    // AMQP
    Task DeclareExchangeAsync(string vhost, string name, string type, bool durable, bool autoDelete, IDictionary<string, object?>? args, CancellationToken ct);
    Task DeclareQueueAsync(string vhost, string name, bool durable, bool exclusive, bool autoDelete, IDictionary<string, object?>? args, CancellationToken ct);
    Task BindQueueAsync(string vhost, string sourceExchange, string queue, string routingKey, IDictionary<string, object?>? args, CancellationToken ct);
    Task BindExchangeAsync(string vhost, string sourceExchange, string destExchange, string routingKey, IDictionary<string, object?>? args, CancellationToken ct);
    Task<bool> QueueExistsAsync(string vhost, string name, CancellationToken ct);
    Task<bool> ExchangeExistsAsync(string vhost, string name, CancellationToken ct);

    // Management HTTP
    Task CreateVirtualHostAsync(string vhost, CancellationToken ct);
    Task PutShovelAsync(string vhost, string name, RabbitMQShovelDefinition def, CancellationToken ct);
    Task<string?> GetShovelStateAsync(string vhost, string name, CancellationToken ct);
    Task PutPolicyAsync(string vhost, string name, RabbitMQPolicyDefinition def, CancellationToken ct);
}
