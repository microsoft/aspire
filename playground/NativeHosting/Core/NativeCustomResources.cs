// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Concurrent;
using System.Text.Json.Nodes;

namespace NativeHosting;

internal sealed class NativeCustomResources(RpcPeer peer, ConcurrentDictionary<string, JsonObject> runtime) : IDisposable
{
    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte> _disconnected = new(StringComparer.Ordinal);

    public void Register(JsonObject definition)
    {
        var controller = RpcPeer.RequiredString(definition, "controllerOwner");
        _ = RpcPeer.RequiredString(definition, "control");
        if (definition["runOnly"]?.GetValue<bool>() != true)
        {
            throw new ArgumentException("The experimental custom-resource contract is run-only.");
        }

        if (!_entries.TryAdd(RpcPeer.RequiredString(definition, "name"), new Entry(controller)))
        {
            throw new InvalidOperationException("Custom resource already registered.");
        }
    }

    public async Task ControlAsync(JsonObject resource, string command, CancellationToken cancellationToken)
    {
        if (command is not ("start" or "stop" or "restart"))
        {
            throw new ArgumentException("Custom command must be start, stop, or restart.");
        }

        var name = RpcPeer.RequiredString(resource, "name");
        var entry = _entries[name];
        if (!await entry.Operation.WaitAsync(0, cancellationToken))
        {
            throw new InvalidOperationException($"A custom operation for '{name}' is already running.");
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, entry.Lifetime.Token);
        linked.CancelAfter(TimeSpan.FromMinutes(2));
        try
        {
            long generation;
            lock (entry.Gate)
            {
                if (_disconnected.ContainsKey(entry.Controller))
                {
                    throw new InvalidOperationException("Custom resource controller disconnected.");
                }

                generation = ++entry.Generation;
                entry.Revision = 0;
                entry.Terminal = command == "stop";
                runtime[name] = Snapshot(entry, command == "stop" ? "Stopping" : "Starting");
            }

            await peer.RequestAsync("invokeIntegration", new JsonObject
            {
                ["callback"] = RpcPeer.RequiredString(resource, "control"),
                ["resource"] = new JsonObject { ["owner"] = resource["owner"]!.DeepClone(), ["name"] = name },
                ["command"] = command, ["generation"] = generation
            }, linked.Token);
            lock (entry.Gate)
            {
                if (_disconnected.ContainsKey(entry.Controller) || entry.Generation != generation)
                {
                    throw new InvalidOperationException("Custom controller incarnation is no longer current.");
                }

                if (command == "stop")
                {
                    runtime[name] = Snapshot(entry, "Stopped");
                }
                else if (runtime[name]["state"]?.GetValue<string>() != "Healthy")
                {
                    throw new InvalidOperationException("Custom controller completed without publishing readiness.");
                }
            }
        }
        catch (Exception)
        {
            lock (entry.Gate)
            {
                entry.Terminal = true;
                runtime[name] = Snapshot(entry, _disconnected.ContainsKey(entry.Controller) ? "OwnerDisconnected" : "Failed");
            }

            Console.Error.WriteLine($"Custom '{name}' operation '{command}' failed; runtime endpoints were invalidated.");
            throw;
        }
        finally
        {
            entry.Operation.Release();
        }
    }

    public JsonObject Update(JsonObject resource, JsonObject args)
    {
        var name = RpcPeer.RequiredString(resource, "name");
        var entry = _entries[name];
        var controller = RpcPeer.RequiredString(args, "controllerOwner");
        var generation = args["generation"]!.GetValue<long>();
        var revision = args["revision"]!.GetValue<long>();
        var state = RpcPeer.RequiredString(args, "state");
        if (state is not ("Starting" or "Healthy" or "Unavailable" or "Failed"))
        {
            throw new ArgumentException("Unsupported custom state.");
        }

        var endpoints = new JsonObject();
        var logMessage = args["message"]?.GetValue<string>();
        if (logMessage?.Length > 512)
        {
            throw new ArgumentException("Custom log message exceeds 512 characters.");
        }

        if (args["endpoints"] is JsonObject reported && state == "Healthy")
        {
            var declared = RpcPeer.RequiredObject(resource, "endpoints");
            foreach (var endpoint in reported)
            {
                if (!declared.ContainsKey(endpoint.Key) || endpoint.Value is not JsonValue text ||
                    !Uri.TryCreate(text.GetValue<string>(), UriKind.Absolute, out var uri) ||
                    uri.Scheme is not ("http" or "https") || declared[endpoint.Key]?["scheme"]?.GetValue<string>() != uri.Scheme ||
                    uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0)
                {
                    throw new ArgumentException("Invalid custom endpoint allocation.");
                }

                endpoints[endpoint.Key] = new JsonObject
                {
                    ["url"] = uri.AbsoluteUri, ["host"] = uri.Host, ["port"] = uri.Port, ["scheme"] = uri.Scheme
                };
            }

            if (declared.Count != endpoints.Count)
            {
                throw new ArgumentException("Healthy custom resource must allocate every declared endpoint.");
            }
        }
        else if (state == "Healthy")
        {
            throw new ArgumentException("Healthy custom resource must report endpoint allocations.");
        }

        lock (entry.Gate)
        {
            if (controller != entry.Controller || _disconnected.ContainsKey(controller) || entry.Terminal ||
                generation != entry.Generation || generation == 0 || revision <= entry.Revision)
            {
                throw new InvalidOperationException("Custom owner, generation, or revision is stale or invalid.");
            }

            entry.Revision = revision;
            var snapshot = Snapshot(entry, state);
            if (state == "Healthy")
            {
                snapshot["endpoints"] = endpoints;
            }

            if (logMessage is not null)
            {
                entry.Logs.Enqueue(logMessage);
                while (entry.Logs.Count > 32)
                {
                    entry.Logs.Dequeue();
                }
            }

            snapshot["logs"] = new JsonArray(entry.Logs.Select(message => (JsonNode)JsonValue.Create(message)!).ToArray());
            if (state == "Failed")
            {
                entry.Terminal = true;
            }

            runtime[name] = snapshot;
            return (JsonObject)snapshot.DeepClone();
        }
    }

    // The prototype relay explicitly reports transport EOF. Production ownership
    // must derive this from an authenticated connection, not a caller-supplied ID.
    public void Disconnect(string controller)
    {
        _disconnected.TryAdd(controller, 0);
        foreach (var pair in _entries.Where(pair => pair.Value.Controller == controller))
        {
            lock (pair.Value.Gate)
            {
                pair.Value.Terminal = true;
                runtime[pair.Key] = Snapshot(pair.Value, "OwnerDisconnected");
            }

            pair.Value.Lifetime.Cancel();
        }
    }

    private static JsonObject Snapshot(Entry entry, string state) => new()
    {
        ["state"] = state, ["generation"] = entry.Generation, ["revision"] = entry.Revision
    };

    public void Dispose()
    {
        foreach (var entry in _entries.Values)
        {
            entry.Lifetime.Cancel();
            entry.Lifetime.Dispose();
            entry.Operation.Dispose();
        }
    }

    private sealed class Entry(string controller)
    {
        public string Controller { get; } = controller;
        public object Gate { get; } = new();
        public SemaphoreSlim Operation { get; } = new(1);
        public CancellationTokenSource Lifetime { get; } = new();
        public Queue<string> Logs { get; } = [];
        public long Generation { get; set; }
        public long Revision { get; set; }
        public bool Terminal { get; set; }
    }
}
