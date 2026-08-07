// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using Aspire.Hosting.ApplicationModel;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;
using RabbitMQ.Client.Exceptions;

namespace Aspire.Hosting.RabbitMQ.Provisioning;

internal sealed class RabbitMQProvisioningClient : IRabbitMQProvisioningClient
{
    private readonly ILogger _logger;
    private readonly RabbitMQAmqpConnectionManager _amqp;

    public RabbitMQProvisioningClient(RabbitMQServerResource server, ILogger<RabbitMQProvisioningClient> logger)
    {
        _logger = logger;
        _amqp = new RabbitMQAmqpConnectionManager(server);
    }

    public async ValueTask<IConnection> GetOrCreateConnectionAsync(string vhost, CancellationToken ct)
        => await _amqp.GetOrCreateConnectionAsync(vhost, ct).ConfigureAwait(false);

    public async Task<bool> CanConnectAsync(string vhost, CancellationToken ct)
        => await _amqp.CanConnectAsync(vhost, ct).ConfigureAwait(false);

    internal async ValueTask<IChannel> GetOrCreateChannelAsync(string vhost, CancellationToken ct)
        => await _amqp.GetOrCreateChannelAsync(vhost, ct).ConfigureAwait(false);

    public async Task DeclareExchangeAsync(string vhost, string name, string type, bool durable, bool autoDelete, IDictionary<string, object?>? args, CancellationToken ct)
    {
        _logger.LogDebug("Declaring exchange '{Exchange}' (type={Type}) on vhost '{Vhost}'.", name, type, vhost);
        await AmqpAsync(vhost,
            ch => ch.ExchangeDeclareAsync(name, type, durable, autoDelete, args, cancellationToken: ct),
            $"Failed to declare exchange '{name}' on vhost '{vhost}'", ct).ConfigureAwait(false);
    }

    public async Task DeclareQueueAsync(string vhost, string name, bool durable, bool exclusive, bool autoDelete, IDictionary<string, object?>? args, CancellationToken ct)
    {
        _logger.LogDebug("Declaring queue '{Queue}' on vhost '{Vhost}'.", name, vhost);
        await AmqpAsync(vhost,
            ch => ch.QueueDeclareAsync(name, durable, exclusive, autoDelete, args, cancellationToken: ct),
            $"Failed to declare queue '{name}' on vhost '{vhost}'", ct).ConfigureAwait(false);
    }

    public async Task BindQueueAsync(string vhost, string sourceExchange, string queue, string routingKey, IDictionary<string, object?>? args, CancellationToken ct)
    {
        _logger.LogDebug("Binding queue '{Queue}' to exchange '{Exchange}' on vhost '{Vhost}'.", queue, sourceExchange, vhost);
        await AmqpAsync(vhost,
            ch => ch.QueueBindAsync(queue, sourceExchange, routingKey, args, cancellationToken: ct),
            $"Failed to bind queue '{queue}' to exchange '{sourceExchange}' on vhost '{vhost}'", ct).ConfigureAwait(false);
    }

