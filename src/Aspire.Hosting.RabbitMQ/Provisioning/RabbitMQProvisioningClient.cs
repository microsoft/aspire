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
        var ch = await GetOrCreateChannelAsync(vhost, ct).ConfigureAwait(false);
        try
        {
            await ch.ExchangeDeclareAsync(name, type, durable, autoDelete, args, cancellationToken: ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            throw new DistributedApplicationException($"Failed to declare exchange '{name}' on vhost '{vhost}': {ex.Message}", ex);
        }
    }

    public async Task DeclareQueueAsync(string vhost, string name, bool durable, bool exclusive, bool autoDelete, IDictionary<string, object?>? args, CancellationToken ct)
    {
        var ch = await GetOrCreateChannelAsync(vhost, ct).ConfigureAwait(false);
        try
        {
            await ch.QueueDeclareAsync(name, durable, exclusive, autoDelete, args, cancellationToken: ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            throw new DistributedApplicationException($"Failed to declare queue '{name}' on vhost '{vhost}': {ex.Message}", ex);
        }
    }

    public async Task BindQueueAsync(string vhost, string sourceExchange, string queue, string routingKey, IDictionary<string, object?>? args, CancellationToken ct)
    {
        var ch = await GetOrCreateChannelAsync(vhost, ct).ConfigureAwait(false);
        try
        {
            await ch.QueueBindAsync(queue, sourceExchange, routingKey, args, cancellationToken: ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            throw new DistributedApplicationException($"Failed to bind queue '{queue}' to exchange '{sourceExchange}' on vhost '{vhost}': {ex.Message}", ex);
        }
    }

    public async Task BindExchangeAsync(string vhost, string sourceExchange, string destExchange, string routingKey, IDictionary<string, object?>? args, CancellationToken ct)
    {
        var ch = await GetOrCreateChannelAsync(vhost, ct).ConfigureAwait(false);
        try
        {
            await ch.ExchangeBindAsync(destExchange, sourceExchange, routingKey, args, cancellationToken: ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            throw new DistributedApplicationException($"Failed to bind exchange '{destExchange}' to exchange '{sourceExchange}' on vhost '{vhost}': {ex.Message}", ex);
        }
    }

    public async Task<bool> QueueExistsAsync(string vhost, string name, CancellationToken ct)
    {
        var ch = await GetOrCreateChannelAsync(vhost, ct).ConfigureAwait(false);
        try
        {
            await ch.QueueDeclarePassiveAsync(name, cancellationToken: ct).ConfigureAwait(false);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public async Task<bool> ExchangeExistsAsync(string vhost, string name, CancellationToken ct)
    {
        var ch = await GetOrCreateChannelAsync(vhost, ct).ConfigureAwait(false);
        try
        {
            await ch.ExchangeDeclarePassiveAsync(name, cancellationToken: ct).ConfigureAwait(false);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public async Task CreateVirtualHostAsync(string vhost, CancellationToken ct)
    {
        var http = await GetOrCreateHttpClientAsync(ct).ConfigureAwait(false);
        try
        {
            var response = await http.PutAsync($"/api/vhosts/{Uri.EscapeDataString(vhost)}", null, ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
        }
        catch (Exception ex)
        {
            throw new DistributedApplicationException($"Failed to create virtual host '{vhost}': {ex.Message}", ex);
        }
    }

    public async Task PutShovelAsync(string vhost, string name, RabbitMQShovelDefinition def, CancellationToken ct)
    {
        var http = await GetOrCreateHttpClientAsync(ct).ConfigureAwait(false);
        try
        {
            var response = await http.PutAsJsonAsync($"/api/parameters/shovel/{Uri.EscapeDataString(vhost)}/{Uri.EscapeDataString(name)}", def, cancellationToken: ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
        }
        catch (Exception ex)
        {
            throw new DistributedApplicationException($"Failed to create shovel '{name}' on vhost '{vhost}': {ex.Message}", ex);
        }
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
        var http = await GetOrCreateHttpClientAsync(ct).ConfigureAwait(false);
        try
        {
            var response = await http.PutAsJsonAsync($"/api/policies/{Uri.EscapeDataString(vhost)}/{Uri.EscapeDataString(name)}", def, cancellationToken: ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
        }
        catch (Exception ex)
        {
            throw new DistributedApplicationException($"Failed to apply policy '{name}' on vhost '{vhost}': {ex.Message}", ex);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            foreach (var (_, (conn, ch)) in _channels)
            {
                try { await ch.CloseAsync().ConfigureAwait(false); } catch { }
                try { await ch.DisposeAsync().ConfigureAwait(false); } catch { }
                try { await conn.CloseAsync().ConfigureAwait(false); } catch { }
                try { await conn.DisposeAsync().ConfigureAwait(false); } catch { }
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

    private sealed class RabbitMQShovelStatus
    {
        public string? Name { get; set; }
        public string? State { get; set; }
    }
}
