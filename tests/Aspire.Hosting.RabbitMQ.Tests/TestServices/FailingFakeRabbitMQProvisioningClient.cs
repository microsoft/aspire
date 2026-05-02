// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.RabbitMQ.Provisioning;
using RabbitMQ.Client;

namespace Aspire.Hosting.RabbitMQ.Tests.TestServices;

/// <summary>
/// A provisioning client that always throws on <see cref="CreateVirtualHostAsync"/>,
/// simulating a vhost-level failure that should cascade to all children.
/// </summary>
internal sealed class FailingFakeRabbitMQProvisioningClient : IRabbitMQProvisioningClient
{
    public ValueTask<IConnection> GetOrCreateConnectionAsync(string vhost, CancellationToken ct) => throw new NotImplementedException();
    public Task<bool> CanConnectAsync(string vhost, CancellationToken ct) => Task.FromResult(false);
    public Task DeclareExchangeAsync(string vhost, string name, string type, bool durable, bool autoDelete, IDictionary<string, object?>? args, CancellationToken ct) => throw new NotImplementedException();
    public Task DeclareQueueAsync(string vhost, string name, bool durable, bool exclusive, bool autoDelete, IDictionary<string, object?>? args, CancellationToken ct) => throw new NotImplementedException();
    public Task BindQueueAsync(string vhost, string sourceExchange, string queue, string routingKey, IDictionary<string, object?>? args, CancellationToken ct) => throw new NotImplementedException();
    public Task BindExchangeAsync(string vhost, string sourceExchange, string destExchange, string routingKey, IDictionary<string, object?>? args, CancellationToken ct) => throw new NotImplementedException();
    public Task<bool> QueueExistsAsync(string vhost, string name, CancellationToken ct) => throw new NotImplementedException();
    public Task<bool> ExchangeExistsAsync(string vhost, string name, CancellationToken ct) => throw new NotImplementedException();
    public Task CreateVirtualHostAsync(string vhost, CancellationToken ct) => throw new DistributedApplicationException("Failed to create virtual host");
    public Task PutShovelAsync(string vhost, string name, RabbitMQShovelDefinition def, CancellationToken ct) => throw new NotImplementedException();
    public Task<string?> GetShovelStateAsync(string vhost, string name, CancellationToken ct) => throw new NotImplementedException();
    public Task PutPolicyAsync(string vhost, string name, RabbitMQPolicyDefinition def, CancellationToken ct) => throw new NotImplementedException();
    public ValueTask DisposeAsync() => default;
}