    public async Task BindExchangeAsync(string vhost, string sourceExchange, string destExchange, string routingKey, IDictionary<string, object?>? args, CancellationToken ct)
    {
        _logger.LogDebug("Binding exchange '{Dest}' to exchange '{Source}' on vhost '{Vhost}'.", destExchange, sourceExchange, vhost);
        await AmqpAsync(vhost,
            ch => ch.ExchangeBindAsync(destExchange, sourceExchange, routingKey, args, cancellationToken: ct),
            $"Failed to bind exchange '{destExchange}' to exchange '{sourceExchange}' on vhost '{vhost}'", ct).ConfigureAwait(false);
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

    public async Task PutPolicyAsync(string vhost, string name, RabbitMQPolicyDefinition def, CancellationToken ct)
    {
        _logger.LogDebug("Applying policy '{Policy}' on vhost '{Vhost}'.", name, vhost);
        await HttpPutAsync($"/api/policies/{Uri.EscapeDataString(vhost)}/{Uri.EscapeDataString(name)}", def, $"Failed to apply policy '{name}' on vhost '{vhost}'", ct).ConfigureAwait(false);
    }

    public async Task<RabbitMQQueueDefinition?> GetQueueAsync(string vhost, string name, CancellationToken ct)
    {
        return await HttpGetOrNullAsync<RabbitMQQueueDefinition>(
            $"/api/queues/{Uri.EscapeDataString(vhost)}/{Uri.EscapeDataString(name)}", ct).ConfigureAwait(false);
    }

    public async Task<RabbitMQExchangeDefinition?> GetExchangeAsync(string vhost, string name, CancellationToken ct)
    {
        return await HttpGetOrNullAsync<RabbitMQExchangeDefinition>(
            $"/api/exchanges/{Uri.EscapeDataString(vhost)}/{Uri.EscapeDataString(name)}", ct).ConfigureAwait(false);
    }

    public async Task<RabbitMQPolicyDefinition?> GetPolicyAsync(string vhost, string name, CancellationToken ct)
        => await HttpGetOrNullAsync<RabbitMQPolicyDefinition>(
            $"/api/policies/{Uri.EscapeDataString(vhost)}/{Uri.EscapeDataString(name)}", ct).ConfigureAwait(false);

    public async Task<RabbitMQShovelDefinition?> GetShovelAsync(string vhost, string name, CancellationToken ct)
    {
        return await HttpGetOrNullAsync<RabbitMQShovelDefinition>(
            $"/api/parameters/shovel/{Uri.EscapeDataString(vhost)}/{Uri.EscapeDataString(name)}", ct).ConfigureAwait(false);
    }

    public async Task<bool> VirtualHostExistsAsync(string vhost, CancellationToken ct)
    {
        var http = await _amqp.GetOrCreateHttpClientAsync(ct).ConfigureAwait(false);
        try
        {
            var response = await http.GetAsync($"/api/vhosts/{Uri.EscapeDataString(vhost)}", ct).ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    public async Task DeleteVirtualHostAsync(string vhost, CancellationToken ct)
    {
        _logger.LogDebug("Deleting virtual host '{Vhost}'.", vhost);
        await HttpDeleteAsync($"/api/vhosts/{Uri.EscapeDataString(vhost)}", $"Failed to delete virtual host '{vhost}'", ct).ConfigureAwait(false);
    }

    public async Task DeleteQueueAsync(string vhost, string name, CancellationToken ct)
    {
        _logger.LogDebug("Deleting queue '{Queue}' on vhost '{Vhost}'.", name, vhost);
        await HttpDeleteAsync($"/api/queues/{Uri.EscapeDataString(vhost)}/{Uri.EscapeDataString(name)}", $"Failed to delete queue '{name}' on vhost '{vhost}'", ct).ConfigureAwait(false);
    }

    public async Task DeleteExchangeAsync(string vhost, string name, CancellationToken ct)
    {
        _logger.LogDebug("Deleting exchange '{Exchange}' on vhost '{Vhost}'.", name, vhost);
        await HttpDeleteAsync($"/api/exchanges/{Uri.EscapeDataString(vhost)}/{Uri.EscapeDataString(name)}", $"Failed to delete exchange '{name}' on vhost '{vhost}'", ct).ConfigureAwait(false);
    }

    public async Task DeletePolicyAsync(string vhost, string name, CancellationToken ct)
    {
        _logger.LogDebug("Deleting policy '{Policy}' on vhost '{Vhost}'.", name, vhost);
        await HttpDeleteAsync($"/api/policies/{Uri.EscapeDataString(vhost)}/{Uri.EscapeDataString(name)}", $"Failed to delete policy '{name}' on vhost '{vhost}'", ct).ConfigureAwait(false);
    }

    public async Task DeleteShovelAsync(string vhost, string name, CancellationToken ct)
    {
        _logger.LogDebug("Deleting shovel '{Shovel}' on vhost '{Vhost}'.", name, vhost);
        await HttpDeleteAsync($"/api/parameters/shovel/{Uri.EscapeDataString(vhost)}/{Uri.EscapeDataString(name)}", $"Failed to delete shovel '{name}' on vhost '{vhost}'", ct).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
        => await _amqp.DisposeAsync().ConfigureAwait(false);

    private async Task AmqpAsync(string vhost, Func<IChannel, Task> action, string errorMessage, CancellationToken ct)
    {
        var ch = await _amqp.GetOrCreateChannelAsync(vhost, ct).ConfigureAwait(false);
        try
        {
            await action(ch).ConfigureAwait(false);
        }
        catch (Exception e) when (e is AlreadyClosedException or OperationInterruptedException)
        {
            ch = await _amqp.GetOrCreateChannelAsync(vhost, ct).ConfigureAwait(false);
            try
            {
                await action(ch).ConfigureAwait(false);
            }
            catch (Exception retryEx)
            {
                throw new DistributedApplicationException($"{errorMessage}: {retryEx.Message}", retryEx);
            }
        }
        catch (Exception ex)
        {
            throw new DistributedApplicationException($"{errorMessage}: {ex.Message}", ex);
        }
    }

    private async Task HttpPutAsync<T>(string path, T? body, string errorMessage, CancellationToken ct)
    {
        var http = await _amqp.GetOrCreateHttpClientAsync(ct).ConfigureAwait(false);
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

    private async Task<T?> HttpGetOrNullAsync<T>(string path, CancellationToken ct) where T : class
    {
        var http = await _amqp.GetOrCreateHttpClientAsync(ct).ConfigureAwait(false);
        try
        {
            var response = await http.GetAsync(path, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            return await response.Content.ReadFromJsonAsync<T>(cancellationToken: ct).ConfigureAwait(false);
        }
        catch
        {
            return null;
        }
    }

    private async Task HttpDeleteAsync(string path, string errorMessage, CancellationToken ct)
    {
        var http = await _amqp.GetOrCreateHttpClientAsync(ct).ConfigureAwait(false);
        try
        {
            var response = await http.DeleteAsync(path, ct).ConfigureAwait(false);
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                return;
            }

            response.EnsureSuccessStatusCode();
        }
        catch (Exception ex)
        {
            throw new DistributedApplicationException($"{errorMessage}: {ex.Message}", ex);
        }
    }

    private sealed class RabbitMQAmqpConnectionManager(RabbitMQServerResource server) : IAsyncDisposable
    {
        private readonly ConcurrentDictionary<string, (IConnection connection, IChannel channel)> _channels = new(StringComparer.Ordinal);
        private readonly SemaphoreSlim _gate = new(1, 1);
        private HttpClient? _http;

        private async ValueTask<(IConnection Connection, IChannel Channel)> GetOrCreateEntryAsync(string vhost, CancellationToken ct)
        {
            if (_channels.TryGetValue(vhost, out var existing) && existing.channel.IsOpen)
            {
                return existing;
            }

            await _gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (_channels.TryGetValue(vhost, out var racy) && racy.channel.IsOpen)
                {
                    return racy;
                }

                if (_channels.TryRemove(vhost, out var stale))
                {
                    try { await stale.channel.DisposeAsync().ConfigureAwait(false); } catch { }
                    try { await stale.connection.DisposeAsync().ConfigureAwait(false); } catch { }
                }

                var cs = await server.ConnectionStringExpression.GetValueAsync(ct).ConfigureAwait(false);
                var f = new ConnectionFactory
                {
                    Uri = new Uri(cs!),
                    VirtualHost = vhost,
                    AutomaticRecoveryEnabled = true,
                    RequestedConnectionTimeout = TimeSpan.FromSeconds(10),
                    ContinuationTimeout = TimeSpan.FromSeconds(10),
                    SocketReadTimeout = TimeSpan.FromSeconds(10),
                    SocketWriteTimeout = TimeSpan.FromSeconds(10),
                };
                var conn = await f.CreateConnectionAsync(ct).ConfigureAwait(false);
                var ch = await conn.CreateChannelAsync(cancellationToken: ct).ConfigureAwait(false);
                var entry = (conn, ch);
                _channels[vhost] = entry;
                return entry;
            }
            finally
            {
                _gate.Release();
            }
        }

        internal async ValueTask<IChannel> GetOrCreateChannelAsync(string vhost, CancellationToken ct)
            => (await GetOrCreateEntryAsync(vhost, ct).ConfigureAwait(false)).Channel;

        internal async ValueTask<IConnection> GetOrCreateConnectionAsync(string vhost, CancellationToken ct)
            => (await GetOrCreateEntryAsync(vhost, ct).ConfigureAwait(false)).Connection;

        internal async Task<bool> CanConnectAsync(string vhost, CancellationToken ct)
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

        internal async ValueTask<HttpClient> GetOrCreateHttpClientAsync(CancellationToken ct)
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

                var mgmt = await server.ManagementEndpoint.GetValueAsync(ct).ConfigureAwait(false)
                    ?? throw new DistributedApplicationException(
                        "Management endpoint is not exposed. Call WithManagementPlugin().");
                var user = await server.UserNameReference.GetValueAsync(ct).ConfigureAwait(false);
                var pass = await server.PasswordParameter.GetValueAsync(ct).ConfigureAwait(false);
                _http = new HttpClient
                {
                    BaseAddress = new Uri(mgmt),
                    Timeout = TimeSpan.FromSeconds(5),
                };
                _http.DefaultRequestHeaders.Authorization =
                    new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{pass}")));
                return _http;
            }
            finally
            {
                _gate.Release();
            }
        }

        public async ValueTask DisposeAsync()
        {
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                foreach (var (_, (conn, ch)) in _channels)
                {
                    try { await ch.DisposeAsync().ConfigureAwait(false); } catch { }
                    try { await conn.DisposeAsync().ConfigureAwait(false); } catch { }
                }
                _channels.Clear();
                _http?.Dispose();
            }
            finally
            {
                _gate.Release();
                _gate.Dispose();
            }
        }
    }
}
