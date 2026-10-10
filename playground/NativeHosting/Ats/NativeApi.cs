// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Aspire.Hosting;

[assembly: AspireExport(typeof(CancellationToken))]

namespace NativeHosting;

// These are primitive graph contracts, not replacements for the shipped Hosting
// interfaces. ATS scanning/code generation runs offline; runtime calls are static.
[AspireDto]
public sealed class ResourceOptions
{
    public string? Image { get; init; }
    public string? Executable { get; init; }
    public string? Command { get; init; }
    public string? Directory { get; init; }
    public bool Secret { get; init; }
    public bool RunOnly { get; init; }
    public Dictionary<string, EndpointOptions> Endpoints { get; init; } = [];
    public VolumeOptions[] Volumes { get; init; } = [];
    public BindMountOptions[] BindMounts { get; init; } = [];
}

[AspireDto]
public sealed class EndpointOptions
{
    public required string Scheme { get; init; }
    public int? TargetPort { get; init; }
    public string? Environment { get; init; }
}

[AspireDto]
public sealed class VolumeOptions
{
    public required string Name { get; init; }
    public required string Target { get; init; }
}

[AspireDto]
public sealed class BindMountOptions
{
    public required string Source { get; init; }
    public required string Target { get; init; }
}

[AspireDto]
public sealed class ControlRequest
{
    public string? Command { get; init; }
    public long Generation { get; init; }
}

[AspireDto]
public sealed class CustomUpdate
{
    public long Generation { get; init; }
    public long Revision { get; init; }
    public required string State { get; init; }
    public Dictionary<string, string> Endpoints { get; init; } = [];
    public string? Message { get; init; }
}

[AspireDto]
public sealed class ResourceState
{
    public required string State { get; init; }
    public string? ContainerId { get; init; }
    public int? Pid { get; init; }
    public string? ExecutionId { get; init; }
    public Dictionary<string, AllocatedEndpoint> Endpoints { get; init; } = [];
}

[AspireDto]
public sealed class AllocatedEndpoint
{
    public string? Host { get; init; }
    public int Port { get; init; }
    public string? Url { get; init; }
}

[AspireDto]
public sealed class LogChunk
{
    public long Offset { get; init; }
    public required string Data { get; init; }
}

[AspireDto]
public sealed class ExecutableLogs
{
    public required LogChunk Stdout { get; init; }
    public required LogChunk Stderr { get; init; }
    public required string ExecutionId { get; init; }
}

[AspireDto]
public sealed class CoreStats
{
    public bool DynamicCodeSupported { get; init; }
    public long WorkingSetBytes { get; init; }
    public int Resources { get; init; }
}

[AspireExport]
public sealed class NativeValue
{
    internal NativeValue(NativeBuilder builder, JsonNode expression)
    {
        Builder = builder;
        Expression = expression;
    }

    internal NativeBuilder Builder { get; }
    internal JsonNode Expression { get; }
}

[AspireExport]
public sealed class NativeResource
{
    internal NativeResource(NativeBuilder builder, JsonObject definition)
    {
        Builder = builder;
        Definition = definition;
    }

    internal NativeBuilder Builder { get; }
    internal JsonObject Definition { get; }
    internal string Name => RpcPeer.RequiredString(Definition, "name");
    internal JsonObject Identity => new() { ["owner"] = Builder.Owner, ["name"] = Name };

    private NativeResource Set(string collection, string name, object value)
    {
        Builder.EnsureMutable();
        if (Definition[collection] is not JsonObject values)
        {
            Definition[collection] = values = new JsonObject();
        }

        values[name] = Builder.Expression(value);
        return this;
    }

    /// <summary>Sets a literal or deferred environment value.</summary>
    [AspireExport]
    public NativeResource WithEnvironment(string name, [AspireUnion(typeof(string), typeof(NativeValue))] object value) => Set("environment", name, value);

