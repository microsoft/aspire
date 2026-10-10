// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json.Nodes;

namespace NativeHosting;

internal sealed class NativeModel(string owner, IRequestPeer peer) : IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, JsonObject> _resources = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, JsonObject> _runtime = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, string> _secrets = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, Lazy<Task>> _starts = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _operations = new(StringComparer.OrdinalIgnoreCase);
    private readonly NativeDcp _dcp = new();
    private readonly SemaphoreSlim _definitionGate = new(1);
    private readonly SemaphoreSlim _networkGate = new(1);
    private readonly string _network = $"native-{owner[..10]}";
    private volatile bool _sealed;
    private Task? _initialStart;
    private bool _networkCreated;
    private NativeCustomResources? _custom;
    private readonly object _customGate = new();
    private NativeCustomResources Custom
    {
        get
        {
            lock (_customGate)
            {
                return _custom ??= new(peer, _runtime);
            }
        }
    }

    public async Task<JsonNode?> InvokeAsync(string method, JsonObject args, CancellationToken cancellationToken)
    {
        if (method == "configure")
        {
            using var startup = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            startup.CancelAfter(TimeSpan.FromSeconds(45));
            await _dcp.StartAsync(RpcPeer.RequiredString(args, "dcp"), startup.Token);
            return null;
        }

        if (method == "define")
        {
            var definition = (JsonObject)RpcPeer.RequiredObject(args, "definition").DeepClone();
            var name = RpcPeer.RequiredString(definition, "name");
            if (name.Length is 0 or > 40 || name.Any(c => !(char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '-')) || !char.IsAsciiLetterLower(name[0]))
            {
                throw new ArgumentException("Native model names must be lowercase DNS labels of at most 40 characters.");
            }

            var kind = RpcPeer.RequiredString(definition, "kind");
            if (kind is not ("parameter" or "container" or "executable" or "value" or "custom"))
            {
                throw new ArgumentException("Unsupported primitive resource kind.");
            }

            await _definitionGate.WaitAsync(cancellationToken);
            try
            {
                if (_sealed)
                {
                    throw new InvalidOperationException("The native model is sealed for execution.");
                }

                definition["owner"] = owner;
                if (_resources.ContainsKey(name))
                {
                    throw new InvalidOperationException($"Native resource '{name}' already exists.");
                }

                if (kind == "custom")
                {
                    Custom.Register(definition);
                }

                if (!_resources.TryAdd(name, definition))
                {
                    throw new InvalidOperationException($"Native resource '{name}' already exists.");
                }
            }
            finally
            {
                _definitionGate.Release();
            }

            return Handle(name);
        }

        if (method is "publish" or "stats")
        {
            if (method == "stats")
            {
                using var process = System.Diagnostics.Process.GetCurrentProcess();
                return new JsonObject
                {
                    ["resources"] = _resources.Count, ["workingSetBytes"] = process.WorkingSet64,
                    ["dynamicCodeSupported"] = System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeSupported
                };
            }

            // Canonical structured configuration only. Runtime allocations and
            // generated secrets live in separate registries and never appear here.
            return new JsonObject
            {
                ["format"] = "native-model.v0",
                ["resources"] = new JsonArray(_resources.Where(entry => entry.Value["runOnly"]?.GetValue<bool>() != true).OrderBy(entry => entry.Key, StringComparer.Ordinal)
                    .Select(entry => (JsonNode)entry.Value.DeepClone()).ToArray())
            };
        }

        if (method == "startAll")
        {
            var starting = await SealAsync(cancellationToken);
            await starting;
            return null;
        }

        if (method == "ownerDisconnected")
        {
            var controller = RpcPeer.RequiredString(args, "controllerOwner");
            Custom.Disconnect(controller);
            foreach (var resource in _resources.Values.Where(resource =>
                resource["controllerOwner"]?.GetValue<string>() == controller &&
                RpcPeer.RequiredString(resource, "kind") == "executable"))
            {
                var name = RpcPeer.RequiredString(resource, "name");
                // Fence the provider first, then remove the controller's DCP process.
                // Direct transport ownership is still supplied by the prototype relay.
                _runtime[name] = new JsonObject { ["state"] = "OwnerDisconnected" };
                await _dcp.DeleteAsync("executables", PhysicalName(name), cancellationToken);
            }

            return null;
        }

        var target = Get(RpcPeer.RequiredObject(args, "resource"));
        var targetName = RpcPeer.RequiredString(target, "name");
        if (method == "describe")
        {
            return target.DeepClone();
        }

        if (method == "resolve")
        {
            var mode = RpcPeer.RequiredString(args, "mode");
            if (mode is not ("run" or "publish"))
            {
                throw new ArgumentException("Mode must be run or publish.");
            }

            var network = args["network"]?.GetValue<string>() ?? "host";
            if (network is not ("host" or "container"))
            {
                throw new ArgumentException("Network must be host or container.");
            }

            var expression = RpcPeer.RequiredObject(target, "properties")[RpcPeer.RequiredString(args, "property")]
                ?? throw new ArgumentException("Unknown connection property.");
            return JsonValue.Create(await ResolveAsync(expression, mode, network, Handle(targetName), [], cancellationToken));
        }

        if (method == "status")
        {
            return _runtime.TryGetValue(targetName, out var state) ? state.DeepClone() :
                new JsonObject { ["state"] = "NotStarted" };
        }

        if (RpcPeer.RequiredString(target, "kind") == "custom" && method is "updateCustom" or "command")
        {
            if (method == "updateCustom")
            {
                return Custom.Update(target, args);
            }

            if (!_sealed || (_starts.TryGetValue(targetName, out var startingCustom) && !startingCustom.Value.IsCompleted))
            {
                throw new InvalidOperationException("Custom resource has not completed initial startup.");
            }

            await Custom.ControlAsync(target, RpcPeer.RequiredString(args, "command"), cancellationToken);
            return null;
        }

        if (method is "computeStatus" or "readLogs")
        {
            if (RpcPeer.RequiredString(target, "kind") != "executable")
            {
                throw new ArgumentException("Executable observation requires an executable resource.");
            }

            var observed = await _dcp.GetAsync("executables", PhysicalName(targetName), cancellationToken);
            var status = observed?["status"] as JsonObject ?? throw new InvalidOperationException("Executable is not running in DCP.");
            if (method == "computeStatus")
            {
                return new JsonObject { ["state"] = status["state"]?.DeepClone(), ["pid"] = status["pid"]?.DeepClone(), ["executionId"] = status["executionID"]?.DeepClone() };
            }

            return new JsonObject
            {
                ["stdout"] = await _dcp.ReadLogsAsync(PhysicalName(targetName), "stdout", args["stdoutOffset"]?.GetValue<long>() ?? 0, cancellationToken),
                ["stderr"] = await _dcp.ReadLogsAsync(PhysicalName(targetName), "stderr", args["stderrOffset"]?.GetValue<long>() ?? 0, cancellationToken),
                ["executionId"] = status["executionID"]?.DeepClone()
            };
        }

        if (method is "restart" or "stop")
        {
            var kind = RpcPeer.RequiredString(target, "kind");
            if (kind is not ("container" or "executable"))
            {
                throw new ArgumentException("Only compute resources can stop or restart.");
            }

            var gate = _operations.GetOrAdd(targetName, _ => new SemaphoreSlim(1));
            if (!await gate.WaitAsync(0, cancellationToken))
            {
                throw new InvalidOperationException($"A native operation for '{targetName}' is already running.");
            }

            try
            {
                if (!_sealed || (_starts.TryGetValue(targetName, out var starting) && !starting.Value.IsCompleted))
                {
                    throw new InvalidOperationException($"Native resource '{targetName}' has not completed initial startup.");
                }

                // Fence old allocations before deleting the old workload. A deferred
                // expression must fail rather than return the old port during restart.
                _runtime.TryRemove(targetName, out _);
                _starts.TryRemove(targetName, out _);
                await _dcp.DeleteAsync(kind == "container" ? "containers" : "executables", PhysicalName(targetName), cancellationToken);
                if (target["endpoints"] is JsonObject endpoints)
                {
                    foreach (var endpoint in endpoints)
                    {
                        await _dcp.DeleteAsync("services", PhysicalName(targetName + "-" + endpoint.Key), cancellationToken);
                    }
                }

                if (method == "restart")
                {
                    await StartAsync(target, cancellationToken);
                }
                else
                {
                    _runtime[targetName] = new JsonObject { ["state"] = "Stopped" };
                }
            }
            finally
            {
                gate.Release();
            }

            return null;
        }

        throw new InvalidOperationException($"Unknown native model operation '{method}'.");
    }

    private async Task<Task> SealAsync(CancellationToken cancellationToken)
    {
        await _definitionGate.WaitAsync(cancellationToken);
        try
        {
            if (_initialStart is not null)
            {
                return _initialStart;
            }

            // Validate the complete graph before any side effects. Detecting a cycle
            // after starting tasks would deadlock their mutual readiness waits.
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var resource in _resources.Values)
            {
                Visit(resource, []);
            }

            _sealed = true;
            // Only initial startup fans out over the entire model. A repeated
            // startAll joins that operation, rather than racing a targeted restart.
            _initialStart = Task.WhenAll(_resources.Values.Select(resource => StartAsync(resource, cancellationToken)));
            return _initialStart;

            void Visit(JsonObject resource, HashSet<string> path)
            {
                var name = RpcPeer.RequiredString(resource, "name");
                if (!path.Add(name))
                {
                    throw new InvalidOperationException($"Native dependency cycle at '{name}'.");
                }

                if (visited.Add(name) && resource["dependencies"] is JsonArray dependencies)
                {
                    foreach (var dependency in dependencies)
                    {
                        Visit(Get(dependency!.AsObject()), path);
                    }
                }

                path.Remove(name);
            }
        }
        finally
        {
            _definitionGate.Release();
        }
    }

    private Task StartAsync(JsonObject resource, CancellationToken cancellationToken)
    {
        var name = RpcPeer.RequiredString(resource, "name");
        return _starts.GetOrAdd(name, _ => new Lazy<Task>(() => StartResourceAsync(resource, cancellationToken))).Value;
    }

    private async Task StartResourceAsync(JsonObject resource, CancellationToken cancellationToken)
    {
        var name = RpcPeer.RequiredString(resource, "name");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var ct = timeout.Token;
        try
        {
            if (resource["dependencies"] is JsonArray dependencies)
            {
                await Task.WhenAll(dependencies.Select(dependency => StartAsync(Get(dependency!.AsObject()), cancellationToken)));
            }

            // Dependencies have independent startup budgets. Start this resource's
            // budget after they are ready, so slow parents do not starve its setup.
            timeout.CancelAfter(TimeSpan.FromMinutes(3));
            var kind = RpcPeer.RequiredString(resource, "kind");
            if (kind == "parameter")
            {
                _secrets.GetOrAdd(name, _ => RandomNumberGenerator.GetString("abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789", 22));
            }
            else if (kind is "container" or "executable")
            {
                if (resource["beforeStart"] is JsonValue beforeStart)
                {
                    await CallbackAsync(beforeStart.GetValue<string>(), name, ct);
                }

                await StartComputeAsync(resource, kind, ct);
            }
            else if (kind == "custom")
            {
                await Custom.ControlAsync(resource, "start", ct);
                return;
            }

            if (resource["initialize"] is JsonValue initialize)
            {
                await CallbackAsync(initialize.GetValue<string>(), name, ct);
            }

            if (resource["health"] is JsonValue health)
            {
                while (true)
                {
                    var probe = await CallbackAsync(health.GetValue<string>(), name, ct);
                    if (probe?["ready"]?.GetValue<bool>() == true)
                    {
                        break;
                    }

                    Console.Error.WriteLine($"Native '{name}' is waiting for its integration health probe.");
                    await Task.Delay(250, ct);
                }
            }

            _runtime.AddOrUpdate(name, _ => new JsonObject { ["state"] = "Healthy" }, (_, state) =>
            {
                var copy = (JsonObject)state.DeepClone();
                copy["state"] = "Healthy";
                return copy;
            });
        }
        catch (Exception)
        {
            if (RpcPeer.RequiredString(resource, "kind") != "custom")
            {
                _runtime[name] = new JsonObject { ["state"] = "FailedToStart" };
            }
            Console.Error.WriteLine($"Native resource '{name}' failed to become ready.");
            throw;
        }
    }

    private async Task StartComputeAsync(JsonObject resource, string kind, CancellationToken cancellationToken)
    {
        var name = RpcPeer.RequiredString(resource, "name");
        var endpoints = resource["endpoints"] as JsonObject ?? new JsonObject();
        var produced = new JsonArray();
        foreach (var endpoint in endpoints)
        {
            var service = PhysicalName(name + "-" + endpoint.Key);
            await _dcp.CreateAsync("services", service,
                new JsonObject { ["protocol"] = "TCP", ["addressAllocationMode"] = kind == "container" ? "Proxyless" : "Localhost" }, null, cancellationToken);
            produced.Add((JsonNode)new JsonObject
            {
                ["serviceName"] = service, ["address"] = "127.0.0.1",
                ["port"] = kind == "container" ? endpoint.Value!["targetPort"]!.DeepClone() : null
            });
        }

        var environment = new JsonArray();
        if (resource["environment"] is JsonObject variables)
        {
            foreach (var entry in variables)
            {
                environment.Add((JsonNode)new JsonObject
                {
                    ["name"] = entry.Key, ["value"] = await ResolveAsync(entry.Value!, "run", kind == "container" ? "container" : "host", Handle(name), [], cancellationToken)
                });
            }
        }

        // DCP's portForServing substitution allocates a workload port without the
        // reserve/release race in the original harness. Service-producer is stored
        // as a JSON array encoded inside a Kubernetes annotation string.
        foreach (var endpoint in endpoints)
        {
            if (kind == "executable" && endpoint.Value!["environment"] is JsonValue variable)
            {
                environment.Add((JsonNode)new JsonObject
                {
                    ["name"] = variable.GetValue<string>(), ["value"] = "{{- portForServing \"" + PhysicalName(name + "-" + endpoint.Key) + "\" -}}"
                });
            }
        }

        var arguments = new JsonArray();
        if (resource["arguments"] is JsonArray sourceArguments)
        {
            foreach (var argument in sourceArguments)
            {
                arguments.Add((JsonNode)JsonValue.Create(await ResolveAsync(argument!, "run", kind == "container" ? "container" : "host", Handle(name), [], cancellationToken))!);
            }
        }

        var spec = new JsonObject { ["env"] = environment, ["args"] = arguments };
        if (kind == "container")
        {
            await EnsureNetworkAsync(cancellationToken);
            spec["image"] = RpcPeer.RequiredString(resource, "image");
            spec["command"] = resource["command"]?.DeepClone();
            spec["ports"] = new JsonArray(endpoints.Select(endpoint => (JsonNode)new JsonObject
            {
                ["containerPort"] = endpoint.Value!["targetPort"]!.DeepClone(), ["hostIP"] = "127.0.0.1", ["protocol"] = "TCP"
            }).ToArray());
            spec["networks"] = new JsonArray(new JsonObject { ["name"] = _network, ["aliases"] = new JsonArray(name) });
            if (resource["volumes"] is JsonArray volumes)
            {
                var mounts = new JsonArray();
                foreach (var volume in volumes)
                {
                    var logicalName = RpcPeer.RequiredString(volume!.AsObject(), "name");
                    var volumeName = PhysicalName(logicalName);
                    // The session owns the volume, but restarting a container retains
                    // it. Session cleanup deletes this non-persistent DCP volume.
                    await _networkGate.WaitAsync(cancellationToken);
                    try
                    {
                        if (await _dcp.GetAsync("containervolumes", volumeName, cancellationToken) is null)
                        {
                            await _dcp.CreateAsync("containervolumes", volumeName, new JsonObject { ["name"] = volumeName, ["persistent"] = false }, null, cancellationToken);
                        }
                    }
                    finally
                    {
                        _networkGate.Release();
                    }

                    mounts.Add((JsonNode)new JsonObject { ["type"] = "volume", ["source"] = volumeName, ["target"] = volume!["target"]!.DeepClone() });
                }

                spec["volumeMounts"] = mounts;
            }

            if (resource["bindMounts"] is JsonArray bindMounts)
            {
                if (spec["volumeMounts"] is not JsonArray mounts)
                {
                    spec["volumeMounts"] = mounts = [];
                }

                foreach (var mount in bindMounts)
                {
                    var source = RpcPeer.RequiredString(mount!.AsObject(), "source");
                    if (!Path.IsPathFullyQualified(source) || !Directory.Exists(source))
                    {
                        throw new ArgumentException("A bind mount requires an existing absolute host directory.");
                    }

                    mounts.Add((JsonNode)new JsonObject
                    {
                        ["type"] = "bind", ["source"] = source, ["target"] = RpcPeer.RequiredString(mount.AsObject(), "target")
                    });
                }
            }
        }
        else
        {
            spec["executablePath"] = RpcPeer.RequiredString(resource, "executable");
            spec["workingDirectory"] = RpcPeer.RequiredString(resource, "directory");
            spec["executionType"] = "Process";
        }

        var annotations = new JsonObject { ["service-producer"] = produced.ToJsonString(), ["resource-name"] = name };
        var collection = kind == "container" ? "containers" : "executables";
        await _dcp.CreateAsync(collection, PhysicalName(name), spec, annotations, cancellationToken);
        JsonObject observed;
        while (true)
        {
            observed = (await _dcp.GetAsync(collection, PhysicalName(name), cancellationToken))!;
            var state = observed["status"]?["state"]?.GetValue<string>();
            if (state == "Running")
            {
                break;
            }

            if (state is "FailedToStart" or "Exited" or "Finished")
            {
                var containerId = observed["status"]?["containerId"]?.GetValue<string>();
                throw new InvalidOperationException($"DCP resource '{name}' entered {state}." +
                    (containerId is not null ? $" Container: {containerId}." : ""));
            }

            await Task.Delay(100, cancellationToken);
        }

        var allocation = new JsonObject();
        foreach (var endpoint in endpoints)
        {
            while (true)
            {
                var service = await _dcp.GetAsync("services", PhysicalName(name + "-" + endpoint.Key), cancellationToken);
                if (service?["status"]?["effectivePort"] is JsonValue port && port.GetValue<int>() > 0)
                {
                    allocation[endpoint.Key] = new JsonObject
                    {
                        ["host"] = service["status"]!["effectiveAddress"]!.DeepClone(), ["port"] = port.DeepClone()
                    };
                    break;
                }

                await Task.Delay(100, cancellationToken);
            }
        }

        _runtime[name] = new JsonObject
        {
            ["state"] = "Running", ["endpoints"] = allocation,
            ["containerId"] = observed["status"]?["containerId"]?.DeepClone(),
            ["pid"] = observed["status"]?["pid"]?.DeepClone()
        };
    }

    private async Task<string> ResolveAsync(JsonNode expression, string mode, string network, JsonObject consumer, HashSet<string> path, CancellationToken cancellationToken)
    {
        if (expression is JsonValue literal)
        {
            return literal.TryGetValue<string>(out var text) ? text : literal.GetValue<int>().ToString(CultureInfo.InvariantCulture);
        }

        var node = expression.AsObject();
        var kind = RpcPeer.RequiredString(node, "kind");
        string value;
        if (kind == "concat")
        {
            var parts = new List<string>();
            foreach (var part in node["items"]!.AsArray())
            {
                parts.Add(await ResolveAsync(part!, mode, network, consumer, path, cancellationToken));
            }

            return string.Concat(parts);
        }

        if (kind == "remote")
        {
            return (await peer.RequestAsync("resolveOwner", new JsonObject
            {
                ["reference"] = node["resource"]!.DeepClone(), ["property"] = node["property"]!.DeepClone(),
                ["consumer"] = consumer.DeepClone(), ["mode"] = mode, ["network"] = network
            }, cancellationToken))!.GetValue<string>();
        }

        var resource = Get(RpcPeer.RequiredObject(node, "resource"));
        var name = RpcPeer.RequiredString(resource, "name");
        if (mode == "publish" && resource["runOnly"]?.GetValue<bool>() == true)
        {
            throw new InvalidOperationException("Run-only resource values cannot be consumed in publication.");
        }
        if (kind == "parameter")
        {
            if (mode == "publish")
            {
                return "{" + name + ".value" + (node["format"]?.GetValue<string>() == "uri" ? ":uri" : "") + "}";
            }

            value = _secrets.GetOrAdd(name, _ => RandomNumberGenerator.GetString("abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789", 22));
        }
        else if (kind == "property")
        {
            var property = RpcPeer.RequiredString(node, "property");
            var key = name + "." + property;
            if (!path.Add(key))
            {
                throw new InvalidOperationException($"Native value cycle at '{key}'.");
            }

            try
            {
                value = await ResolveAsync(RpcPeer.RequiredObject(resource, "properties")[property]
                    ?? throw new ArgumentException($"Unknown property '{key}'."), mode, network, consumer, path, cancellationToken);
            }
            finally
            {
                path.Remove(key);
            }
        }
        else if (kind == "endpoint")
        {
            var endpointName = RpcPeer.RequiredString(node, "endpoint");
            var property = RpcPeer.RequiredString(node, "property");
            var endpoint = RpcPeer.RequiredObject(RpcPeer.RequiredObject(resource, "endpoints"), endpointName);
            if (mode == "publish")
            {
                return "{" + name + ".bindings." + endpointName + "." + property + "}";
            }

            value = property switch
            {
                "url" or "scheme" or "host" or "port" when RpcPeer.RequiredString(resource, "kind") == "custom" =>
                    _runtime.TryGetValue(name, out var customState) && customState["state"]?.GetValue<string>() == "Healthy" &&
                    customState["endpoints"]?[endpointName]?[property] is { } customValue
                        ? await ResolveAsync(customValue, mode, network, consumer, path, cancellationToken)
                        : throw new InvalidOperationException($"Custom endpoint '{name}.{endpointName}' is unavailable."),
                "scheme" => RpcPeer.RequiredString(endpoint, "scheme"),
                "url" => await ResolveEndpointUrlAsync(),
                "host" when network == "container" => name,
                "port" or "targetPort" when network == "container" => endpoint["targetPort"]!.GetValue<int>().ToString(CultureInfo.InvariantCulture),
                "host" or "port" => _runtime.TryGetValue(name, out var runtime) && runtime["endpoints"]?[endpointName]?[property] is { } allocated
                    ? await ResolveAsync(allocated, mode, network, consumer, path, cancellationToken)
                    : throw new InvalidOperationException($"Endpoint '{name}.{endpointName}' is not allocated."),
                _ => throw new ArgumentException("Unsupported endpoint property.")
            };

            async Task<string> ResolveEndpointUrlAsync()
            {
                var hostExpression = (JsonObject)node.DeepClone();
                hostExpression["property"] = "host";
                var portExpression = (JsonObject)node.DeepClone();
                portExpression["property"] = "port";
                var host = await ResolveAsync(hostExpression, mode, network, consumer, path, cancellationToken);
                var port = await ResolveAsync(portExpression, mode, network, consumer, path, cancellationToken);
                return new UriBuilder(RpcPeer.RequiredString(endpoint, "scheme"), host, int.Parse(port, CultureInfo.InvariantCulture)).Uri.AbsoluteUri;
            }
        }
        else
        {
            throw new ArgumentException($"Unsupported expression kind '{kind}'.");
        }

        return node["format"]?.GetValue<string>() == "uri" && mode == "run" ? Uri.EscapeDataString(value) : value;
    }

    private async Task EnsureNetworkAsync(CancellationToken cancellationToken)
    {
        await _networkGate.WaitAsync(cancellationToken);
        try
        {
            if (!_networkCreated)
            {
                await _dcp.CreateAsync("containernetworks", _network, new JsonObject { ["networkName"] = _network, ["persistent"] = false }, null, cancellationToken);
                _networkCreated = true;
            }
        }
        finally
        {
            _networkGate.Release();
        }
    }

    private Task<JsonNode?> CallbackAsync(string callback, string name, CancellationToken cancellationToken) =>
        peer.RequestAsync("invokeIntegration", new JsonObject { ["callback"] = callback, ["resource"] = Handle(name) }, cancellationToken);

    private JsonObject Get(JsonObject reference)
    {
        if (RpcPeer.RequiredString(reference, "owner") != owner || !_resources.TryGetValue(RpcPeer.RequiredString(reference, "name"), out var resource))
        {
            throw new InvalidOperationException("Native model owner or resource identity is invalid.");
        }

        return resource;
    }

    private JsonObject Handle(string name) => new() { ["owner"] = owner, ["name"] = name };
    private string PhysicalName(string name) => $"native-{owner[..10]}-{name}";

    public async ValueTask DisposeAsync()
    {
        await _dcp.DisposeAsync();
        _custom?.Dispose();
        foreach (var operation in _operations.Values)
        {
            operation.Dispose();
        }

        _definitionGate.Dispose();
        _networkGate.Dispose();
    }
}
