// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.RabbitMQ.Provisioning;

internal interface IRabbitMQProvisioningClient : IAsyncDisposable
{
    Task<bool> CanConnectAsync(string vhost, CancellationToken ct);

    // Gets whether the RabbitMQ management plugin (HTTP API) is available for this server. When false,
    // queue/exchange probe and delete must fall back to AMQP-only operations because the management HTTP
    // endpoint (used by GetQueueAsync/GetExchangeAsync/DeleteQueueAsync/DeleteExchangeAsync) is not exposed.
    bool ManagementEnabled { get; }

    // AMQP
    Task DeclareExchangeAsync(string vhost, string name, string type, bool durable, bool autoDelete, IDictionary<string, object?>? args, CancellationToken ct);
    Task DeclareQueueAsync(string vhost, string name, bool durable, bool exclusive, bool autoDelete, IDictionary<string, object?>? args, CancellationToken ct);
    Task BindQueueAsync(string vhost, string sourceExchange, string queue, string routingKey, IDictionary<string, object?>? args, CancellationToken ct);
    Task BindExchangeAsync(string vhost, string sourceExchange, string destExchange, string routingKey, IDictionary<string, object?>? args, CancellationToken ct);

    // AMQP-only existence checks (passive declare). Return false when the entity does not exist (broker
    // replies 404 NOT_FOUND, which closes the channel; the client recovers the channel transparently).
    // Used by queue/exchange health probes when the management plugin is not enabled.
    Task<bool> QueueExistsAsync(string vhost, string name, CancellationToken ct);
    Task<bool> ExchangeExistsAsync(string vhost, string name, CancellationToken ct);

    // AMQP-only deletes. Idempotent: deleting a non-existent entity is a successful no-op.
    // Used by the destructive Stop command for queues/exchanges when the management plugin is not enabled.
    Task DeleteQueueAmqpAsync(string vhost, string name, CancellationToken ct);
    Task DeleteExchangeAmqpAsync(string vhost, string name, CancellationToken ct);

    // Management HTTP
    Task CreateVirtualHostAsync(string vhost, CancellationToken ct);
    Task PutShovelAsync(string vhost, string name, RabbitMQShovelDefinition def, CancellationToken ct);
    Task PutPolicyAsync(string vhost, string name, RabbitMQPolicyDefinition def, CancellationToken ct);

    // Read-back for declared-fields drift detection. A null return means the entity was not found (404).
    // These MUST return only the fields Aspire declared/sent in the object's own PUT — never any
    // server-computed / effective / resolved field (e.g. effective_policy_definition). Best-effort:
    // fields the management API cannot round-trip are omitted from the returned definition.
    Task<RabbitMQQueueDefinition?> GetQueueAsync(string vhost, string name, CancellationToken ct);
    Task<RabbitMQExchangeDefinition?> GetExchangeAsync(string vhost, string name, CancellationToken ct);
    Task<RabbitMQPolicyDefinition?> GetPolicyAsync(string vhost, string name, CancellationToken ct);
    Task<RabbitMQShovelDefinition?> GetShovelAsync(string vhost, string name, CancellationToken ct);
    Task<bool> VirtualHostExistsAsync(string vhost, CancellationToken ct);

    // Delete methods for the destructive Stop command (Stop = delete). Each is idempotent:
    // deleting a non-existent entity is a successful no-op (404 does not throw).
    Task DeleteVirtualHostAsync(string vhost, CancellationToken ct);
    Task DeleteQueueAsync(string vhost, string name, CancellationToken ct);
    Task DeleteExchangeAsync(string vhost, string name, CancellationToken ct);
    Task DeletePolicyAsync(string vhost, string name, CancellationToken ct);
    Task DeleteShovelAsync(string vhost, string name, CancellationToken ct);
}