    /// <summary>Sets a named literal or deferred connection property.</summary>
    [AspireExport]
    public NativeResource WithProperty(string name, [AspireUnion(typeof(string), typeof(NativeValue))] object value) => Set("properties", name, value);

    /// <summary>Stores or replaces a singleton payload after validating its registered schema.</summary>
    [AspireExport]
    public NativeResource WithAnnotation(string annotationId, string json)
    {
        Builder.EnsureMutable();
        var payload = Builder.Annotations.Validate(annotationId, json);
        if (Definition["annotations"] is not JsonObject annotations)
        {
            Definition["annotations"] = annotations = new JsonObject();
        }
        annotations[annotationId] = payload;
        return this;
    }

    /// <summary>Reads portable annotation data; missing payloads fail explicitly.</summary>
    [AspireExport]
    public string GetAnnotation(string annotationId)
    {
        Builder.Annotations.RequireDefined(annotationId);
        return Definition["annotations"]?[annotationId]?.ToJsonString()
            ?? throw new ArgumentException($"Resource '{Name}' has no annotation '{annotationId}'.");
    }

    /// <summary>Checks whether this resource has a payload for a registered annotation.</summary>
    [AspireExport]
    public bool HasAnnotation(string annotationId)
    {
        Builder.Annotations.RequireDefined(annotationId);
        return Definition["annotations"]?[annotationId] is not null;
    }

    /// <summary>Adds a literal or deferred process argument.</summary>
    [AspireExport]
    public NativeResource WithArgument([AspireUnion(typeof(string), typeof(NativeValue))] object value)
    {
        Builder.EnsureMutable();
        if (Definition["arguments"] is not JsonArray arguments)
        {
            Definition["arguments"] = arguments = [];
        }

        arguments.Add(Builder.Expression(value));
        return this;
    }

    /// <summary>Waits for another resource to become ready.</summary>
    [AspireExport]
    public NativeResource WaitFor(NativeResource dependency)
    {
        Builder.EnsureMutable();
        Builder.EnsureSameGraph(dependency.Builder);
        if (Definition["dependencies"] is not JsonArray dependencies)
        {
            Definition["dependencies"] = dependencies = [];
        }

        dependencies.Add((JsonNode)dependency.Identity);
        return this;
    }

    /// <summary>Records a parent relationship without implicit readiness ordering.</summary>
    [AspireExport]
    public NativeResource WithParent(NativeResource parent)
    {
        Builder.EnsureMutable();
        Builder.EnsureSameGraph(parent.Builder);
        Definition["parent"] = parent.Identity;
        return this;
    }

    /// <summary>Returns a deferred endpoint property.</summary>
    [AspireExport]
    public NativeValue GetEndpoint(string endpoint, string property) => new(Builder,
        new JsonObject { ["kind"] = "endpoint", ["resource"] = Identity, ["endpoint"] = endpoint, ["property"] = property });

    /// <summary>Returns a deferred named connection property.</summary>
    [AspireExport]
    public NativeValue GetProperty(string name) => new(Builder,
        new JsonObject { ["kind"] = "property", ["resource"] = Identity, ["property"] = name });

    /// <summary>Returns a parameter value, optionally URI-escaped at resolution.</summary>
    [AspireExport]
    public NativeValue GetParameter(bool uriEscape)
    {
        var expression = new JsonObject { ["kind"] = "parameter", ["resource"] = Identity };
        if (uriEscape)
        {
            expression["format"] = "uri";
        }

        return new(Builder, expression);
    }

    /// <summary>Registers an observation-only readiness callback.</summary>
    [AspireExport]
    public NativeResource WithHealth(Func<NativeResource, CancellationToken, Task<bool>> callback) => Hook("health", callback);

    /// <summary>Registers one-time initialization after dependencies are ready.</summary>
    [AspireExport]
    public NativeResource WithInitialize(Func<NativeResource, CancellationToken, Task<bool>> callback) => Hook("initialize", callback);

