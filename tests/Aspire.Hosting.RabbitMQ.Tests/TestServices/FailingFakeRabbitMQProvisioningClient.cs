// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.RabbitMQ.Provisioning;

namespace Aspire.Hosting.RabbitMQ.Tests.TestServices;

/// <summary>
/// A provisioning client that always throws on <see cref="CreateVirtualHostAsync"/>,
/// simulating a vhost-level failure that should cascade to all children.
/// </summary>
internal sealed class FailingFakeRabbitMQProvisioningClient : IRabbitMQProvisioningClient
{
    public Task<bool> CanConnectAsync(string vhost, CancellationToken ct) => Task.FromResult(false);
    public Task DeclareExchangeAsync(string vhost, string name, string type, bool durable, bool autoDelete, IDictionary<string, object?>? args, CancellationToken ct) => throw new NotImplementedException();
    public Task DeclareQueueAsync(string vhost, string name, bool durable, bool exclusive, bool autoDelete, IDictionary<string, object?>? args, CancellationToken ct) => throw new NotImplementedException();
    public Task BindQueueAsync(string vhost, string sourceExchange, string queue, string routingKey, IDictionary<string, object?>? args, CancellationToken ct) => throw new NotImplementedException();
    public Task BindExchangeAsync(string vhost, string sourceExchange, string destExchange, string routingKey, IDictionary<string, object?>? args, CancellationToken ct) => throw new NotImplementedException();
    public Task CreateVirtualHostAsync(string vhost, CancellationToken ct) => throw new DistributedApplicationException("Failed to create virtual host");
    public Task PutShovelAsync(string vhost, string name, RabbitMQShovelDefinition def, CancellationToken ct) => throw new NotImplementedException();
    public Task PutPolicyAsync(string vhost, string name, RabbitMQPolicyDefinition def, CancellationToken ct) => throw new NotImplementedException();

    // Minimal read-back/delete stubs so the test project compiles against the extended interface.
    // Read-backs report "not found" and deletes are no-ops; Phase 4 fleshes these out.
    public Task<RabbitMQQueueDefinition?> GetQueueAsync(string vhost, string name, CancellationToken ct) => Task.FromResult<RabbitMQQueueDefinition?>(null);
    public Task<RabbitMQExchangeDefinition?> GetExchangeAsync(string vhost, string name, CancellationToken ct) => Task.FromResult<RabbitMQExchangeDefinition?>(null);
    public Task<RabbitMQPolicyDefinition?> GetPolicyAsync(string vhost, string name, CancellationToken ct) => Task.FromResult<RabbitMQPolicyDefinition?>(null);
    public Task<RabbitMQShovelDefinition?> GetShovelAsync(string vhost, string name, CancellationToken ct) => Task.FromResult<RabbitMQShovelDefinition?>(null);
    public Task<bool> VirtualHostExistsAsync(string vhost, CancellationToken ct) => Task.FromResult(false);
    public Task DeleteVirtualHostAsync(string vhost, CancellationToken ct) => Task.CompletedTask;
    public Task DeleteQueueAsync(string vhost, string name, CancellationToken ct) => Task.CompletedTask;
    public Task DeleteExchangeAsync(string vhost, string name, CancellationToken ct) => Task.CompletedTask;
    public Task DeletePolicyAsync(string vhost, string name, CancellationToken ct) => Task.CompletedTask;
    public Task DeleteShovelAsync(string vhost, string name, CancellationToken ct) => Task.CompletedTask;
    public ValueTask DisposeAsync() => default;
}
