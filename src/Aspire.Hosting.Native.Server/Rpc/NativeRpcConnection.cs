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

namespace Aspire.Hosting.Native.Rpc;

/// <summary>Processes authenticated JSON-RPC requests using generated ATS dispatch and scoped handles.</summary>
internal sealed class NativeRpcConnection : IDisposable
{
    public const int MaximumRequestBytes = 256 * 1024;
    private static readonly JsonObject s_contract = LoadContract();
    private readonly Lock _gate = new();
    private readonly NativeHandles _handles;
    private readonly NativeServerOptions _options;
    private readonly byte[] _token;
    private readonly NativeApplicationServer _server;
    private readonly bool _ownsServer;
    private readonly NativeLanguageCatalog? _languages;
    private readonly CancellationTokenSource _lifetime = new();
    private bool _authenticated;
    private bool _disposed;
    private int _inFlight;

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
        : this(token, server, ownsServer: false, languages, options)
    {
    }

    private NativeRpcConnection(string token, NativeApplicationServer server, bool ownsServer, NativeLanguageCatalog? languages,
        NativeServerOptions options)
    {
        options.Validate();
        _options = options;
        _handles = new(options.MaximumHandlesPerConnection);
        ArgumentException.ThrowIfNullOrEmpty(token);
        _token = Encoding.UTF8.GetBytes(token);
        _server = server;
        _ownsServer = ownsServer;
        _languages = languages;
        if (_token.Length is < 32 or > 256)
        {
            throw new ArgumentException("RPC authentication tokens must contain 32 to 256 UTF-8 bytes.", nameof(token));
        }
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

    /// <summary>Dispatches a request without holding the connection gate while awaiting remote or user work.</summary>
    public async Task<byte[]?> ProcessAsync(ReadOnlyMemory<byte> payload)
    {
        using var incomingTrace = StartIncomingTrace(payload);
        using var operation = new NativeDiagnostics.Operation("rpc.request", null, null);
        JsonNode? id;
        string method;
        bool notification;
        Task<JsonNode?> invocation;
        CancellationToken lifetime;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (payload.Length > _options.MaximumRequestBytes)
            {
                return EncodeError(null, -32600, "REQUEST_TOO_LARGE", "The request exceeds the size limit.");
            }
            JsonObject request;
            try
            {
                // JSON-RPC uses {"jsonrpc":"2.0","id":1,"method":"invokeCapability",
                // "params":["assembly/capability",{"context":{"$handle":"...","$type":"..."}}]}.
                // Duplicate keys, including nested arguments, cannot be last-write-wins.
                using var document = JsonDocument.Parse(payload, new JsonDocumentOptions { MaxDepth = 32 });
                ValidateUniqueProperties(document.RootElement);
                request = JsonNode.Parse(document.RootElement.GetRawText()) as JsonObject
                    ?? throw new RpcFault("INVALID_REQUEST", "A JSON-RPC object is required.");
            }
            catch (JsonException)
            {
                return EncodeError(null, -32700, "PARSE_ERROR", "The request is not valid JSON.");
            }
            catch (RpcFault)
            {
                return EncodeError(null, -32600, "INVALID_REQUEST", "The request shape is invalid.");
            }

            id = request["id"];
            if (request["jsonrpc"]?.ToJsonString() != "\"2.0\"" ||
                request["method"] is not JsonValue methodValue || !methodValue.TryGetValue<string>(out var parsedMethod) ||
                !IsValidId(id) || request.Any(property => property.Key is not ("jsonrpc" or "id" or "method" or "params" or "traceparent" or "tracestate")) ||
                request["traceparent"] is not null && (request["traceparent"] is not JsonValue traceParent ||
                    !traceParent.TryGetValue<string>(out var parent) || parent.Length > 512) ||
                request["tracestate"] is not null && (request["tracestate"] is not JsonValue traceState ||
                    !traceState.TryGetValue<string>(out var state) || state.Length > 512))
            {
                operation.ErrorType = "INVALID_REQUEST";
                return EncodeError(null, -32600, "INVALID_REQUEST", "The request shape is invalid.");
            }
            // StreamJsonRpc adds bounded top-level trace context to requests:
            // {"jsonrpc":"2.0","method":"authenticate","params":[...],"id":1,
            //  "traceparent":"00-<trace-id>-<span-id>-01","tracestate":"vendor=value"}
            // See https://github.com/microsoft/vs-streamjsonrpc/blob/main/doc/tracecontext.md.
            method = parsedMethod;
            notification = !request.ContainsKey("id");
            if (_inFlight >= _options.MaximumConcurrentRequests)
            {
                return notification ? null : EncodeFailure(id, method, -32000, "RPC_BUSY", "The connection request limit was reached.");
            }
            _inFlight++;
            lifetime = _lifetime.Token;
            // Resolve arguments and authoritative handles under the gate, but
            // await completion outside it. A confirmation response on this same
            // connection must be able to unblock its outstanding request.
            invocation = InvokeAsync(method, request["params"]);
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
            if (parameters is not JsonArray { Count: 1 } authentication)
            {
                throw new RpcFault("INVALID_ARGUMENT", "Authentication requires one token.");
            }
            var supplied = Encoding.UTF8.GetBytes(RpcArguments.String(authentication[0]));
            try
            {
                _authenticated = CryptographicOperations.FixedTimeEquals(supplied, _token);

                return JsonValue.Create(_authenticated);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(supplied);
            }
        }
        if (!_authenticated)
        {
            throw new RpcFault("UNAUTHENTICATED", "Authenticate before using capabilities.");
        }
        if (_languages is not null && method is "getRuntimeSpec" or "generateCode")
        {
            if (parameters is not JsonArray { Count: > 0 and <= 2 } bootstrapArgs ||
                method == "getRuntimeSpec" && bootstrapArgs.Count != 1 ||
                bootstrapArgs.Count == 2 && bootstrapArgs[1] is not null)
            {
                throw new RpcFault("INVALID_ARGUMENT", "Bootstrap requires a language and does not support assembly filtering.");
            }
            return method == "getRuntimeSpec"
                ? _languages.RuntimeSpec(RpcArguments.String(bootstrapArgs[0]))
                : _languages.GeneratedSdk(RpcArguments.String(bootstrapArgs[0]));
        }
        if (method == "getCapabilities")
        {
            if (parameters is not null and not JsonArray { Count: 0 })
            {
                throw new RpcFault("INVALID_ARGUMENT", "Capability discovery takes no arguments.");
            }

            return s_contract.DeepClone();
        }
        if (method == "getApplicationServer")
        {
            if (parameters is not null and not JsonArray { Count: 0 })
            {
                throw new RpcFault("INVALID_ARGUMENT", "Server entry-point discovery takes no arguments.");
            }

            return _handles.Add(_server, "Aspire.Hosting.Native.Server/Aspire.Hosting.Native.Api.NativeApplicationServer");
        }
        if (method != "invokeCapability")
        {
            throw new RpcFault("METHOD_NOT_FOUND", "The RPC method is not implemented.");
        }
        if (parameters is not JsonArray { Count: 2 } arguments || arguments[1] is not JsonObject capabilityArguments)
        {
            throw new RpcFault("INVALID_ARGUMENT", "Capability invocation requires an ID and an argument object.");
        }

        return await NativeDispatch.InvokeAsync(_handles, RpcArguments.String(arguments[0]), capabilityArguments).ConfigureAwait(false);
    }

