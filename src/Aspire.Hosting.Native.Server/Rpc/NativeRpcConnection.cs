// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Security.Cryptography;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Aspire.Hosting.Native.Diagnostics;
using Aspire.Hosting.Native.Api;
using Aspire.Hosting.Native.Server;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aspire.Hosting.Native.Rpc;

/// <summary>Processes authenticated JSON-RPC requests using generated ATS dispatch and scoped handles.</summary>
internal sealed partial class NativeRpcConnection : IDisposable
{
    private static readonly JsonObject s_contract = LoadContract();
    private static readonly HashSet<string> s_coreCapabilities = s_contract["Capabilities"]!.AsArray()
        .Select(capability => capability!["CapabilityId"]!.GetValue<string>()).ToHashSet(StringComparer.Ordinal);
    private readonly Lock _gate = new();
    private readonly NativeHandles _handles;
    private readonly NativeServerOptions _options;
    private readonly ILogger _logger;
    private readonly byte[] _token;
    private readonly NativeApplicationServer _server;
    private readonly bool _ownsServer;
    private readonly NativeLanguageCatalog? _languages;
    private readonly CancellationTokenSource _lifetime = new();
    private bool _authenticated;
    private bool _disposed;
    private int _inFlight;
    private INativeProviderRegistry? _providers;
    private INativeRpcPeer? _peer;
    private readonly Dictionary<string, ProviderCallback> _callbacks = new(StringComparer.Ordinal);
    private readonly Dictionary<(NativeRpcConnection, string, string), object> _proxyReferences = [];
    internal bool IsClosed => Volatile.Read(ref _disposed);
    internal CancellationToken Closed => _lifetime.Token;
    internal static JsonObject CoreContract => s_contract.DeepClone().AsObject();

    internal void AttachProviderTransport(INativeProviderRegistry providers, INativeRpcPeer peer)
    {
        _providers = providers;
        _peer = peer;
        _server.AttachProviderLifecycleDispatcher(providers);
    }

    internal JsonObject AddLifecycleScope(ResourceLifecycleScope scope)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _handles.RemoveRevoked();

