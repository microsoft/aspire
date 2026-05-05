// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using Aspire.Hosting.ApplicationModel;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;

namespace Aspire.Hosting.RabbitMQ.Provisioning;

internal sealed class RabbitMQProvisioningClient : IRabbitMQProvisioningClient
{
    private readonly RabbitMQServerResource _server;
    private readonly ILogger _logger;
    private readonly ConcurrentDictionary<string, (IConnection, IChannel)> _channels = new(StringComparer.Ordinal);
    private HttpClient? _http;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public RabbitMQProvisioningClient(RabbitMQServerResource server, ILogger<RabbitMQProvisioningClient> logger)
    {
        _server = server;
        _logger = logger;
    }

    public async ValueTask<IConnection> GetOrCreateConnectionAsync(string vhost, CancellationToken ct)
    {
        await GetOrCreateChannelAsync(vhost, ct).ConfigureAwait(false);
        return _channels[vhost].Item1;
    }

    public async Task<bool> CanConnectAsync(string vhost, CancellationToken ct)
    {
        try
        {
            await GetOrCreateConnectionAsync(vhost, ct).ConfigureAwait(false);
            return true;
        }
        catch
        {
            return false;
        }
    }

    internal async ValueTask<IChannel> GetOrCreateChannelAsync(string vhost, CancellationToken ct)
    {
        if (_channels.TryGetValue(vhost, out var existing) && existing.Item2.IsOpen)
        {
            return existing.Item2;
        }

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_channels.TryGetValue(vhost, out var racy) && racy.Item2.IsOpen)
            {
                return racy.Item2;
            }

            // Dispose the stale connection/channel before replacing it to avoid leaking resources.
            if (_channels.TryRemove(vhost, out var stale))
            {
                try { await stale.Item2.CloseAsync(cancellationToken: CancellationToken.None).ConfigureAwait(false); } catch { }
                try { stale.Item2.Dispose(); } catch { }
                try { await stale.Item1.CloseAsync(cancellationToken: CancellationToken.None).ConfigureAwait(false); } catch { }
                try { stale.Item1.Dispose(); } catch { }
            }

            var cs = await _server.ConnectionStringExpression.GetValueAsync(ct).ConfigureAwait(false);
            var f = new ConnectionFactory { Uri = new Uri(cs!), VirtualHost = vhost };
            var conn = await f.CreateConnectionAsync(ct).ConfigureAwait(false);
            var ch = await conn.CreateChannelAsync(cancellationToken: ct).ConfigureAwait(false);
            _channels[vhost] = (conn, ch);
            return ch;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async ValueTask<HttpClient> GetOrCreateHttpClientAsync(CancellationToken ct)
    {
        if (_http is not null)
        {
            return _http;
        }

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_http is not null)
            {
                return _http;
            }

            var mgmt = await _server.ManagementEndpoint.GetValueAsync(ct).ConfigureAwait(false)
                ?? throw new DistributedApplicationException(
                    "Management endpoint is not exposed. Call WithManagementPlugin().");
            var user = await _server.UserNameReference.GetValueAsync(ct).ConfigureAwait(false);
            var pass = await _server.PasswordParameter.GetValueAsync(ct).ConfigureAwait(false);
            _http = new HttpClient { BaseAddress = new Uri(mgmt) };
            _http.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{pass}")));
            return _http;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task DeclareExchangeAsync(string vhost, string name, string type, bool durable, bool autoDelete, IDictionary<string, object?>? args, CancellationToken ct)
    {
        _logger.LogDebug("Declaring exchange '{Exchange}' (type={Type}) on vhost '{Vhost}'.", name, type, vhost);
        var ch = await GetOrCreateChannelAsync(vhost, ct).ConfigureAwait(false);
        await AmqpAsync(
            () => ch.ExchangeDeclareAsync(name, type, durable, autoDelete, args, cancellationToken: ct),
            $"Failed to declare exchange '{name}' on vhost '{vhost}'").ConfigureAwait(false);
    }

    public async Task DeclareQueueAsync(string vhost, string name, bool durable, bool exclusive, bool autoDelete, IDictionary<string, object?>? args, CancellationToken ct)
    {
        _logger.LogDebug("Declaring queue '{Queue}' on vhost '{Vhost}'.", name, vhost);
        var ch = await GetOrCreateChannelAsync(vhost, ct).ConfigureAwait(false);
        await AmqpAsync(
            () => ch.QueueDeclareAsync(name, durable, exclusive, autoDelete, args, cancellationToken: ct),
            $"Failed to declare queue '{name}' on vhost '{vhost}'").ConfigureAwait(false);
    }

    public async Task BindQueueAsync(string vhost, string sourceExchange, string queue, string routingKey, IDictionary<string, object?>? args, CancellationToken ct)
    {
        _logger.LogDebug("Binding queue '{Queue}' to exchange '{Exchange}' on vhost '{Vhost}'.", queue, sourceExchange, vhost);
        var ch = await GetOrCreateChannelAsync(vhost, ct).ConfigureAwait(false);
        await AmqpAsync(
            () => ch.QueueBindAsync(queue, sourceExchange, routingKey, args, cancellationToken: ct),
            $"Failed to bind queue '{queue}' to exchange '{sourceExchange}' on vhost '{vhost}'").ConfigureAwait(false);
    }

    public async Task BindExchangeAsync(string vhost, string sourceExchange, string destExchange, string routingKey, IDictionary<string, object?>? args, CancellationToken ct)
    {
        _logger.LogDebug("Binding exchange '{Dest}' to exchange '{Source}' on vhost '{Vhost}'.", destExchange, sourceExchange, vhost);
        var ch = await GetOrCreateChannelAsync(vhost, ct).ConfigureAwait(false);
        await AmqpAsync(
            () => ch.ExchangeBindAsync(destExchange, sourceExchange, routingKey, args, cancellationToken: ct),
            $"Failed to bind exchange '{destExchange}' to exchange '{sourceExchange}' on vhost '{vhost}'").ConfigureAwait(false);
    }

