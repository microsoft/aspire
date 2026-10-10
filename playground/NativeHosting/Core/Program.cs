// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using NativeHosting;

var resources = new ConcurrentDictionary<string, JsonObject>(StringComparer.OrdinalIgnoreCase);
var owner = Guid.NewGuid().ToString("N");
RpcPeer? peer = null;
peer = new RpcPeer(InvokeAsync);
using (peer)
{
    await peer.RunAsync();
}

async Task<JsonNode?> InvokeAsync(string method, JsonObject args, CancellationToken cancellationToken)
{
    if (method == "stats")
    {
        using var process = Process.GetCurrentProcess();
        return new JsonObject
        {
            ["dynamicCodeSupported"] = RuntimeFeature.IsDynamicCodeSupported,
            ["workingSetBytes"] = process.WorkingSet64,
            ["resources"] = resources.Count
        };
    }

    if (method == "addExecutable")
    {
        var name = RpcPeer.RequiredString(args, "name");
        var directory = RpcPeer.RequiredString(args, "directory");
        if (!Path.IsPathFullyQualified(directory) || !Directory.Exists(directory))
        {
            throw new ArgumentException("Executable directory must be an existing absolute path.");
        }

        var executable = RpcPeer.RequiredString(args, "executable");
        if (args["arguments"] is not JsonArray arguments || arguments.Any(arg => arg is not JsonValue value || !value.TryGetValue<string>(out _)))
        {
            throw new ArgumentException("Executable arguments must be an array of strings.");
        }

        var resource = new JsonObject
        {
            ["owner"] = owner, ["name"] = name, ["directory"] = directory,
            ["executable"] = executable, ["arguments"] = arguments.DeepClone(),
            ["environment"] = new JsonObject { ["HOST"] = "0.0.0.0" },
            ["publish"] = RpcPeer.RequiredObject(args, "publish").DeepClone()
        };
        if (!resources.TryAdd(name, resource))
        {
            throw new InvalidOperationException($"Resource '{name}' already exists.");
        }

        return new JsonObject { ["owner"] = owner, ["name"] = name };
    }

    var handle = RpcPeer.RequiredObject(args, "resource");
    if (RpcPeer.RequiredString(handle, "owner") != owner || !resources.TryGetValue(RpcPeer.RequiredString(handle, "name"), out var target))
    {
        throw new InvalidOperationException("Native resource owner or identity is invalid for this session.");
    }

    if (method == "withReference")
    {
        var reference = RpcPeer.RequiredObject(args, "reference");
        var description = await peer!.RequestAsync("describeOwner", new JsonObject { ["reference"] = reference.DeepClone() }, cancellationToken);
        if (description?["properties"] is not JsonArray properties || !properties.Any(property => property?.GetValue<string>() == "Uri"))
        {
            throw new InvalidOperationException("Reference owner must provide a Uri property.");
        }

        lock (target)
        {
            // Keep the owner identity and requested property, never a resolved URI.
            // Re-resolve for each execution context; do not cache ports or credentials.
            target["environment"]!["NUXT_REDIS_URI"] = new JsonObject
            {
                ["reference"] = reference.DeepClone(), ["property"] = "Uri"
            };
        }

        return handle.DeepClone();
    }

    if (method == "applyEnvironment")
    {
        var environment = RpcPeer.RequiredObject(args, "environment");
        foreach (var entry in environment)
        {
            if (entry.Value is JsonObject expression)
            {
                _ = RpcPeer.RequiredString(RpcPeer.RequiredObject(expression, "reference"), "owner");
                _ = RpcPeer.RequiredString(RpcPeer.RequiredObject(expression, "reference"), "name");
                _ = RpcPeer.RequiredString(expression, "property");
            }
            else if (entry.Value is not JsonValue value || !value.TryGetValue<string>(out _))
            {
                throw new ArgumentException($"Invalid environment value for '{entry.Key}'.");
            }
        }

        lock (target)
        {
            foreach (var entry in environment)
            {
                target["environment"]![entry.Key] = entry.Value!.DeepClone();
            }
        }

        return handle.DeepClone();
    }

    JsonObject snapshot;
    lock (target)
    {
        snapshot = (JsonObject)target.DeepClone();
    }

    if (method == "describe")
    {
        return snapshot;
    }

    if (method == "resolveEnvironment")
    {
        var mode = RpcPeer.RequiredString(args, "mode");
        if (mode is not ("run" or "publish"))
        {
            throw new ArgumentException("Mode must be 'run' or 'publish'.");
        }

        var environment = RpcPeer.RequiredObject(snapshot, "environment");
        foreach (var entry in environment.ToArray())
        {
            if (entry.Value is JsonObject expression)
            {
                environment[entry.Key] = await peer!.RequestAsync("resolveOwner", new JsonObject
                {
                    ["reference"] = expression["reference"]!.DeepClone(),
                    ["property"] = expression["property"]!.DeepClone(),
                    ["consumer"] = handle.DeepClone(), ["mode"] = mode
                }, cancellationToken);
            }
        }

        return snapshot;
    }

    throw new InvalidOperationException($"Unknown native operation '{method}'.");
}