    /// <summary>Registers setup before launching a compute resource.</summary>
    [AspireExport]
    public NativeResource WithBeforeStart(Func<NativeResource, CancellationToken, Task<bool>> callback) => Hook("beforeStart", callback);

    private NativeResource Hook(string field, Func<NativeResource, CancellationToken, Task<bool>> callback)
    {
        Builder.EnsureMutable();
        Definition[field] = Builder.RegisterCallback(this, (request, token) => callback(this, token), field);
        return this;
    }

    /// <summary>Registers a controller for a run-only custom resource.</summary>
    [AspireExport]
    public NativeResource WithControl(Func<NativeResource, ControlRequest, CancellationToken, Task<bool>> callback)
    {
        Builder.EnsureMutable();
        Definition["controllerOwner"] = Builder.ControllerOwner;
        Definition["control"] = Builder.RegisterCallback(this, (request, token) => callback(this, request, token), "control");
        return this;
    }

    /// <summary>Resolves a connection property in an explicit execution context.</summary>
    [AspireExport]
    public async Task<string> Resolve(string property, string mode, string network, CancellationToken cancellationToken)
    {
        var value = await Builder.InvokeAsync("resolve", new JsonObject
        {
            ["resource"] = Identity, ["property"] = property, ["mode"] = mode, ["network"] = network
        }, cancellationToken);
        return value!.GetValue<string>();
    }

    /// <summary>Observes the native resource state.</summary>
    [AspireExport]
    public async Task<ResourceState> Status(CancellationToken cancellationToken) =>
        (await Builder.InvokeAsync("status", new JsonObject { ["resource"] = Identity }, cancellationToken))!
            .Deserialize(NativeJsonContext.Default.ResourceState)!;

    /// <summary>Observes the DCP executable incarnation and process state.</summary>
    [AspireExport]
    public async Task<ResourceState> ComputeStatus(CancellationToken cancellationToken) =>
        (await Builder.InvokeAsync("computeStatus", new JsonObject { ["resource"] = Identity }, cancellationToken))!
            .Deserialize(NativeJsonContext.Default.ResourceState)!;

    /// <summary>Reads complete executable log lines from DCP.</summary>
    [AspireExport]
    public async Task<ExecutableLogs> ReadLogs(long stdoutOffset, long stderrOffset, CancellationToken cancellationToken) =>
        (await Builder.InvokeAsync("readLogs", new JsonObject
        {
            ["resource"] = Identity, ["stdoutOffset"] = stdoutOffset, ["stderrOffset"] = stderrOffset
        }, cancellationToken))!.Deserialize(NativeJsonContext.Default.ExecutableLogs)!;

    /// <summary>Stops or restarts a compute or custom resource.</summary>
    [AspireExport]
    public async Task<bool> Command(string command, CancellationToken cancellationToken)
    {
        var kind = RpcPeer.RequiredString(Definition, "kind");
        await Builder.InvokeAsync(kind == "custom" ? "command" : command,
            new JsonObject { ["resource"] = Identity, ["command"] = command }, cancellationToken);
        return true;
    }

    /// <summary>Publishes a fenced custom-resource state and endpoint update.</summary>
    [AspireExport]
    public async Task<bool> UpdateCustom(CustomUpdate update, CancellationToken cancellationToken)
    {
        var args = JsonSerializer.SerializeToNode(update, NativeJsonContext.Default.CustomUpdate)!.AsObject();
        args["resource"] = Identity;
        args["controllerOwner"] = Builder.ControllerOwner;
        await Builder.InvokeAsync("updateCustom", args, cancellationToken);
        return true;
    }
}

[AspireExport]
public sealed class NativeBuilder : IAsyncDisposable, IRequestPeer
{
    private volatile bool _ready;
    internal bool Ready => _ready;

