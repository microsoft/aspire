// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.RabbitMQ.Provisioning;
using RabbitMQ.Client;

namespace Aspire.Hosting.RabbitMQ.Tests.TestServices;

internal sealed class FakeRabbitMQProvisioningClient : IRabbitMQProvisioningClient
{
    public List<string> Calls { get; } = new();

    /// <summary>
    /// When set, <see cref="DeclareQueueAsync"/> throws for queues whose name is in this set.
    /// Used to simulate per-entity failures without affecting siblings.
    /// </summary>
    public HashSet<string> FailQueueNames { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// When set, <see cref="DeclareExchangeAsync"/> throws for exchanges whose name is in this set.
    /// </summary>
    public HashSet<string> FailExchangeNames { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// When set, <see cref="BindQueueAsync"/> and <see cref="BindExchangeAsync"/> throw for
    /// source exchanges whose name is in this set.
    /// </summary>
    public HashSet<string> FailBindingSourceExchangeNames { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// When set, <see cref="PutPolicyAsync"/> throws for policies whose name is in this set.
    /// </summary>
    public HashSet<string> FailPolicyNames { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// Controls the return value of <see cref="CanConnectAsync"/>. Defaults to <see langword="true"/>.
    /// </summary>
    public bool CanConnect { get; set; } = true;

    public ValueTask<IConnection> GetOrCreateConnectionAsync(string vhost, CancellationToken ct)
    {
        Calls.Add($"GetOrCreateConnectionAsync({vhost})");
        return ValueTask.FromResult<IConnection>(null!);
    }

    public Task<bool> CanConnectAsync(string vhost, CancellationToken ct)
    {
        Calls.Add($"CanConnectAsync({vhost})");
        return Task.FromResult(CanConnect);
    }

    public Task DeclareExchangeAsync(string vhost, string name, string type, bool durable, bool autoDelete, IDictionary<string, object?>? args, CancellationToken ct)
    {
        Calls.Add($"DeclareExchangeAsync({vhost}, {name}, {type}, {durable}, {autoDelete})");
        if (FailExchangeNames.Contains(name))
        {
            throw new DistributedApplicationException($"Simulated failure declaring exchange '{name}'.");
        }

        return Task.CompletedTask;
    }

    public Task DeclareQueueAsync(string vhost, string name, bool durable, bool exclusive, bool autoDelete, IDictionary<string, object?>? args, CancellationToken ct)
    {
        Calls.Add($"DeclareQueueAsync({vhost}, {name}, {durable}, {exclusive}, {autoDelete})");
        if (FailQueueNames.Contains(name))
        {
            throw new DistributedApplicationException($"Simulated failure declaring queue '{name}'.");
        }

        return Task.CompletedTask;
    }

    public Task BindQueueAsync(string vhost, string sourceExchange, string queue, string routingKey, IDictionary<string, object?>? args, CancellationToken ct)
    {
        Calls.Add($"BindQueueAsync({vhost}, {sourceExchange}, {queue}, {routingKey})");
        if (FailBindingSourceExchangeNames.Contains(sourceExchange))
        {
            throw new DistributedApplicationException($"Simulated failure binding queue '{queue}' to exchange '{sourceExchange}'.");
        }

        return Task.CompletedTask;
    }

    public Task BindExchangeAsync(string vhost, string sourceExchange, string destExchange, string routingKey, IDictionary<string, object?>? args, CancellationToken ct)
    {
        Calls.Add($"BindExchangeAsync({vhost}, {sourceExchange}, {destExchange}, {routingKey})");
        if (FailBindingSourceExchangeNames.Contains(sourceExchange))
        {
            throw new DistributedApplicationException($"Simulated failure binding exchange '{destExchange}' to exchange '{sourceExchange}'.");
        }

        return Task.CompletedTask;
    }

    public Task<bool> QueueExistsAsync(string vhost, string name, CancellationToken ct)
    {
        Calls.Add($"QueueExistsAsync({vhost}, {name})");
        return Task.FromResult(!FailQueueNames.Contains(name));
    }

    public Task<bool> ExchangeExistsAsync(string vhost, string name, CancellationToken ct)
    {
        Calls.Add($"ExchangeExistsAsync({vhost}, {name})");
        return Task.FromResult(!FailExchangeNames.Contains(name));
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

    public Task PutPolicyAsync(string vhost, string name, RabbitMQPolicyDefinition def, CancellationToken ct)
    {
        Calls.Add($"PutPolicyAsync({vhost}, {name}, {def.Pattern}, {def.ApplyTo})");
        if (FailPolicyNames.Contains(name))
        {
            throw new DistributedApplicationException($"Simulated failure applying policy '{name}'.");
        }

        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        Calls.Add("DisposeAsync()");
        return default;
    }
}
