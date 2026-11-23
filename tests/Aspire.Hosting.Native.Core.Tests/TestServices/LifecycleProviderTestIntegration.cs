// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using Aspire.Hosting.Native.Client;

namespace Aspire.Hosting.Native.Core.Tests.TestServices;

/// <summary>Exercises the portable provider contract over actual sockets, independently of native runtime implementation types.</summary>
internal sealed class LifecycleProviderTestIntegration
{
    private const string Package = "example.lifecycle";
    private const string ResourceType = Package + "/Resource";
    private const string CoreType = "Aspire.Hosting.Native.Server/Aspire.Hosting.Native.Api.";
    public ConcurrentQueue<LifecycleProviderTestResource> Resources { get; } = new();
    public ConcurrentQueue<(LifecycleProviderTestResource Resource, string Phase)> Phases { get; } = new();
    public ConcurrentQueue<ResourceLifecycleScope> Scopes { get; } = new();
    public Func<string, ResourceLifecycleScope, CancellationToken, Task>? OnPhase { get; set; }
    public Func<LifecycleProviderTestResource, LifecycleProviderTestResource, CancellationToken, Task>? OnInvocation { get; set; }
    public NativeProviderContext? Context { get; private set; }

    public NativeProviderDefinition Definition
    {
        get
        {
            static JsonObject Type(string id, string category = "Handle") => new() { ["typeId"] = id, ["category"] = category };
            static JsonObject Parameter(string name, JsonObject type) => new() { ["name"] = name, ["type"] = type };
            var methods = new JsonArray
            {
                new JsonObject
                {
                    ["id"] = Package + "/add", ["method"] = "add",
                    ["parameters"] = new JsonArray(Parameter("composition", Type(CoreType + "Composition")),
                        Parameter("password", Type(CoreType + "DeclaredValue")), Parameter("image", Type("string", "Primitive"))),
                    ["returnType"] = Type(ResourceType)
                }
            };
            foreach (var phase in new[] { "initialize", "allocated", "ready", "retiring" })
            {
                methods.Add(new JsonObject
                {
                    ["id"] = Package + "/" + phase,
                    ["method"] = phase,
                    ["lifecyclePhase"] = phase,
                    ["targetTypeId"] = ResourceType,
                    ["targetParameterName"] = "context",
                    ["parameters"] = new JsonArray(Parameter("context", Type(ResourceType)), Parameter("scope", Type(CoreType + "ResourceLifecycleScope"))),
                    ["returnType"] = Type("void", "Primitive")
                });
            }
            methods.Add(new JsonObject
            {
                ["id"] = Package + "/invoke",
                ["method"] = "invoke",
                ["targetTypeId"] = ResourceType,
                ["targetParameterName"] = "context",
                ["parameters"] = new JsonArray(Parameter("context", Type(ResourceType)), Parameter("other", Type(ResourceType))),
                ["returnType"] = Type("void", "Primitive")
            });
            var manifest = new JsonObject { ["protocolVersion"] = 2, ["capabilities"] = methods };

            return new(JsonSerializer.SerializeToElement(manifest), DispatchAsync);
        }
    }

    private async Task<JsonElement> DispatchAsync(NativeProviderContext context, string capability, JsonElement arguments)
    {
        Context = context;
        if (capability == Package + "/add")
        {
            var composition = new Composition(context.Client, arguments.GetProperty("composition"));
            var declaration = await composition.AddContainerAsync("service", arguments.GetProperty("image").GetString()!);
            await declaration.WithEnvironmentAsync("TOKEN", new DeclaredValue(context.Client, arguments.GetProperty("password")));
            var resource = new LifecycleProviderTestResource(declaration);
            Resources.Enqueue(resource);

            return context.PublishResource(resource, ResourceType);
        }
        var target = context.ReadResource<LifecycleProviderTestResource>(arguments.GetProperty("context"));
        if (capability == Package + "/invoke")
        {
            var other = context.ReadResource<LifecycleProviderTestResource>(arguments.GetProperty("other"));
            if (OnInvocation is { } invocation)
            {
                await invocation(target, other, context.CancellationToken);
            }

            return JsonSerializer.SerializeToElement((string?)null);
        }
        var scope = new ResourceLifecycleScope(context.Client, arguments.GetProperty("scope"));
        var phase = await scope.ReadLifecyclePhaseAsync(context.CancellationToken);
        Phases.Enqueue((target, phase));
        Scopes.Enqueue(scope);
        if (OnPhase is { } handler)
        {
            await handler(phase, scope, context.CancellationToken);
        }

        return JsonSerializer.SerializeToElement((string?)null);
    }

    public static Task<JsonElement> AddAsync(Composition composition, DeclaredValue password, string image) =>
        composition.Client.InvokeAsync(Package + "/add", new()
        {
            ["composition"] = composition.Handle,
            ["password"] = password.Handle,
            ["image"] = JsonSerializer.SerializeToElement(image)
        }, CancellationToken.None);
}

internal sealed class LifecycleProviderTestResource(DeclaredResource declaration) : NativeIntegrationResource(declaration)
{
    private int _disposeCount;
    public int DisposeCount => Volatile.Read(ref _disposeCount);
    public TaskCompletionSource Disposed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public override ValueTask DisposeAsync()
    {
        Interlocked.Increment(ref _disposeCount);
        Disposed.TrySetResult();

        return ValueTask.CompletedTask;
    }
}