    private Activity? StartIncomingTrace(ReadOnlyMemory<byte> payload)
    {
        if (payload.Length > _options.MaximumRequestBytes || payload.Span.IndexOf("\"traceparent\""u8) < 0)
        {
            return null;
        }
        try
        {
            using var document = JsonDocument.Parse(payload, new JsonDocumentOptions { MaxDepth = 32 });
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty("traceparent", out var parent) || parent.ValueKind != JsonValueKind.String)
            {
                return null;
            }
            var state = document.RootElement.TryGetProperty("tracestate", out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString() : null;
            if (parent.GetString() is not { Length: <= 512 } parentValue || state is { Length: > 512 } ||
                !ActivityContext.TryParse(parentValue, state, out var context))
            {
                return null;
            }

            var activity = new Activity("native.rpc.incoming")
                .SetParentId(context.TraceId, context.SpanId, context.TraceFlags);
            activity.TraceStateString = state;

            return activity.Start();
        }
        catch (JsonException)
        {
            // This preliminary read is only for trace propagation. The normal
            // envelope parser still reports malformed input as a protocol error.
            return null;
        }
    }

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
            _lifetime.Dispose();
            _handles.Dispose();
            if (_ownsServer)
            {
                _server.Close();
            }
            CryptographicOperations.ZeroMemory(_token);
        }
    }

    private static bool IsValidId(JsonNode? id) => id is null ||
        id is JsonValue value && (value.TryGetValue<string>(out _) || value.TryGetValue<long>(out _));

    internal static void ValidateUniqueProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name))
                {
                    throw new RpcFault("INVALID_REQUEST", "Duplicate JSON properties are not allowed.");
                }
                ValidateUniqueProperties(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                ValidateUniqueProperties(item);
            }
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
