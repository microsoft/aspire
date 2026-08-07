// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.RabbitMQ.Provisioning;

namespace Aspire.Hosting.RabbitMQ.Tests.TestServices;

/// <summary>
/// An in-memory <see cref="IRabbitMQProvisioningClient"/> used by unit tests.
/// </summary>
/// <remarks>
/// Write methods (Declare*/Put*/Create*) record the desired definition into in-memory state so the matching
/// read-back method (Get*/…Exists) returns it — this lets tests reconcile-then-probe and see Healthy. To
/// simulate drift, seed a differing live definition with the Seed* helpers (or seed nothing to simulate a
/// missing entity). Delete* methods remove the entity from state and record the call.
/// </remarks>
internal sealed class FakeRabbitMQProvisioningClient : IRabbitMQProvisioningClient
{
    public List<string> Calls { get; } = new();

    /// <summary>
    /// When set, <see cref="DeclareQueueAsync"/> throws for queues whose name is in this set.
    /// Used to simulate per-entity failures without affecting siblings.
    /// </summary>
    public HashSet<string> FailQueueNames { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// When set, <see cref="CreateVirtualHostAsync"/> throws for virtual hosts whose name is in this set.
    /// </summary>
    public HashSet<string> FailVirtualHostNames { get; } = new(StringComparer.Ordinal);

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

    /// <summary>
    /// Optional rendezvous hook awaited inside <see cref="DeclareQueueAsync"/> after the call is recorded
    /// but before the declared state is stored. When set, tests can block an in-flight reconcile and observe
    /// cancellation (the awaited task is passed the reconcile's <see cref="CancellationToken"/>). When
    /// <see langword="null"/> (the default) the behavior is unchanged, so existing tests are unaffected.
    /// </summary>
    public Func<CancellationToken, Task>? OnDeclareQueue { get; set; }

    // In-memory live state keyed by "vhost/name". Populated by write methods and by the Seed* helpers.
    private readonly Dictionary<string, RabbitMQQueueDefinition> _queues = new(StringComparer.Ordinal);
    private readonly Dictionary<string, RabbitMQExchangeDefinition> _exchanges = new(StringComparer.Ordinal);
    private readonly Dictionary<string, RabbitMQPolicyDefinition> _policies = new(StringComparer.Ordinal);
    private readonly Dictionary<string, RabbitMQShovelDefinition> _shovels = new(StringComparer.Ordinal);
    private readonly HashSet<string> _virtualHosts = new(StringComparer.Ordinal);

    private static string Key(string vhost, string name) => $"{vhost}/{name}";

    // ── Seed helpers (drift simulation) ──────────────────────────────────────

    public void SeedVirtualHost(string vhost) => _virtualHosts.Add(vhost);
    public void SeedQueue(string vhost, string name, RabbitMQQueueDefinition def) => _queues[Key(vhost, name)] = def;
    public void SeedExchange(string vhost, string name, RabbitMQExchangeDefinition def) => _exchanges[Key(vhost, name)] = def;
    public void SeedPolicy(string vhost, string name, RabbitMQPolicyDefinition def) => _policies[Key(vhost, name)] = def;
    public void SeedShovel(string vhost, string name, RabbitMQShovelDefinition def) => _shovels[Key(vhost, name)] = def;

    // ── Connectivity ─────────────────────────────────────────────────────────

    public Task<bool> CanConnectAsync(string vhost, CancellationToken ct)
    {
        Calls.Add($"CanConnectAsync({vhost})");
        return Task.FromResult(CanConnect);
    }

    // ── AMQP declares ─────────────────────────────────────────────────────────

    public Task DeclareExchangeAsync(string vhost, string name, string type, bool durable, bool autoDelete, IDictionary<string, object?>? args, CancellationToken ct)
    {
        Calls.Add($"DeclareExchangeAsync({vhost}, {name}, {type}, {durable}, {autoDelete})");
        if (FailExchangeNames.Contains(name))
        {
            throw new DistributedApplicationException($"Simulated failure declaring exchange '{name}'.");
        }

        // Record the declared shape so a subsequent GetExchangeAsync round-trips as Healthy.
        _exchanges[Key(vhost, name)] = new RabbitMQExchangeDefinition(type, durable, autoDelete, args);
        return Task.CompletedTask;
    }

    public async Task DeclareQueueAsync(string vhost, string name, bool durable, bool exclusive, bool autoDelete, IDictionary<string, object?>? args, CancellationToken ct)
    {
        Calls.Add($"DeclareQueueAsync({vhost}, {name}, {durable}, {exclusive}, {autoDelete})");
        if (FailQueueNames.Contains(name))
        {
            throw new DistributedApplicationException($"Simulated failure declaring queue '{name}'.");
        }

        // Optional rendezvous point so a lifecycle test can hold the reconcile open and cancel it mid-flight.
        // Default (unset) keeps the method synchronous-completing, so existing callers see no behavior change.
        if (OnDeclareQueue is { } gate)
        {
            await gate(ct).ConfigureAwait(false);
        }

        // The declared type lives inside the arguments bag under "x-queue-type" (absent = classic). Mirror
        // the broker, which reports "classic" when no explicit type was declared.
        var type = args is not null && args.TryGetValue("x-queue-type", out var t) && t is string ts ? ts : "classic";
        _queues[Key(vhost, name)] = new RabbitMQQueueDefinition(type, durable, exclusive, autoDelete, args);
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

    // ── Management HTTP writes ────────────────────────────────────────────────

    public Task CreateVirtualHostAsync(string vhost, CancellationToken ct)
    {
        Calls.Add($"CreateVirtualHostAsync({vhost})");
        if (FailVirtualHostNames.Contains(vhost))
        {
            throw new DistributedApplicationException($"boom");
        }

        _virtualHosts.Add(vhost);
        return Task.CompletedTask;
    }

    public Task PutShovelAsync(string vhost, string name, RabbitMQShovelDefinition def, CancellationToken ct)
    {
        Calls.Add($"PutShovelAsync({vhost}, {name}, {def.Value.SrcUri}, {def.Value.DestUri})");
        _shovels[Key(vhost, name)] = def;
        return Task.CompletedTask;
    }

    public Task PutPolicyAsync(string vhost, string name, RabbitMQPolicyDefinition def, CancellationToken ct)
    {
        Calls.Add($"PutPolicyAsync({vhost}, {name}, {def.Pattern}, {def.ApplyTo})");
        if (FailPolicyNames.Contains(name))
        {
            throw new DistributedApplicationException($"Simulated failure applying policy '{name}'.");
        }

        _policies[Key(vhost, name)] = def;
        return Task.CompletedTask;
    }

    // ── Read-back for drift detection ─────────────────────────────────────────

    public Task<RabbitMQQueueDefinition?> GetQueueAsync(string vhost, string name, CancellationToken ct)
    {
        Calls.Add($"GetQueueAsync({vhost}, {name})");
        return Task.FromResult(_queues.TryGetValue(Key(vhost, name), out var def) ? def : null);
    }

    public Task<RabbitMQExchangeDefinition?> GetExchangeAsync(string vhost, string name, CancellationToken ct)
    {
        Calls.Add($"GetExchangeAsync({vhost}, {name})");
        return Task.FromResult(_exchanges.TryGetValue(Key(vhost, name), out var def) ? def : null);
    }

    public Task<RabbitMQPolicyDefinition?> GetPolicyAsync(string vhost, string name, CancellationToken ct)
    {
        Calls.Add($"GetPolicyAsync({vhost}, {name})");
        return Task.FromResult(_policies.TryGetValue(Key(vhost, name), out var def) ? def : null);
    }

    public Task<RabbitMQShovelDefinition?> GetShovelAsync(string vhost, string name, CancellationToken ct)
    {
        Calls.Add($"GetShovelAsync({vhost}, {name})");
        return Task.FromResult(_shovels.TryGetValue(Key(vhost, name), out var def) ? def : null);
    }

    public Task<bool> VirtualHostExistsAsync(string vhost, CancellationToken ct)
    {
        Calls.Add($"VirtualHostExistsAsync({vhost})");
        return Task.FromResult(_virtualHosts.Contains(vhost) && !FailVirtualHostNames.Contains(vhost));
    }

    // ── Management HTTP deletes ───────────────────────────────────────────────

    public Task DeleteVirtualHostAsync(string vhost, CancellationToken ct)
    {
        Calls.Add($"DeleteVirtualHostAsync({vhost})");
        _virtualHosts.Remove(vhost);
        return Task.CompletedTask;
    }

    public Task DeleteQueueAsync(string vhost, string name, CancellationToken ct)
    {
        Calls.Add($"DeleteQueueAsync({vhost}, {name})");
        _queues.Remove(Key(vhost, name));
        return Task.CompletedTask;
    }

    public Task DeleteExchangeAsync(string vhost, string name, CancellationToken ct)
    {
        Calls.Add($"DeleteExchangeAsync({vhost}, {name})");
        _exchanges.Remove(Key(vhost, name));
        return Task.CompletedTask;
    }

    public Task DeletePolicyAsync(string vhost, string name, CancellationToken ct)
    {
        Calls.Add($"DeletePolicyAsync({vhost}, {name})");
        _policies.Remove(Key(vhost, name));
        return Task.CompletedTask;
    }

    public Task DeleteShovelAsync(string vhost, string name, CancellationToken ct)
    {
        Calls.Add($"DeleteShovelAsync({vhost}, {name})");
        _shovels.Remove(Key(vhost, name));
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        Calls.Add("DisposeAsync()");
        return default;
    }
}