    internal async Task<JsonArray> ObserveResourcesAsync(CancellationToken cancellationToken)
    {
        var result = new JsonArray();
        if (!_ready)
        {
            return result;
        }

        foreach (var resource in _resources.Values)
        {
            var state = (await InvokeAsync("status", new JsonObject { ["resource"] = resource.Identity }, cancellationToken))!.AsObject();
            // Parameters and synthetic children have no allocation dictionary.
            // Project only observable state and URLs, never definition values.
            var urls = new JsonArray();
            if (state["endpoints"] is JsonObject endpoints)
            {
                foreach (var endpoint in endpoints)
                {
                    if (endpoint.Value?["url"] is { } url)
                    {
                        urls.Add(url.DeepClone());
                    }
                    else if (endpoint.Value?["host"] is { } host && endpoint.Value["port"] is { } port)
                    {
                        var scheme = resource.Definition["endpoints"]![endpoint.Key]!["scheme"]!.GetValue<string>();
                        urls.Add((JsonNode)JsonValue.Create(new UriBuilder(scheme, host.GetValue<string>(), port.GetValue<int>()).Uri.AbsoluteUri)!);
                    }
                }
            }

            result.Add((JsonNode)new JsonObject
            {
                ["name"] = resource.Name,
                ["kind"] = resource.Definition["kind"]!.DeepClone(),
                ["state"] = RpcPeer.RequiredString(state, "state"),
                ["containerId"] = state["containerId"]?.DeepClone(),
                ["pid"] = state["pid"]?.DeepClone(),
                ["urls"] = urls
            });
        }

        return result;
    }

    private readonly NativeModel _model;
    private readonly Dictionary<string, NativeResource> _resources = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Func<ControlRequest, CancellationToken, Task<bool>>> _callbacks = new(StringComparer.Ordinal);
    private bool _sealed;
    private bool _built;
    private bool _disposed;
    private readonly string _dcp;
    internal NativeAnnotations Annotations { get; } = new();

    internal NativeBuilder(string dcp)
    {
        _dcp = dcp;
        _model = new(Owner, this);
    }

    internal string Owner { get; } = Guid.NewGuid().ToString("N");
    internal string ControllerOwner { get; } = Guid.NewGuid().ToString("N");
    internal string ProjectDirectory { get; set; } = Environment.CurrentDirectory;

    /// <summary>Registers a graph-scoped singleton schema; identical repeated declarations are permitted.</summary>
    [AspireExport]
    public bool DefineAnnotation(string annotationId, AnnotationFieldOptions[] fields)
    {
        EnsureMutable();
        return Annotations.Define(annotationId, fields);
    }

    internal void Build()
    {
        EnsureMutable();
        _built = true;
    }