    public async Task<bool> QueueExistsAsync(string vhost, string name, CancellationToken ct)
    {
        var ch = await GetOrCreateChannelAsync(vhost, ct).ConfigureAwait(false);
        return await SafeAmqpAsync(() => ch.QueueDeclarePassiveAsync(name, cancellationToken: ct)).ConfigureAwait(false);
    }

    public async Task<bool> ExchangeExistsAsync(string vhost, string name, CancellationToken ct)
    {
        var ch = await GetOrCreateChannelAsync(vhost, ct).ConfigureAwait(false);
        return await SafeAmqpAsync(() => ch.ExchangeDeclarePassiveAsync(name, cancellationToken: ct)).ConfigureAwait(false);
    }

    public async Task CreateVirtualHostAsync(string vhost, CancellationToken ct)
    {
        _logger.LogDebug("Creating virtual host '{Vhost}'.", vhost);
        await HttpPutAsync($"/api/vhosts/{Uri.EscapeDataString(vhost)}", (object?)null, $"Failed to create virtual host '{vhost}'", ct).ConfigureAwait(false);
    }

    public async Task PutShovelAsync(string vhost, string name, RabbitMQShovelDefinition def, CancellationToken ct)
    {
        _logger.LogDebug("Creating shovel '{Shovel}' on vhost '{Vhost}'.", name, vhost);
        await HttpPutAsync($"/api/parameters/shovel/{Uri.EscapeDataString(vhost)}/{Uri.EscapeDataString(name)}", def, $"Failed to create shovel '{name}' on vhost '{vhost}'", ct).ConfigureAwait(false);
    }

    public async Task<string?> GetShovelStateAsync(string vhost, string name, CancellationToken ct)
    {
        var http = await GetOrCreateHttpClientAsync(ct).ConfigureAwait(false);
        try
        {
            var response = await http.GetAsync($"/api/shovels/{Uri.EscapeDataString(vhost)}", ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var shovels = await response.Content.ReadFromJsonAsync<RabbitMQShovelStatus[]>(cancellationToken: ct).ConfigureAwait(false);
            var shovel = shovels?.FirstOrDefault(s => s.Name == name);
            return shovel?.State;
        }
        catch
        {
            return null;
        }
    }

    public async Task PutPolicyAsync(string vhost, string name, RabbitMQPolicyDefinition def, CancellationToken ct)
    {
        _logger.LogDebug("Applying policy '{Policy}' on vhost '{Vhost}'.", name, vhost);
        await HttpPutAsync($"/api/policies/{Uri.EscapeDataString(vhost)}/{Uri.EscapeDataString(name)}", def, $"Failed to apply policy '{name}' on vhost '{vhost}'", ct).ConfigureAwait(false);
    }

    public async Task<bool> PolicyExistsAsync(string vhost, string name, CancellationToken ct)
    {
        var http = await GetOrCreateHttpClientAsync(ct).ConfigureAwait(false);
        try
        {
            var response = await http.GetAsync($"/api/policies/{Uri.EscapeDataString(vhost)}/{Uri.EscapeDataString(name)}", ct).ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            foreach (var (_, (conn, ch)) in _channels)
            {
                try { await ch.CloseAsync().ConfigureAwait(false); } catch (Exception ex) { _logger.LogDebug(ex, "Suppressed channel close error during disposal."); }
                try { await ch.DisposeAsync().ConfigureAwait(false); } catch (Exception ex) { _logger.LogDebug(ex, "Suppressed channel dispose error during disposal."); }
                try { await conn.CloseAsync().ConfigureAwait(false); } catch (Exception ex) { _logger.LogDebug(ex, "Suppressed connection close error during disposal."); }
                try { await conn.DisposeAsync().ConfigureAwait(false); } catch (Exception ex) { _logger.LogDebug(ex, "Suppressed connection dispose error during disposal."); }
            }
            _channels.Clear();
        }
        finally
        {
            _gate.Release();
        }
        _http?.Dispose();
        _gate.Dispose();
    }

    /// <summary>
    /// Executes an AMQP operation and wraps any exception in a <see cref="DistributedApplicationException"/>.
    /// </summary>
    private static async Task AmqpAsync(Func<Task> action, string errorMessage)
    {
        try
        {
            await action().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            throw new DistributedApplicationException($"{errorMessage}: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Executes an AMQP passive-declare operation and returns <see langword="true"/> if it succeeds,
    /// <see langword="false"/> if the entity does not exist (any exception is swallowed).
    /// </summary>
    private static async Task<bool> SafeAmqpAsync(Func<Task> action)
    {
        try
        {
            await action().ConfigureAwait(false);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Sends an HTTP PUT to the management API and wraps any failure in a <see cref="DistributedApplicationException"/>.
    /// </summary>
    private async Task HttpPutAsync<T>(string path, T? body, string errorMessage, CancellationToken ct)
    {
        var http = await GetOrCreateHttpClientAsync(ct).ConfigureAwait(false);
        try
        {
            var response = body is null
                ? await http.PutAsync(path, null, ct).ConfigureAwait(false)
                : await http.PutAsJsonAsync(path, body, cancellationToken: ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
        }
        catch (Exception ex)
        {
            throw new DistributedApplicationException($"{errorMessage}: {ex.Message}", ex);
        }
    }

    private sealed class RabbitMQShovelStatus
    {
        public string? Name { get; set; }
        public string? State { get; set; }
    }
}
