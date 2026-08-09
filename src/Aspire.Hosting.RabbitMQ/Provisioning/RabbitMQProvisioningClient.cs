// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Concurrent;
using System.Net;
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
    // Broker provisioning happens during startup, when the broker may still be warming up (connection
    // refused, HTTP 503, mid-restart channel drops). Every provisioning operation therefore retries a
    // transient failure up to MaxAttempts total tries with exponential backoff. Definitive failures
    // (a 404 "not found", an HTTP 4xx, cancellation) are never retried.
    private const int MaxAttempts = 5;
    private static readonly TimeSpan s_baseRetryDelay = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan s_maxRetryDelay = TimeSpan.FromSeconds(5);

    private readonly ILogger _logger;
    private readonly RabbitMQAmqpConnectionManager _amqp;

    public RabbitMQProvisioningClient(RabbitMQServerResource server, ILogger<RabbitMQProvisioningClient> logger)
    {
        _logger = logger;
        _amqp = new RabbitMQAmqpConnectionManager(server);
    }

    // Runs operation with bounded exponential-backoff retry. shouldRetry classifies a thrown exception as
    // transient (retry) vs definitive (rethrow immediately). Cancellation always rethrows and never counts
    // as a retryable attempt, so a superseded reconcile abandons work instead of looping.
    private async Task RunWithRetryAsync(Func<Task> operation, Func<Exception, bool> shouldRetry, string operationDescription, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await operation().ConfigureAwait(false);
                return;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (attempt < MaxAttempts && shouldRetry(ex))
            {
                var delay = ComputeBackoff(attempt);
                _logger.LogDebug(
                    "Transient failure {Operation} (attempt {Attempt}/{MaxAttempts}): {Error}. Retrying in {DelayMs}ms.",
                    operationDescription, attempt, MaxAttempts, ex.Message, delay.TotalMilliseconds);
                await Task.Delay(delay, ct).ConfigureAwait(false);
            }
        }
    }

    // Exponential backoff (200ms, 400ms, 800ms, 1600ms...) capped at s_maxRetryDelay, with up to 30% jitter
    // added to avoid thundering-herd retries when many resources reconcile against a warming broker at once.
    private static TimeSpan ComputeBackoff(int attempt)
    {
        var exponentialMs = s_baseRetryDelay.TotalMilliseconds * Math.Pow(2, attempt - 1);
        var cappedMs = Math.Min(exponentialMs, s_maxRetryDelay.TotalMilliseconds);
        var jitterMs = cappedMs * 0.3 * Random.Shared.NextDouble();
        return TimeSpan.FromMilliseconds(cappedMs + jitterMs);
    }

    // A RabbitMQ.Client exception is transient unless it is a definitive broker rejection. This retry is
    // complementary to the client's AutomaticRecoveryEnabled (set in GetOrCreateEntryAsync), not redundant:
    //   - BrokerUnreachableException is the initial-connect failure during broker warmup. Auto-recovery does
    //     NOT cover this (there is no established connection to recover), so retrying here is what makes a
    //     resource come up cleanly against a still-starting broker.
    //   - AlreadyClosedException is an in-flight call hitting a dropped channel/connection. Auto-recovery
    //     heals the connection in the background over ~5s, but the failed call still throws; a short retry
    //     rides out that window and re-issues on the recovered channel (GetOrCreateChannelAsync returns it).
    //   - A 404 NOT_FOUND is a definitive answer (entity missing), never retried.
    // AlreadyClosedException must precede OperationInterruptedException because it derives from it (and a
    // closed channel is never a 404).
    private static bool IsTransientAmqp(Exception ex) => ex switch
    {
        AlreadyClosedException => true,
        OperationInterruptedException oie => oie.ShutdownReason?.ReplyCode != 404,
        BrokerUnreachableException => true,
        _ => false,
    };

    // An HTTP failure is transient when the request never reached the server or the server was
    // unavailable. HttpPutAsync/HttpDeleteAsync throw a wrapped HttpRequestException on a retryable
    // status via EnsureRetryableStatus; a definitive 4xx surfaces as a non-retryable status and is
    // not wrapped, so it is not retried. TaskCanceledException here is an HTTP timeout (caller
    // cancellation is filtered earlier by the OperationCanceledException guard).
    private static bool IsTransientHttp(Exception ex) => ex is HttpRequestException or TaskCanceledException;

    private static bool IsRetryableStatus(HttpStatusCode status)
        => (int)status >= 500 || status == HttpStatusCode.RequestTimeout;

    public async ValueTask<IConnection> GetOrCreateConnectionAsync(string vhost, CancellationToken ct)
        => await _amqp.GetOrCreateConnectionAsync(vhost, ct).ConfigureAwait(false);

    public async Task<bool> CanConnectAsync(string vhost, CancellationToken ct)
        => await _amqp.CanConnectAsync(vhost, ct).ConfigureAwait(false);

    public bool ManagementEnabled => _amqp.ManagementEnabled;

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

    public async Task<bool> QueueExistsAsync(string vhost, string name, CancellationToken ct)
        => await AmqpExistsAsync(vhost, ch => ch.QueueDeclarePassiveAsync(name, ct), ct).ConfigureAwait(false);

    public async Task<bool> ExchangeExistsAsync(string vhost, string name, CancellationToken ct)
        => await AmqpExistsAsync(vhost, ch => ch.ExchangeDeclarePassiveAsync(name, ct), ct).ConfigureAwait(false);

    public async Task DeleteQueueAmqpAsync(string vhost, string name, CancellationToken ct)
    {
        _logger.LogDebug("Deleting queue '{Queue}' on vhost '{Vhost}' over AMQP.", name, vhost);
        // ifUnused/ifEmpty false => unconditional delete. Deleting a non-existent queue is a no-op on the broker.
        await AmqpAsync(vhost,
            ch => ch.QueueDeleteAsync(name, ifUnused: false, ifEmpty: false, cancellationToken: ct),
            $"Failed to delete queue '{name}' on vhost '{vhost}'", ct).ConfigureAwait(false);
    }

    public async Task DeleteExchangeAmqpAsync(string vhost, string name, CancellationToken ct)
    {
        _logger.LogDebug("Deleting exchange '{Exchange}' on vhost '{Vhost}' over AMQP.", name, vhost);
        // ifUnused false => unconditional delete. Deleting a non-existent exchange is a no-op on the broker.
        await AmqpAsync(vhost,
            ch => ch.ExchangeDeleteAsync(name, ifUnused: false, cancellationToken: ct),
            $"Failed to delete exchange '{name}' on vhost '{vhost}'", ct).ConfigureAwait(false);
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
            using var response = await http.GetAsync($"/api/vhosts/{Uri.EscapeDataString(vhost)}", ct).ConfigureAwait(false);
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
        // A single per-vhost IChannel is cached and shared. The RabbitMQ .NET client requires that a shared
        // IChannel is NOT used concurrently — overlapping operations interleave protocol frames and cause
        // continuation failures. Aspire fires ResourceReadyEvent per resource concurrently, so declares,
        // binds, passive-declare probes, and deletes on the same vhost can otherwise race on one channel.
        // Serialize all channel access per vhost. See:
        // https://www.rabbitmq.com/client-libraries/dotnet-api-guide#concurrency-channel-sharing
        var gate = _amqp.GetChannelGate(vhost);
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Each attempt fetches the channel fresh: a transient failure (channel dropped, broker
            // restarting) closes the channel, and GetOrCreateChannelAsync recreates it on the next attempt.
            await RunWithRetryAsync(
                async () =>
                {
                    var ch = await _amqp.GetOrCreateChannelAsync(vhost, ct).ConfigureAwait(false);
                    await action(ch).ConfigureAwait(false);
                },
                IsTransientAmqp,
                errorMessage,
                ct).ConfigureAwait(false);
        }
        // Cancellation flows through unwrapped so callers can distinguish a superseded reconcile / shutdown
        // from a genuine broker failure. Wrapping it caused exchange bindings to be recorded as permanent
        // failures on a superseded startup pass. See RabbitMQExchangeBindingReconciler.
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new DistributedApplicationException($"{errorMessage}: {ex.Message}", ex);
        }
        finally
        {
            gate.Release();
        }
    }

    // Runs an AMQP passive-declare probe under the per-vhost channel gate and maps the broker's 404 NOT_FOUND
    // reply into a boolean. A failed passive declare closes the channel (raises OperationInterruptedException
    // with replyCode 404); the next AmqpAsync/GetOrCreateChannelAsync transparently recreates it.
    private async Task<bool> AmqpExistsAsync(string vhost, Func<IChannel, Task> passiveDeclare, CancellationToken ct)
    {
        var gate = _amqp.GetChannelGate(vhost);
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // The 404 NOT_FOUND result is a definitive answer (entity absent), not a transient failure, so it
            // is caught inside the operation and returned as false — it never reaches the retry classifier.
            // A dropped channel or broker restart, however, is transient and retries with a fresh channel.
            var exists = false;
            await RunWithRetryAsync(
                async () =>
                {
                    var ch = await _amqp.GetOrCreateChannelAsync(vhost, ct).ConfigureAwait(false);
                    try
                    {
                        await passiveDeclare(ch).ConfigureAwait(false);
                        exists = true;
                    }
                    catch (OperationInterruptedException ex) when (ex.ShutdownReason?.ReplyCode == 404)
                    {
                        // Channel is now closed by the broker; it is recreated lazily on the next call.
                        exists = false;
                    }
                },
                IsTransientAmqp,
                $"probing existence on vhost '{vhost}'",
                ct).ConfigureAwait(false);
            return exists;
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task HttpPutAsync<T>(string path, T? body, string errorMessage, CancellationToken ct)
    {
        var http = await _amqp.GetOrCreateHttpClientAsync(ct).ConfigureAwait(false);
        try
        {
            await RunWithRetryAsync(
                async () =>
                {
                    // Dispose the response so its connection/content buffers are released promptly; these
                    // helpers run on repeated health probes and lifecycle commands, so leaked responses
                    // accumulate sockets.
                    using var response = body is null
                        ? await http.PutAsync(path, null, ct).ConfigureAwait(false)
                        : await http.PutAsJsonAsync(path, body, cancellationToken: ct).ConfigureAwait(false);
                    EnsureSuccessOrClassifiedThrow(response, errorMessage);
                },
                IsTransientHttp,
                errorMessage,
                ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (DistributedApplicationException)
        {
            // Already classified (definitive 4xx) — surface as-is without double-wrapping.
            throw;
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
            T? result = null;
            await RunWithRetryAsync(
                async () =>
                {
                    using var response = await http.GetAsync(path, ct).ConfigureAwait(false);
                    // A retryable status (5xx/408) throws so the loop retries; a definitive non-success
                    // (e.g. 404 "not found") is a valid "no value" answer for a drift read and returns null.
                    if (IsRetryableStatus(response.StatusCode))
                    {
                        throw new HttpRequestException($"Retryable status {(int)response.StatusCode} reading '{path}'.");
                    }

                    if (!response.IsSuccessStatusCode)
                    {
                        result = null;
                        return;
                    }

                    result = await response.Content.ReadFromJsonAsync<T>(cancellationToken: ct).ConfigureAwait(false);
                },
                IsTransientHttp,
                $"reading '{path}'",
                ct).ConfigureAwait(false);
            return result;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            // Drift reads are best-effort: after exhausting retries, treat any failure as "no live value".
            return null;
        }
    }

    private async Task HttpDeleteAsync(string path, string errorMessage, CancellationToken ct)
    {
        var http = await _amqp.GetOrCreateHttpClientAsync(ct).ConfigureAwait(false);
        try
        {
            await RunWithRetryAsync(
                async () =>
                {
                    using var response = await http.DeleteAsync(path, ct).ConfigureAwait(false);
                    // Deleting a non-existent entity is a no-op, not a failure.
                    if (response.StatusCode == HttpStatusCode.NotFound)
                    {
                        return;
                    }

                    EnsureSuccessOrClassifiedThrow(response, errorMessage);
                },
                IsTransientHttp,
                errorMessage,
                ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (DistributedApplicationException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new DistributedApplicationException($"{errorMessage}: {ex.Message}", ex);
        }
    }

    // Maps a management-API response onto the retry policy: 2xx returns; a retryable status (5xx/408) throws
    // HttpRequestException (transient — the loop retries); any other non-success is a definitive client error
    // (4xx) thrown as a non-retryable DistributedApplicationException so it fails fast.
    private static void EnsureSuccessOrClassifiedThrow(HttpResponseMessage response, string errorMessage)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        if (IsRetryableStatus(response.StatusCode))
        {
            throw new HttpRequestException($"{errorMessage}: retryable status {(int)response.StatusCode}.");
        }

        throw new DistributedApplicationException($"{errorMessage}: status {(int)response.StatusCode}.");
    }

    private sealed class RabbitMQAmqpConnectionManager(RabbitMQServerResource server) : IAsyncDisposable
    {
        private readonly ConcurrentDictionary<string, (IConnection connection, IChannel channel)> _channels = new(StringComparer.Ordinal);
        // One mutex per vhost guarding concurrent use of that vhost's shared IChannel. See AmqpAsync for why.
        private readonly ConcurrentDictionary<string, SemaphoreSlim> _channelGates = new(StringComparer.Ordinal);
        private readonly SemaphoreSlim _gate = new(1, 1);
        private HttpClient? _http;

        // Management is available only when the server exposes the management HTTP endpoint, which is added
        // exclusively by WithManagementPlugin(). Queue/exchange probe+delete fall back to AMQP when it isn't.
        public bool ManagementEnabled =>
            server.Annotations.OfType<EndpointAnnotation>().Any(e => e.Name == RabbitMQServerResource.ManagementEndpointName);

        // Returns the per-vhost channel mutex, creating it on first use. SemaphoreSlim instances are never
        // removed for the lifetime of the manager, so the returned gate is stable across calls for a vhost.
        public SemaphoreSlim GetChannelGate(string vhost)
            => _channelGates.GetOrAdd(vhost, static _ => new SemaphoreSlim(1, 1));

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

                foreach (var channelGate in _channelGates.Values)
                {
                    channelGate.Dispose();
                }
                _channelGates.Clear();
            }
            finally
            {
                _gate.Release();
                _gate.Dispose();
            }
        }
    }
}