    internal void EnsureMutable()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_sealed || _built)
        {
            throw new InvalidOperationException("The ATS graph is sealed.");
        }
    }

    internal void EnsureSameGraph(NativeBuilder builder)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!ReferenceEquals(this, builder))
        {
            throw new ArgumentException("Handles belong to different graph generations.");
        }
    }

    internal JsonNode Expression(object value) => value switch
    {
        string text => JsonValue.Create(text)!,
        NativeValue expression when ReferenceEquals(expression.Builder, this) => expression.Expression.DeepClone(),
        _ => throw new ArgumentException("Expected a literal or a value from this graph generation.")
    };

    /// <summary>Adds an inert primitive resource to the graph.</summary>
    [AspireExport]
    public NativeResource AddResource(string name, string kind, ResourceOptions options)
    {
        EnsureMutable();
        var definition = JsonSerializer.SerializeToNode(options, NativeJsonContext.Default.ResourceOptions)!.AsObject();
        definition["name"] = name;
        definition["kind"] = kind;
        var resource = new NativeResource(this, definition);
        if (!_resources.TryAdd(name, resource))
        {
            throw new ArgumentException($"Duplicate resource '{name}'.");
        }

        return resource;
    }

    /// <summary>Creates a literal component of a structured value.</summary>
    [AspireExport]
    public NativeValue Literal(string value) => new(this, JsonValue.Create(value)!);

    /// <summary>Combines structured values without resolving them.</summary>
    [AspireExport]
    public NativeValue Concat(NativeValue[] values) => new(this,
        new JsonObject { ["kind"] = "concat", ["items"] = new JsonArray(values.Select(Expression).ToArray()) });

    /// <summary>Seals, validates, and runs the graph using real DCP.</summary>
    [AspireExport]
    public async Task<bool> Run(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_sealed)
        {
            throw new InvalidOperationException("The ATS graph is already running.");
        }
        _sealed = true;
        // Definitions are committed only after guest construction finishes. This
        // keeps callback registration and deferred references free of side effects.
        foreach (var resource in _resources.Values)
        {
            await _model.InvokeAsync("define", new JsonObject { ["definition"] = resource.Definition.DeepClone() }, cancellationToken);
        }

        await _model.InvokeAsync("configure", new JsonObject { ["dcp"] = _dcp }, cancellationToken);
        await _model.InvokeAsync("startAll", new JsonObject(), cancellationToken);
        _ready = true;
        return true;
    }

    /// <summary>Returns the symbolic run-independent graph, excluding run-only resources.</summary>
    [AspireExport]
    public string Publish()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return new JsonObject
        {
            ["format"] = "native-model.v0",
            ["annotationSchemas"] = Annotations.Describe(),
            ["resources"] = new JsonArray(_resources.Values.Where(resource => resource.Definition["runOnly"]?.GetValue<bool>() != true)
                .OrderBy(resource => resource.Name, StringComparer.Ordinal).Select(resource => (JsonNode)resource.Definition.DeepClone()).ToArray())
        }.ToJsonString();
    }

    /// <summary>Reports native runtime evidence without loading integrations.</summary>
    [AspireExport]
    public async Task<CoreStats> Stats(CancellationToken cancellationToken) =>
        (await InvokeAsync("stats", new JsonObject(), cancellationToken))!.Deserialize(NativeJsonContext.Default.CoreStats)!;

    internal string RegisterCallback(NativeResource resource, Func<ControlRequest, CancellationToken, Task<bool>> callback, string field)
    {
        var id = $"{Owner}:{resource.Name}:{field}";
        _callbacks.Add(id, callback);
        return id;
    }

    internal Task<JsonNode?> InvokeAsync(string method, JsonObject args, CancellationToken token)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        token.ThrowIfCancellationRequested();
        return _model.InvokeAsync(method, args, token);
    }

    public async Task<JsonNode?> RequestAsync(string method, JsonObject args, CancellationToken cancellationToken)
    {
        if (method != "invokeIntegration" || !_callbacks.TryGetValue(RpcPeer.RequiredString(args, "callback"), out var callback))
        {
            throw new InvalidOperationException("Unknown ATS lifecycle callback.");
        }

        var request = new ControlRequest
        {
            Command = args["command"]?.GetValue<string>(), Generation = args["generation"]?.GetValue<long>() ?? 0
        };
        var ready = await callback(request, cancellationToken);
        return new JsonObject { ["ready"] = ready };
    }

    public async ValueTask DisposeAsync()
    {
        if (!_disposed)
        {
            _disposed = true;
            await _model.DisposeAsync();
            _callbacks.Clear();
            _resources.Clear();
        }
    }
}

public static class NativeApi
{
    /// <summary>Creates a minimal native graph builder.</summary>
    [AspireExport]
    public static NativeBuilder CreateNativeBuilder() => new(Environment.GetEnvironmentVariable("NATIVE_HOSTING_DCP")
        ?? throw new InvalidOperationException("NATIVE_HOSTING_DCP must identify the DCP executable."));
}

[JsonSerializable(typeof(ResourceOptions))]
[JsonSerializable(typeof(CustomUpdate))]
[JsonSerializable(typeof(ControlRequest))]
[JsonSerializable(typeof(ResourceState))]
[JsonSerializable(typeof(ExecutableLogs))]
[JsonSerializable(typeof(CoreStats))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
internal sealed partial class NativeJsonContext : JsonSerializerContext;