            return _handles.Add(scope, NativeRpcContract.LifecycleScopeTypeId);
        }
    }

    internal void RemoveLifecycleScopes()
    {
        lock (_gate)
        {
            _handles.RemoveRevoked();
        }
    }

    internal JsonObject AddReference(object value, string typeId)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var identity = value switch
            {
                DelegatedCoreReference delegated => (delegated.Owner, RpcArguments.String(delegated.Handle["$handle"]), typeId),
                ProviderResourceReference resource => (resource.Provider.Connection, RpcArguments.String(resource.Handle["$handle"]), typeId),
                _ => throw new InvalidOperationException("Only delegated references are published through this boundary.")
            };
            foreach (var (key, reference) in _proxyReferences)
            {
                if (reference is ICapabilityLifetime { IsRevoked: true })
                {
                    _proxyReferences.Remove(key);
                }
            }
            if (_proxyReferences.TryGetValue(identity, out var existing))
            {
                if (value is ProviderResourceReference next && existing is ProviderResourceReference previous &&
                    !JsonNode.DeepEquals(next.Lifetime, previous.Lifetime))
                {
                    throw RpcFault.InvalidHandle("The provider resource lifetime cannot change.");
                }
                value = existing;
            }
            else
            {
                _handles.RemoveRevoked();
                _handles.EnsureCapacity();
                _proxyReferences.Add(identity, value);
            }
            return _handles.Add(value, typeId);
        }
    }

    internal object ReadReference(JsonObject handle, string? typeId = null)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _handles.ReadReference(handle, typeId);
        }
    }

    internal bool IsReferenceAlive(JsonObject handle)
    {
        try { _ = ReadReference(handle); return true; }
        catch (Exception exception) when (exception is RpcFault or ObjectDisposedException) { return false; }
    }

    internal ProviderCallbackLease AddCallback(ProviderCallback callback)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            foreach (var (existingId, existing) in _callbacks)
            {
                if (existing.IsRevoked)
                {
                    _callbacks.Remove(existingId);
                }
            }
            if (_callbacks.Count >= _options.MaximumHandlesPerConnection)
            {
                throw RpcFault.HandleLimit("The callback authority limit was reached.");
            }
            var id = RandomNumberGenerator.GetHexString(32);
            _callbacks.Add(id, callback);
            return new(this, id, callback);
        }
    }

    internal void RemoveCallback(string id)
    {
        lock (_gate)
        {
            _callbacks.Remove(id);
        }
    }

    internal ProviderCallback ReadCallback(string id)
    {
        lock (_gate)
        {
            if (!_callbacks.TryGetValue(id, out var callback) || callback.IsRevoked)
            {
                throw RpcFault.HandleNotFound("The callback authority is unknown or retired.");
            }
            return callback;
        }
    }

    internal Task<JsonNode?> RequestAsync(string method, JsonArray arguments) =>
        (_peer ?? throw RpcFault.NotSupported("Bidirectional RPC is not configured."))
        .RequestAsync(method, arguments, Closed);

    internal JsonObject PublishCoreHandle(object value, string typeId)
    {
        lock (_gate)
        {
            if (_disposed || value is ICapabilityLifetime { IsRevoked: true })
            {
                throw RpcFault.HandleNotFound("The capability owner is retired.");
            }
            _handles.RemoveRevoked();

            return _handles.Add(value, typeId);
        }
    }

    internal Func<T, Task> CreateAuthoringCallback<T>(NativeRpcConnection callbackConnection, string callbackId, string typeId) where T : class =>
        async context =>
        {
            // Delegated invocations execute under the resource owner's gate, but
            // their callbacks belong to the provider. Yield before publishing
            // across connections so their gates are never held together.
            await Task.Yield();
            var handle = PublishCoreHandle(context, typeId);
            if (!ReferenceEquals(this, callbackConnection))
            {
                handle = callbackConnection.AddReference(new DelegatedCoreReference(this, handle), typeId);
            }
            using var deadline = new CancellationTokenSource(_options.ProviderInvocationTimeout, _options.TimeProvider);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Closed, callbackConnection.Closed, deadline.Token);
            // Callback arguments use the same {"p0":{"$handle":...,"$type":...}}
            // envelope as the generated ATS SDK. Await outside the connection
            // gate so nested editor RPCs can complete on this connection.
            var arguments = new JsonObject { ["p0"] = handle };
            var response = await (callbackConnection._peer ?? throw RpcFault.NotSupported("Bidirectional RPC is not configured."))
                .RequestAsync("invokeCallback", new JsonArray(callbackId, arguments), timeout.Token)
                .ConfigureAwait(false);
            // TS void callbacks echo their original positional arguments for ATS
            // DTO writeback; C# void callbacks return null. Editors are handles,
            // not DTOs, so accept only the unchanged echo and never apply it.
            if (response is not null && !JsonNode.DeepEquals(arguments, response))
            {
                throw RpcFault.InvalidArgument("Authoring callbacks must return void.");
            }
        };

    internal async Task<JsonNode?> InvokeCoreAsync(string capability, JsonObject arguments, NativeRpcConnection callbackConnection)
    {
        Task<JsonNode?> invocation;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            invocation = NativeDispatch.InvokeAsync(_server, _handles, this, callbackConnection, capability, arguments);
            ObserveFault(invocation);
        }
        return await invocation.WaitAsync(Closed).ConfigureAwait(false);
    }

    public NativeRpcConnection(string token) : this(token, new NativeApplicationServer(), ownsServer: true, null)
    {
    }

    public NativeRpcConnection(string token, NativeApplicationServer server) : this(token, server, ownsServer: false, null)
    {
    }

    public NativeRpcConnection(string token, NativeApplicationServer server, NativeLanguageCatalog? languages)
        : this(token, server, ownsServer: false, languages)
    {
    }

    private NativeRpcConnection(string token, NativeApplicationServer server, bool ownsServer, NativeLanguageCatalog? languages)
        : this(token, server, ownsServer, languages, new())
    {
    }

    public NativeRpcConnection(string token, NativeApplicationServer server, NativeLanguageCatalog? languages, NativeServerOptions options)
        : this(token, server, ownsServer: false, languages, options, NullLogger.Instance)
    {
    }

    public NativeRpcConnection(string token, NativeApplicationServer server, NativeLanguageCatalog? languages, NativeServerOptions options,
        ILogger logger)
        : this(token, server, ownsServer: false, languages, options, logger)
    {
    }

    private NativeRpcConnection(string token, NativeApplicationServer server, bool ownsServer, NativeLanguageCatalog? languages,
        NativeServerOptions options) : this(token, server, ownsServer, languages, options, NullLogger.Instance)
    {
    }

    private NativeRpcConnection(string token, NativeApplicationServer server, bool ownsServer, NativeLanguageCatalog? languages,
        NativeServerOptions options, ILogger logger)
    {
        options.Validate();
        _options = options;
        _logger = logger;
        _handles = new(options.MaximumHandlesPerConnection);
        NativeRpcProtocol.ValidateAuthenticationToken(token);
        _token = Encoding.UTF8.GetBytes(token);
        _server = server;
        _ownsServer = ownsServer;
        _languages = languages;
    }

    internal int HandleCount
    {
        get
        {
            lock (_gate)
            {
                return _handles.Count;
            }
        }
    }

    internal bool IsAuthenticated
    {
        get
        {
            lock (_gate)
            {
                return _authenticated;
            }
        }
    }

    /// <summary>Processes one JSON-RPC request; notifications have no response.</summary>
    public byte[]? Process(ReadOnlyMemory<byte> payload) => ProcessAsync(payload).GetAwaiter().GetResult();

    /// <summary>Bounds and parses a standalone request before dispatch.</summary>
    public async Task<byte[]?> ProcessAsync(ReadOnlyMemory<byte> payload)
    {
        if (payload.Length > _options.MaximumRequestBytes)
        {
            return RejectRequest(-32600, "REQUEST_TOO_LARGE", "The request exceeds the size limit.");
        }
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(payload, new JsonDocumentOptions { MaxDepth = 32 });
        }
        catch (JsonException)
        {
            return RejectRequest(-32700, "PARSE_ERROR", "The request is not valid JSON.");
        }
        using (document)
        {
            return await ProcessAsync(document.RootElement).ConfigureAwait(false);
        }
    }

    /// <summary>Dispatches a bounded envelope without holding the gate while awaiting remote or user work.</summary>
    internal async Task<byte[]?> ProcessAsync(JsonElement envelope)
    {
        using var incomingTrace = StartIncomingTrace(envelope);
        using var operation = new NativeDiagnostics.Operation("rpc.request", null, null, _options.TimeProvider);
        JsonNode? id;
        string method;
        bool notification;
        Task<JsonNode?> invocation;
        CancellationToken lifetime;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            NativeRpcRequest request;
            try
            {
                request = NativeRpcProtocol.ParseRequest(envelope);
            }
            catch (JsonException)
            {
                return EncodeError(null, -32700, "PARSE_ERROR", "The request is not valid JSON.");
            }
            catch (RpcFault)
            {
                operation.ErrorType = "INVALID_REQUEST";
                return EncodeError(null, -32600, "INVALID_REQUEST", "The request shape is invalid.");
            }

            id = request.Id;
            method = request.Method;
            notification = request.IsNotification;
            if (_inFlight >= _options.MaximumConcurrentRequests)
            {
                return notification ? null : EncodeFailure(id, method, -32000, "RPC_BUSY", "The connection request limit was reached.");
            }
            _inFlight++;
            lifetime = _lifetime.Token;
            // Resolve arguments and authoritative handles under the gate, but
            // await completion outside it. A confirmation response on this same
            // connection must be able to unblock its outstanding request.
            invocation = InvokeAsync(method, request.Parameters);
            ObserveFault(invocation);
        }
        try
        {
            var result = await invocation.WaitAsync(lifetime).ConfigureAwait(false);
            operation.Succeeded = true;

            return notification ? null : Encode(new NativeRpcResponse
            {
                Id = id?.DeepClone(),
                Result = result is null
                    ? JsonSerializer.SerializeToElement((string?)null, NativeRpcMessageJsonContext.Default.String)
                    : JsonSerializer.SerializeToElement(result, NativeRpcMessageJsonContext.Default.JsonNode)
            });
        }
        catch (RpcFault fault)
        {
            operation.ErrorType = fault.FaultCode;
            var code = fault.FaultCode switch
            {
                "METHOD_NOT_FOUND" => -32601,
                "INVALID_ARGUMENT" or "INVALID_HANDLE" => -32602,
                _ => -32000
            };

            return notification ? null : EncodeFailure(id, method, code,
                fault.FaultCode, fault.Message);
        }
        catch (ObjectDisposedException)
        {
            operation.ErrorType = "HANDLE_NOT_FOUND";
            return notification ? null : EncodeFailure(id, method, -32000, "HANDLE_NOT_FOUND", "The capability lifetime has ended.");
        }
        catch (Exception exception) when (exception is ArgumentException or JsonException or FormatException)
        {
            operation.ErrorType = "INVALID_ARGUMENT";
            return notification ? null : EncodeFailure(id, method, -32602, "INVALID_ARGUMENT", "The capability arguments are invalid.");
        }
        catch (InvalidOperationException)
        {
            operation.ErrorType = "OPERATION_REJECTED";
            return notification ? null : EncodeFailure(id, method, -32000, "OPERATION_REJECTED", "The operation is not allowed in the current state.");
        }
        catch (NotSupportedException)
        {
            operation.ErrorType = "NOT_SUPPORTED";
            return notification ? null : EncodeFailure(id, method, -32000, "NOT_SUPPORTED", "The requested execution is not configured.");
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
            operation.ErrorType = "CONNECTION_CLOSED";
            return notification ? null : EncodeFailure(id, method, -32000, "CONNECTION_CLOSED", "The connection was closed.");
        }
        catch (OperationCanceledException)
        {
            operation.ErrorType = "EXECUTION_CANCELLED";
            return notification ? null : EncodeFailure(id, method, -32000, "EXECUTION_CANCELLED", "The execution was cancelled.");
        }
        catch (Exception exception)
        {
            // Executors and integration code can fail outside the known capability faults.
            // Export only the classification: messages and stack traces may contain secrets.
            operation.ErrorType = exception.GetType().FullName;
            _logger.LogError("Native request failed: {ErrorType}.", exception.GetType().Name);
            return notification ? null : EncodeFailure(id, method, -32603, "INTERNAL_ERROR", "The request failed internally.");
        }
        finally
        {
            lock (_gate)
            {
                _inFlight--;
                _handles.RemoveRevoked();
            }
        }
    }

    private async Task<JsonNode?> InvokeAsync(string method, JsonNode? parameters)
    {
        if (method == "authenticate")
        {
            return Authenticate(parameters);
        }
        if (!_authenticated)
        {
            throw RpcFault.Unauthenticated("Authenticate before using capabilities.");
        }
        return method switch
        {
            "ping" => JsonValue.Create("pong"),
            "getApplicationServer" => GetApplicationServer(parameters),
            "getCapabilities" => GetCapabilities(parameters),
            "getAppHostServerConfiguration" => GetAppHostServerConfiguration(parameters),
            "getRuntimeSpec" or "generateCode" => Bootstrap(method, parameters),
            "expectIntegrationHost" or "registerAsIntegrationHost" or "waitIntegrationHostRegistration" or
                "abandonIntegrationHostRegistration" or "getIntegrationCapabilities" or "invokeGuestCallback" =>
                await InvokeProviderControlAsync(method, parameters).ConfigureAwait(false),
            "invokeCapability" => await InvokeCapabilityAsync(parameters).ConfigureAwait(false),
            _ => throw RpcFault.MethodNotFound("The RPC method is not implemented.")
        };
    }

    private static Activity? StartIncomingTrace(JsonElement envelope)
    {
        if (NativeRpcProtocol.ReadTraceContext(envelope) is { } context)
        {
            var activity = new Activity("native.rpc.incoming")
                .SetParentId(context.TraceId, context.SpanId, context.TraceFlags);
            activity.TraceStateString = context.TraceState;

            return activity.Start();
        }

        return null;
    }

    private byte[] RejectRequest(int code, string fault, string message)
    {
        using var operation = new NativeDiagnostics.Operation("rpc.request", null, null, _options.TimeProvider);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            return EncodeError(null, code, fault, message);
        }
    }

    private static void ObserveFault(Task task) =>
        _ = task.ContinueWith(static completed => _ = completed.Exception, CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            _lifetime.Cancel();
            _providers?.Disconnect(this);
            _callbacks.Clear();
            _proxyReferences.Clear();
            _handles.Dispose();
            if (_ownsServer)
            {
                _server.Close();
            }
            CryptographicOperations.ZeroMemory(_token);
        }
    }

    private static byte[] EncodeError(JsonNode? id, int code, string classification, string message) => Encode(new NativeRpcResponse
    {
        Id = id?.DeepClone(),
        Error = new NativeRpcError(code, message, new NativeRpcErrorData(classification))
    });

    private static byte[] EncodeFailure(JsonNode? id, string method, int code, string classification, string message)
    {
        if (method != "invokeCapability")
        {
            return EncodeError(id, code, classification, message);
        }

        // ATS application failures are result payloads, not transport errors:
        // {"result":{"$error":{"code":"HANDLE_NOT_FOUND","message":"..."}}}.
        // The existing SDK converts this shape into CapabilityError.
        return Encode(new NativeRpcResponse
        {
            Id = id?.DeepClone(),
            Result = JsonSerializer.SerializeToElement(
                new NativeRpcCapabilityFailure(new NativeRpcCapabilityError(classification, message)),
                NativeRpcMessageJsonContext.Default.NativeRpcCapabilityFailure)
        });
    }

    private static byte[] Encode(NativeRpcResponse response) =>
        JsonSerializer.SerializeToUtf8Bytes(response, NativeRpcMessageJsonContext.Default.NativeRpcResponse);

    private static JsonObject LoadContract()
    {
        using var stream = typeof(NativeRpcConnection).Assembly.GetManifestResourceStream("Aspire.Hosting.Native.Rpc.contract.json")
            ?? throw new InvalidOperationException("The generated ATS contract bundle is missing.");

        return JsonNode.Parse(stream) as JsonObject
            ?? throw new InvalidOperationException("The generated ATS contract bundle is invalid.");
    }
}
