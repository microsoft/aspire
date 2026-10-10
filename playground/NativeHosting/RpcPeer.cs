// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace NativeHosting;

internal interface IRequestPeer
{
    Task<JsonNode?> RequestAsync(string method, JsonObject args, CancellationToken cancellationToken);
}

// This intentionally implements only the framing and operations needed by the spike.
// Private parent-owned stdio pipes are the trust boundary, not a public RPC endpoint.
internal sealed class RpcPeer : IDisposable, IRequestPeer
{
    private readonly Stream _input;
    private readonly Stream _output;
    private readonly SemaphoreSlim _writeGate = new(1);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonNode?>> _pending = new();
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _requests = new();
    private readonly object _requestGate = new();
    private readonly List<Task> _handlers = [];
    private readonly Func<string, JsonNode?, CancellationToken, Task<JsonNode?>> _handler;

    public RpcPeer(Func<string, JsonObject, CancellationToken, Task<JsonNode?>> handler)
        : this(Console.OpenStandardInput(), Console.OpenStandardOutput(),
            (method, args, token) => handler(method, args as JsonObject ?? throw new ArgumentException("Expected named RPC arguments."), token))
    {
    }

    public RpcPeer(Stream input, Stream output, Func<string, JsonNode?, CancellationToken, Task<JsonNode?>> handler)
    {
        _input = input;
        _output = output;
        _handler = handler;
    }

    public async Task<JsonNode?> RequestAsync(string method, JsonObject args, CancellationToken cancellationToken)
        => await RequestAsync(method, (JsonNode)args, cancellationToken);

    public async Task<JsonNode?> RequestAsync(string method, JsonNode args, CancellationToken cancellationToken)
    {
        var id = Guid.NewGuid().ToString("N");
        var completion = new TaskCompletionSource<JsonNode?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = completion;
        try
        {
            await WriteAsync(new JsonObject
            {
                ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method, ["params"] = args
            }, cancellationToken);
            return await completion.Task.WaitAsync(cancellationToken);
        }
        finally
        {
            _pending.TryRemove(id, out _);
            if (cancellationToken.IsCancellationRequested && !_shutdown.IsCancellationRequested)
            {
                await WriteAsync(new JsonObject
                {
                    ["jsonrpc"] = "2.0", ["method"] = "$/cancelRequest",
                    ["params"] = new JsonObject { ["id"] = id }
                }, _shutdown.Token);
            }
        }
    }

    public async Task RunAsync()
    {
        try
        {
            while (await ReadAsync(_shutdown.Token) is { } message)
            {
                if (message["jsonrpc"]?.GetValue<string>() != "2.0")
                {
                    throw new InvalidDataException("Expected JSON-RPC 2.0.");
                }

                if (message["method"] is null)
                {
                    var id = RequiredString(message, "id");
                    if (_pending.TryRemove(id, out var completion))
                    {
                        if (message["error"] is JsonObject error)
                        {
                            completion.SetException(new InvalidOperationException(RequiredString(error, "message")));
                        }
                        else
                        {
                            completion.SetResult(message["result"]?.DeepClone());
                        }
                    }
                }
                else if (message["method"]!.GetValue<string>() == "$/cancelRequest")
                {
                    var args = RequiredObject(message, "params");
                    lock (_requestGate)
                    {
                        if (_requests.TryGetValue(args["id"]!.ToJsonString(), out var request))
                        {
                            request.Cancel();
                        }
                    }
                }
                else
                {
                    var id = message["id"] ?? throw new InvalidDataException("Prototype requests require an id.");
                    var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
                    if (!_requests.TryAdd(id.ToJsonString(), cancellation))
                    {
                        cancellation.Dispose();
                        throw new InvalidDataException("Duplicate in-flight request id.");
                    }

                    // Keep reading while handlers await callbacks, including callbacks that
                    // re-enter this same process. A sequential reader deadlocks this flow.
                    await Task.WhenAll(_handlers.Where(task => task.IsCompleted));
                    _handlers.RemoveAll(task => task.IsCompletedSuccessfully);
                    _handlers.Add(HandleAsync(message, cancellation));
                }
            }
        }
        finally
        {
            _shutdown.Cancel();
            foreach (var pending in _pending.Values)
            {
                pending.TrySetException(new EndOfStreamException("Integration process disconnected."));
            }

            await Task.WhenAll(_handlers);
        }
    }

