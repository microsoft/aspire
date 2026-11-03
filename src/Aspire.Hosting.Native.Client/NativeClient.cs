// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using StreamJsonRpc;

namespace Aspire.Hosting.Native.Client;

/// <summary>Owns an authenticated connection and its generated native ATS capabilities.</summary>
/// <remarks>
/// This type is the low-level transport projection. The separate Authoring namespace
/// records familiar builder commands and materializes them through this client.
/// Disposing the connection releases connection-owned authority. Cancellation stops waiting
/// for a call but cannot undo an operation already accepted by the native server.
/// </remarks>
public sealed class NativeClient : IAsyncDisposable
{
    private readonly JsonRpc _rpc;
    private readonly INativeAppHostRpc _serverRpc;
    private NativeProviderDefinition? _providerDefinition;
    private NativeProviderContext? _providerContext;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, Func<JsonElement, Task<JsonElement>>> _callbacks = new(StringComparer.Ordinal);
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, ProviderInvocation> _providerInvocations = new(StringComparer.Ordinal);
    internal EndPoint Endpoint { get; private set; } = null!;
    internal string AuthenticationToken { get; private set; } = "";
    private readonly TimeProvider _clock;
    private readonly Lock _disposalGate = new();
    private Task? _disposal;

    private NativeClient(NetworkStream stream, TimeProvider clock)
    {
        _clock = clock;
        _rpc = new JsonRpc(new HeaderDelimitedMessageHandler(stream, stream, new SystemTextJsonFormatter()));
        _serverRpc = _rpc.Attach<INativeAppHostRpc>();
        _rpc.AddLocalRpcMethod("getCapabilities", new Func<JsonElement>(() =>
            (_providerDefinition ?? throw new InvalidOperationException("This is not a provider connection.")).Metadata));
        _rpc.AddLocalRpcMethod("handleExternalCapability",
            new Func<string, JsonElement, string, Task<JsonElement>>(DispatchProviderAsync));
        _rpc.AddLocalRpcMethod("cancelProviderInvocation", new Func<string, Task<bool>>(CancelProviderInvocationAsync));
        _rpc.AddLocalRpcMethod("releaseIntegrationResource", new Func<string, Task<bool>>(ReleaseIntegrationResourceAsync));
        _rpc.AddLocalRpcMethod("invokeCallback",
            new Func<string, JsonElement, Task<JsonElement>>(DispatchCallbackAsync));
        _rpc.Disconnected += (_, _) =>
        {
            var cleanup = DisposeAsync().AsTask();
            _ = cleanup.ContinueWith(static completed => _ = completed.Exception, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        };
        _rpc.StartListening();
    }

    private async Task<bool> CancelProviderInvocationAsync(string id)
    {
        if (_providerInvocations.TryGetValue(id, out var invocation))
        {
            invocation.Cancel();
            await invocation.Completion.Task.ConfigureAwait(false);
        }

        return true;
    }

    private async Task<bool> ReleaseIntegrationResourceAsync(string id)
    {
        try
        {
            return await (_providerContext ?? throw new InvalidOperationException("This is not a provider connection."))
                .ReleaseResourceAsync(id).ConfigureAwait(false);
        }
        catch
        {
            throw new InvalidOperationException("The native integration object release failed.");
        }
    }

    private async Task<JsonElement> DispatchProviderAsync(string capability, JsonElement arguments, string invocationId)
    {
        var context = _providerContext ?? throw new InvalidOperationException("This is not a provider connection.");
        var invocation = new ProviderInvocation();
        if (!_providerInvocations.TryAdd(invocationId, invocation))
        {
            invocation.Complete();
            throw new InvalidOperationException("An integration invocation cannot be replayed.");
        }
        try
        {
            context.SetInvocation(invocation);
            return await (_providerDefinition ?? throw new InvalidOperationException("This is not a provider connection."))
                .Dispatch(context, capability, arguments).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Provider exceptions can include credentials. Do not serialize their
            // messages, stack traces or inner exceptions onto the RPC transport.
            throw new InvalidOperationException("The native integration invocation failed.");
        }
        finally
        {
            context.CompleteInvocation(invocation);
            _providerInvocations.TryRemove(invocationId, out _);
            invocation.Complete();
        }
    }

    private async Task<JsonElement> DispatchCallbackAsync(string id, JsonElement arguments)
    {
        try
        {
            return await (_callbacks.TryGetValue(id, out var callback) ? callback(arguments) :
                throw new InvalidOperationException("Unknown callback authority.")).ConfigureAwait(false);
        }
        catch (Exception)
        {
            throw new InvalidOperationException("The native guest callback failed.");
        }
    }

    /// <summary>Gets the generated application entry point after authentication.</summary>
    public NativeApplicationServer Server { get; private set; } = null!;

    /// <summary>Authenticates a connection to a native server on a local socket.</summary>
    /// <param name="endpoint">A Unix-domain socket or loopback TCP endpoint.</param>
    /// <param name="authenticationToken">The server's authentication token; never logged by this client.</param>
    /// <param name="authenticationTimeout">A positive bounded budget for connect and authentication.</param>
    /// <param name="timeProvider">The clock that governs the authentication deadline.</param>
    /// <param name="cancellationToken">Cancels connection establishment.</param>
    /// <returns>An authenticated client that owns its socket and capabilities.</returns>
    /// <exception cref="ArgumentException">The endpoint is not local or the timeout is invalid.</exception>
    public static async Task<NativeClient> ConnectAsync(EndPoint endpoint, string authenticationToken,
        TimeSpan authenticationTimeout, TimeProvider timeProvider, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentException.ThrowIfNullOrWhiteSpace(authenticationToken);
        if (endpoint is not UnixDomainSocketEndPoint &&
            (endpoint is not IPEndPoint ip || !IPAddress.IsLoopback(ip.Address)))
        {
            throw new ArgumentException("The native client requires a local endpoint.", nameof(endpoint));
        }
        if (authenticationTimeout <= TimeSpan.Zero || authenticationTimeout.TotalMilliseconds > int.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(authenticationTimeout));
        }
        using var timeout = new CancellationTokenSource(authenticationTimeout, timeProvider);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        var socket = new Socket(endpoint.AddressFamily, SocketType.Stream, ProtocolType.Unspecified);
        NativeClient? client = null;
        try
        {
            await socket.ConnectAsync(endpoint, deadline.Token).ConfigureAwait(false);
            client = new NativeClient(new NetworkStream(socket, ownsSocket: true), timeProvider);
            client.Endpoint = endpoint;
            client.AuthenticationToken = authenticationToken;
            var authenticated = await RequestAsync(() => client._serverRpc.AuthenticateAsync(authenticationToken),
                deadline.Token).ConfigureAwait(false);
            if (!authenticated)
            {
                throw new UnauthorizedAccessException("Native server authentication was rejected.");
            }
            var root = await RequestAsync(client._serverRpc.GetApplicationServerAsync,
                deadline.Token).ConfigureAwait(false);
            client.Server = new NativeApplicationServer(client, root);

            return client;
        }
        catch
        {
            client?._rpc.Dispose();
            socket.Dispose();
            throw;
        }
    }

