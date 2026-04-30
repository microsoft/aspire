// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.RabbitMQ.Provisioning;
using RabbitMQ.Client;

namespace Aspire.Hosting.RabbitMQ.Tests.TestServices;

internal sealed class FakeRabbitMQProvisioningClient : IRabbitMQProvisioningClient
{
    public List<string> Calls { get; } = new();

    public ValueTask<IConnection> GetOrCreateConnectionAsync(string vhost, CancellationToken ct)
    {
        Calls.Add($"GetOrCreateConnectionAsync({vhost})");
        return ValueTask.FromResult<IConnection>(null!);
    }

    public Task DeclareExchangeAsync(string vhost, string name, string type, bool durable, bool autoDelete, IDictionary<string, object?>? args, CancellationToken ct)
    {
        Calls.Add($"DeclareExchangeAsync({vhost}, {name}, {type}, {durable}, {autoDelete})");
        return Task.CompletedTask;
    }

    public Task DeclareQueueAsync(string vhost, string name, bool durable, bool exclusive, bool autoDelete, IDictionary<string, object?>? args, CancellationToken ct)
    {
        Calls.Add($"DeclareQueueAsync({vhost}, {name}, {durable}, {exclusive}, {autoDelete})");
        return Task.CompletedTask;
    }

    public Task BindQueueAsync(string vhost, string sourceExchange, string queue, string routingKey, IDictionary<string, object?>? args, CancellationToken ct)
    {
        Calls.Add($"BindQueueAsync({vhost}, {sourceExchange}, {queue}, {routingKey})");
        return Task.CompletedTask;
    }

    public Task BindExchangeAsync(string vhost, string sourceExchange, string destExchange, string routingKey, IDictionary<string, object?>? args, CancellationToken ct)
    {
        Calls.Add($"BindExchangeAsync({vhost}, {sourceExchange}, {destExchange}, {routingKey})");
        return Task.CompletedTask;
    }

    public Task<bool> QueueExistsAsync(string vhost, string name, CancellationToken ct)
    {
        Calls.Add($"QueueExistsAsync({vhost}, {name})");
        return Task.FromResult(true);
    }

    public Task<bool> ExchangeExistsAsync(string vhost, string name, CancellationToken ct)
    {
        Calls.Add($"ExchangeExistsAsync({vhost}, {name})");
        return Task.FromResult(true);
    }

    public Task CreateVirtualHostAsync(string vhost, CancellationToken ct)
    {
        Calls.Add($"CreateVirtualHostAsync({vhost})");
        return Task.CompletedTask;
    }

    public Task PutShovelAsync(string vhost, string name, RabbitMQShovelDefinition def, CancellationToken ct)
    {
        Calls.Add($"PutShovelAsync({vhost}, {name}, {def.Value.SrcUri}, {def.Value.DestUri})");
        return Task.CompletedTask;
    }

    public Task<string?> GetShovelStateAsync(string vhost, string name, CancellationToken ct)
    {
        Calls.Add($"GetShovelStateAsync({vhost}, {name})");
        return Task.FromResult<string?>("running");
    }

    public ValueTask DisposeAsync()
    {
        Calls.Add("DisposeAsync()");
        return default;
    }
}