    private async Task HandleAsync(JsonObject message, CancellationTokenSource cancellation)
    {
        var id = message["id"]!.DeepClone();
        var method = RequiredString(message, "method");
        JsonObject response;
        try
        {
            var result = await _handler(method, message["params"], cancellation.Token);
            response = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = result };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"{method} failed: {ex.GetType().Name}");
            response = new JsonObject
            {
                ["jsonrpc"] = "2.0", ["id"] = id,
                ["error"] = new JsonObject
                {
                    ["code"] = ex is OperationCanceledException ? -32800 : -32603,
                    ["message"] = ex.Message
                }
            };
        }

        try
        {
            if (!_shutdown.IsCancellationRequested)
            {
                await WriteAsync(response, _shutdown.Token);
            }
        }
        finally
        {
            lock (_requestGate)
            {
                // Cancellation can race a completed reply. Do not dispose the
                // source while the reader is still delivering its cancellation.
                _requests.TryRemove(id.ToJsonString(), out _);
                cancellation.Dispose();
            }
        }
    }

    private async Task<JsonObject?> ReadAsync(CancellationToken cancellationToken)
    {
        // vscode-jsonrpc uses LSP framing: "Content-Length: <UTF-8 byte count>\r\n\r\n".
        // Read bytes, not characters: non-ASCII arguments have different byte counts.
        // https://microsoft.github.io/language-server-protocol/specifications/lsp/3.17/specification/#baseProtocol
        var header = new List<byte>();
        var oneByte = new byte[1];
        while (true)
        {
            if (await _input.ReadAsync(oneByte, cancellationToken) == 0)
            {
                return header.Count == 0 ? null : throw new EndOfStreamException("Truncated RPC header.");
            }

            header.Add(oneByte[0]);
            if (header.Count > 8192)
            {
                throw new InvalidDataException("RPC header exceeds 8 KiB.");
            }

            if (header.Count >= 4 && header[^4] == '\r' && header[^3] == '\n' && header[^2] == '\r' && header[^1] == '\n')
            {
                break;
            }
        }

        var lengthHeader = Encoding.ASCII.GetString(header.ToArray()).Split("\r\n", StringSplitOptions.RemoveEmptyEntries)
            .Single(line => line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase));
        var length = int.Parse(lengthHeader.AsSpan("Content-Length:".Length), CultureInfo.InvariantCulture);
        if (length is < 1 or > 1024 * 1024)
        {
            throw new InvalidDataException("RPC payload must be between 1 byte and 1 MiB.");
        }

        var payload = new byte[length];
        await _input.ReadExactlyAsync(payload, cancellationToken);
        return JsonNode.Parse(payload) as JsonObject ?? throw new InvalidDataException("RPC payload must be an object.");
    }

    private async Task WriteAsync(JsonObject message, CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(message, PrototypeJsonContext.Default.JsonObject);
        var header = Encoding.ASCII.GetBytes($"Content-Length: {payload.Length}\r\n\r\n");
        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            await _output.WriteAsync(header, cancellationToken);
            await _output.WriteAsync(payload, cancellationToken);
            await _output.FlushAsync(cancellationToken);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public static string RequiredString(JsonObject args, string key)
    {
        if (args[key] is not JsonValue value || !value.TryGetValue<string>(out var text) || string.IsNullOrWhiteSpace(text))
        {
            throw new ArgumentException($"'{key}' must be a nonempty string.");
        }

        return text;
    }

    public static JsonObject RequiredObject(JsonObject args, string key)
    {
        return args[key] as JsonObject ?? throw new ArgumentException($"'{key}' must be an object.");
    }

    public void Dispose()
    {
        _shutdown.Dispose();
        _writeGate.Dispose();
        _input.Dispose();
        _output.Dispose();
    }
}

[JsonSerializable(typeof(JsonObject))]
internal sealed partial class PrototypeJsonContext : JsonSerializerContext;
