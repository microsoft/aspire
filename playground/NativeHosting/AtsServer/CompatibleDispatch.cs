// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json.Nodes;

namespace NativeHosting;

// A wire adapter over native primitives, not CLR implementations of Hosting.
// Production ATS metadata supplies identifiers, argument names and target types.
internal static class CompatibleDispatch
{
    private const string BuilderType = "NativeHosting.Ats/NativeHosting.NativeBuilder";
    private const string ResourceType = "NativeHosting.Ats/NativeHosting.NativeResource";
    private const string ValueType = "NativeHosting.Ats/NativeHosting.NativeValue";
    private static readonly JsonObject s_contract = LoadContract();

    public static bool Contains(string capability) => s_contract.ContainsKey(capability);
    public static bool IsExternal(string capability) => s_contract[capability]?["route"]?.GetValue<string>() == "external";

    private static JsonObject LoadContract()
    {
        using var stream = typeof(CompatibleDispatch).Assembly.GetManifestResourceStream("CompatibleContract.json")
            ?? throw new InvalidOperationException("Generate the compatible ATS contract before publication.");
        return JsonNode.Parse(stream)!.AsObject();
    }

    public static async Task<JsonNode?> InvokeAsync(AtsSession session, string capability, JsonObject args, CancellationToken token,
        Func<string, JsonObject, Task<JsonNode?>> external)
    {
        var contract = s_contract[capability]!.AsObject();
        var route = RpcPeer.RequiredString(contract, "route");
        var targetName = RpcPeer.RequiredString(contract, "target");
        var target = args[targetName];
        if (route != "create")
        {
            var type = RpcPeer.RequiredString(target!.AsObject(), "$type");
            if (!contract["targets"]!.AsArray().Any(node => node?.GetValue<string>() == type))
            {
                throw new AtsFault("TYPE_MISMATCH", "The resource does not support this capability.");
            }
        }

        foreach (var parameter in contract["parameters"]!.AsArray())
        {
            var name = parameter!["name"]!.GetValue<string>();
            if (args[name] is null && !parameter["optional"]!.GetValue<bool>() && !parameter["nullable"]!.GetValue<bool>())
            {
                throw new ArgumentException($"Missing required argument '{name}'.");
            }
        }

        var returnType = RpcPeer.RequiredString(contract, "returnType");
        switch (route)
        {
            case "create":
            {
                if (args["argsOrOptions"] is JsonObject options)
                {
                    foreach (var option in options)
                    {
                        if (option.Key is not ("args" or "projectDirectory" or "appHostFilePath"))
                        {
                            throw new NotSupportedException($"Builder option '{option.Key}' is not implemented.");
                        }
                    }
                }

                var builder = NativeApi.CreateNativeBuilder();
                builder.ProjectDirectory = args["argsOrOptions"]?["projectDirectory"]?.GetValue<string>()
                    ?? Environment.GetEnvironmentVariable("ASPIRE_PROJECT_DIRECTORY") ?? Environment.CurrentDirectory;
                if (!Path.IsPathFullyQualified(builder.ProjectDirectory) || !Directory.Exists(builder.ProjectDirectory))
                {
                    await builder.DisposeAsync();
                    throw new ArgumentException("projectDirectory must be an existing absolute path.");
                }
                session.Marshal(builder, BuilderType);
                return session.Marshal(builder, returnType);
            }
            case "build":
            {
                var builder = session.Get<NativeBuilder>(target);
                builder.Build();
                return session.Marshal(new NativeApplication(builder), returnType);
            }
            case "run":
            {
                var application = session.Get<NativeApplication>(target);
                await application.Builder.Run(token);
                // Match Hosting RunAsync's lifetime: the guest remains in its
                // pending run call, rather than adding a timer to AppHost source.
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return null;
            }
            case "external":
            {
                var converted = new JsonObject();
                foreach (var argument in args)
                {
                    converted[argument.Key] = ConvertHandle(session, argument.Value);
                }
                if (contract["targetType"]!.GetValue<string>().EndsWith(".IDistributedApplicationBuilder", StringComparison.Ordinal))
                {
                    converted["_projectDirectory"] = session.Get<NativeBuilder>(target).ProjectDirectory;
                }

                var result = await external(capability, converted);
                var resource = session.Get<NativeResource>(result);
                return session.Marshal(resource, returnType);
            }
            case "wait":
            {
                RequireDefault(args, "waitBehavior");
                session.Get<NativeResource>(target).WaitFor(session.Get<NativeResource>(args["dependency"]));
                return target!.DeepClone();
            }
            case "environment":
            {
                var value = args["value"] is JsonObject ? session.Get<NativeValue>(args["value"]) : (object)RpcPeer.RequiredString(args, "value");
                session.Get<NativeResource>(target).WithEnvironment(RpcPeer.RequiredString(args, "name"), value);
                return target!.DeepClone();
            }
            case "reference":
            {
                RequireDefault(args, "optional", false);
                RequireDefault(args, "name");
                var resource = session.Get<NativeResource>(target);
                var source = session.Get<NativeResource>(args["source"]);
                var name = args["connectionName"]?.GetValue<string>() ?? source.Name;
                resource.WithEnvironment("ConnectionStrings__" + name, source.GetProperty("connectionString"));
                return target!.DeepClone();
            }
            case "endpoint":
            {
                RequireDefault(args, "port");
                RequireDefault(args, "isProxied");
                var resource = session.Get<NativeResource>(target);
                resource.Builder.EnsureMutable();
                var endpoints = resource.Definition["endpoints"] as JsonObject ?? new JsonObject();
                resource.Definition["endpoints"] = endpoints;
                var name = args["name"]?.GetValue<string>() ?? "http";
                if (endpoints.ContainsKey(name))
                {
                    throw new ArgumentException($"Endpoint '{name}' already exists.");
                }
                endpoints[name] = new JsonObject
                {
                    ["scheme"] = "http", ["targetPort"] = args["targetPort"]?.DeepClone(),
                    ["environment"] = args["env"]?.DeepClone()
                };
                return target!.DeepClone();
            }
            case "property":
            {
                var resource = session.Get<NativeResource>(target);
                var key = RpcPeer.RequiredString(args, "key");
                var properties = resource.Definition["properties"]!.AsObject();
                if (!properties.ContainsKey(key))
                {
                    throw new ArgumentException($"Unknown connection property '{key}'.");
                }
                var value = resource.GetProperty(key);
                session.Marshal(value, ValueType);
                return session.Marshal(value, returnType);
            }
            default:
                throw new NotSupportedException($"Native route '{route}' is not implemented.");
        }
    }

    private static JsonNode? ConvertHandle(AtsSession session, JsonNode? value)
    {
        if (value is not JsonObject reference || !reference.ContainsKey("$handle"))
        {
            return value?.DeepClone();
        }
        var type = RpcPeer.RequiredString(reference, "$type");
        if (type.EndsWith(".IDistributedApplicationBuilder", StringComparison.Ordinal))
        {
            return session.Marshal(session.Get<NativeBuilder>(reference), BuilderType);
        }
        return session.Marshal(session.Get<NativeResource>(reference), ResourceType);
    }

    private static void RequireDefault(JsonObject args, string name, bool? defaultValue = null)
    {
        if (args[name] is { } value && (defaultValue is null || value.GetValue<bool>() != defaultValue))
        {
            throw new NotSupportedException($"Argument '{name}' is not implemented.");
        }
    }

    private sealed class NativeApplication(NativeBuilder builder)
    {
        public NativeBuilder Builder { get; } = builder;
    }
}