    /// <summary>Encodes a same-connection handle for generated projection infrastructure.</summary>
    public JsonElement GetHandle(NativeRemoteObject value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (!ReferenceEquals(value.Client, this))
        {
            throw new ArgumentException("A capability belongs to a different native connection.", nameof(value));
        }

        return value.Handle;
    }

    /// <summary>Invokes an offline-generated capability projection, preserving its native error classification.</summary>
    public async Task<JsonElement> InvokeAsync(string capability, Dictionary<string, JsonElement> arguments,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // The native contract does not implement JSON-RPC $/cancelRequest. Bound
        // the caller's wait without pretending to cancel accepted server work.
        var result = await RequestAsync(() => _serverRpc.InvokeCapabilityAsync(capability, arguments),
            cancellationToken).ConfigureAwait(false);

        return NativeCapabilityResult.Parse(result);
    }

    private static async Task<T> RequestAsync<T>(Func<Task<T>> send, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var request = send();
        // The caller can stop waiting before the native request completes. Observe
        // any later transport fault, including faults caused by connection disposal.
        _ = request.ContinueWith(
            static completed => _ = completed.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

        return await request.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Creates one-use registration authority for an explicitly selected integration provider package.</summary>
    public Task<string> ExpectIntegrationHostAsync(string packageName, CancellationToken cancellationToken) =>
        RequestAsync(() => _serverRpc.ExpectIntegrationHostAsync(packageName), cancellationToken);
    /// <summary>Waits for explicit provider activation without log-based readiness or retries.</summary>
    public Task<bool> WaitIntegrationHostRegistrationAsync(string authority, CancellationToken cancellationToken) =>
        RequestAsync(() => _serverRpc.WaitIntegrationHostRegistrationAsync(authority), cancellationToken);

    /// <summary>Releases pending provider activation authority owned by this connection.</summary>
    /// <param name="authority">The authority returned by expected provider registration.</param>
    /// <param name="cancellationToken">Cancels the abandonment request.</param>
    /// <returns>Whether pending authority was removed; already removed authority returns false.</returns>
    public Task<bool> AbandonIntegrationHostRegistrationAsync(string authority, CancellationToken cancellationToken) =>
        RequestAsync(() => _serverRpc.AbandonIntegrationHostRegistrationAsync(authority), cancellationToken);
    /// <summary>Reads portable metadata for registered integration APIs without changing the static native contract.</summary>
    public Task<JsonElement> GetIntegrationCapabilitiesAsync(CancellationToken cancellationToken) =>
        RequestAsync(_serverRpc.GetIntegrationCapabilitiesAsync, cancellationToken);

    /// <summary>Hosts an offline-generated integration contract on this bidirectional connection.</summary>
    public async Task RegisterIntegrationHostAsync(string authority, NativeProviderDefinition definition,
        CancellationToken cancellationToken)
    {
        if (_providerDefinition is not null)
        {
            throw new InvalidOperationException("This connection already has a provider contract.");
        }
        _providerContext = new NativeProviderContext(this);
        _providerDefinition = definition;
        if (!await RequestAsync(() => _serverRpc.RegisterIntegrationHostAsync(authority), cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException("Provider registration was rejected.");
        }
    }

    /// <summary>Registers a guest callback for generated projected callback parameters.</summary>
    public void RegisterCallback(string id, Func<JsonElement, Task<JsonElement>> callback)
    {
        if (!_callbacks.TryAdd(id, callback))
        {
            throw new InvalidOperationException("The callback authority is already registered.");
        }
    }

    /// <summary>Releases a completed guest callback's connection-local registration.</summary>
    /// <param name="id">The identifier supplied when registering the callback.</param>
    public void UnregisterCallback(string id) => _callbacks.TryRemove(id, out _);

    internal Task WaitForDisconnectAsync(CancellationToken cancellationToken) => _rpc.Completion.WaitAsync(cancellationToken);
    internal Task<JsonElement> InvokeGuestCallbackAsync(string callback, Dictionary<string, JsonElement> arguments) =>
        RequestAsync(() => _serverRpc.InvokeGuestCallbackAsync(callback, arguments), CancellationToken.None);

    /// <summary>Closes the connection and releases its server-owned capability lifetimes.</summary>
    /// <returns>The shared disposal operation, including provider-local object release.</returns>
    public ValueTask DisposeAsync()
    {
        lock (_disposalGate)
        {
            return new(_disposal ??= DisposeCoreAsync());
        }
    }

    private async Task DisposeCoreAsync()
    {
        // Publish the shared disposal task before closing transport. JsonRpc can
        // synchronously raise Disconnected and reenter this disposal boundary.
        await Task.Yield();
        _rpc.Dispose();
        var active = _providerInvocations.Values.ToArray();
        foreach (var invocation in active)
        {
            invocation.Cancel();
        }
        try
        {
            if (_providerContext is { } context)
            {
                // Native authority is already revoked. Bound the caller's wait,
                // but never dispose local state while an invocation still uses it.
                var cleanup = context.ClearAsync();
                _ = cleanup.ContinueWith(static completed => _ = completed.Exception, CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                await cleanup.WaitAsync(TimeSpan.FromSeconds(15), _clock).ConfigureAwait(false);
            }
        }
        finally
        {
            _providerDefinition = null;
            _callbacks.Clear();
        }
    }

    /// <summary>Tracks invocation cancellation, completion and resource leases.</summary>
    internal sealed class ProviderInvocation
    {
        private readonly Lock _gate = new();
        private bool _completed;
        public CancellationTokenSource Cancellation { get; } = new();
        public CancellationToken CancellationToken { get; }
        public TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public HashSet<NativeProviderContext.ResourceEntry> Resources { get; } = [];
        public bool ResourceAccessClosed { get; set; }

        public ProviderInvocation()
        {
            CancellationToken = Cancellation.Token;
        }

        public void Cancel()
        {
            lock (_gate)
            {
                if (!_completed)
                {
                    Cancellation.Cancel();
                }
            }
        }
        public void Complete()
        {
            lock (_gate)
            {
                _completed = true;
                Completion.TrySetResult();
                Cancellation.Dispose();
            }
        }
    }
}
