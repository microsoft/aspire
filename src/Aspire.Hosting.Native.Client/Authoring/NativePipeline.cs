// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Runtime.ExceptionServices;
using System.Text.Json.Nodes;
using Aspire.Hosting.Native.Client;

namespace Aspire.Hosting.Native.Authoring;

/// <summary>Records callbacks; native code validates, selects and claims every executable step.</summary>
public sealed class NativePipeline
{
    private readonly List<NativePipelineStep> _steps = [];
    private readonly List<Action<IReadOnlyList<NativePipelineStep>>> _configuration = [];
    private bool _sealed;

    /// <summary>Gets whether user or integration code contributed startup or deployment callbacks.</summary>
    public bool HasCustomizations { get; private set; }

    /// <summary>Creates standard aggregation steps without target-specific deployment actions.</summary>
    public NativePipeline()
    {
        AddStep("process-parameters", _ => Task.CompletedTask);
        foreach (var operation in new[] { "build", "publish", "deploy", "push", "destroy" })
        {
            AddStep(operation + "-prereq", _ => Task.CompletedTask).DependsOn.Add("process-parameters");
            AddStep(operation, _ =>
            {
                if (operation != "build" && !_steps.Any(step => step.RequiredBy.Contains(operation)))
                {
                    throw new InvalidOperationException($"Native pipeline '{operation}' requires an integration-provided action.");
                }

                return Task.CompletedTask;
            }).DependsOn.Add(operation + "-prereq");
        }
        AddStep("validate-compute-environments", _ => Task.CompletedTask);
        AddStep("before-start", _ => Task.CompletedTask).DependsOn.Add("validate-compute-environments");
        foreach (var name in new[] { "check-container-runtime", "diagnostics" })
        {
            AddStep(name, _ => throw new InvalidOperationException($"Native pipeline '{name}' requires an integration-provided action."));
        }
        HasCustomizations = false;
    }

    /// <summary>Records an action and returns its dependency/configuration metadata.</summary>
    /// <param name="name">Unique native step identity.</param>
    /// <param name="action">Callback executed only after the native scheduler claims it.</param>
    /// <returns>The step's mutable authoring metadata.</returns>
    /// <exception cref="InvalidOperationException">The pipeline has been sealed.</exception>
    public NativePipelineStep AddStep(string name, Func<NativePipelineStepContext, Task> action)
    {
        if (_sealed)
        {
            throw new InvalidOperationException("The native pipeline is sealed.");
        }
        var step = new NativePipelineStep(name, action);
        _steps.Add(step);
        HasCustomizations = true;

        return step;
    }

    /// <summary>Records a configuration callback evaluated before native graph validation.</summary>
    /// <param name="configure">Updates the final list's step metadata.</param>
    /// <exception cref="InvalidOperationException">The pipeline has been sealed.</exception>
    public void Configure(Action<IReadOnlyList<NativePipelineStep>> configure)
    {
        if (_sealed)
        {
            throw new InvalidOperationException("The native pipeline is sealed.");
        }
        _configuration.Add(configure);
        HasCustomizations = true;
    }

    /// <summary>Seals the native DAG and executes independent claimed callbacks concurrently.</summary>
    /// <param name="server">Generated native server projection.</param>
    /// <param name="context">Native desired model and deployment coordinates.</param>
    /// <param name="target">Selected target and transitive prerequisites.</param>
    /// <returns>A task completing after callbacks settle and native progress completes.</returns>
    /// <exception cref="InvalidOperationException">The pipeline has already been sealed.</exception>
    public async Task ExecuteAsync(NativeApplicationServer server, PublishingContext context, string target)
    {
        if (_sealed)
        {
            throw new InvalidOperationException("The native pipeline can execute only once.");
        }
        foreach (var configure in _configuration)
        {
            configure(_steps.AsReadOnly());
        }
        foreach (var step in _steps)
        {
            foreach (var operation in new[] { "build", "push", "deploy" })
            {
                if (step.Tags.Contains(operation switch { "build" => "build-compute", "push" => "push-container-image", _ => "deploy-compute" }) ||
                    operation == "deploy" && step.Tags.Contains("provision-infra"))
                {
                    step.RequiredBy.Add(operation);
                }
            }
            foreach (var operation in new[] { "build", "publish", "deploy", "push", "destroy" })
            {
                if (step.RequiredBy.Contains(operation))
                {
                    step.DependsOn.Add(operation + "-prereq");
                }
            }
        }
        _sealed = true;
        var graph = new JsonObject
        {
            ["mode"] = context.IsRunMode ? "run" : "publish",
            ["steps"] = new JsonArray(_steps.Select(step => (JsonNode)new JsonObject
            {
                ["name"] = step.Name, ["dependsOn"] = Strings(step.DependsOn), ["requiredBy"] = Strings(step.RequiredBy),
                ["tags"] = Strings(step.Tags), ["description"] = step.Description, ["resource"] = step.Resource
            }).ToArray())
        };
        await server.ConfigurePublishingAsync(graph.ToJsonString(), context.CancellationToken).ConfigureAwait(false);
        await server.BeginPublishingAsync(target, context.CancellationToken).ConfigureAwait(false);
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(context.CancellationToken);
        var actions = _steps.ToDictionary(step => step.Name, StringComparer.Ordinal);
        var running = new List<Task>();
        Exception? failure = null;
        try
        {
            while (true)
            {
                if (!lifetime.IsCancellationRequested)
                {
                    foreach (var name in await server.ClaimPublishingStepsAsync(context.CancellationToken).ConfigureAwait(false))
                    {
                        running.Add(ExecuteStepAsync(name));
                    }
                }
                if (running.Count == 0)
                {
                    break;
                }
                await Task.WhenAny(running).ConfigureAwait(false);
                // Observe reporting failures as well as callback failures before leaving the operation.
                foreach (var task in running.Where(task => task.IsCompleted).ToArray())
                {
                    await task.ConfigureAwait(false);
                    running.Remove(task);
                }
            }
            if (failure is not null)
            {
                ExceptionDispatchInfo.Capture(failure).Throw();
            }
            lifetime.Token.ThrowIfCancellationRequested();
        }
        catch (Exception exception)
        {
            failure ??= exception;
            throw;
        }
        finally
        {
            await lifetime.CancelAsync().ConfigureAwait(false);
            try
            {
                await Task.WhenAll(running).ConfigureAwait(false);
            }
            finally
            {
                await server.CompletePublishingAsync(failure is not null || lifetime.IsCancellationRequested &&
                    context.CancellationToken.IsCancellationRequested, CancellationToken.None).ConfigureAwait(false);
            }
        }

        async Task ExecuteStepAsync(string name)
        {
            try
            {
                lifetime.Token.ThrowIfCancellationRequested();
                await actions[name].Action(new(server, name, context with { CancellationToken = lifetime.Token })).ConfigureAwait(false);
                lifetime.Token.ThrowIfCancellationRequested();
                await server.ReportPublishingActivityAsync(name, "Completed.", true, false, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                Interlocked.CompareExchange(ref failure, exception, null);
                await lifetime.CancelAsync().ConfigureAwait(false);
                // Exception payloads can contain secrets. Native progress receives only a classified failure.
                await server.ReportPublishingActivityAsync(name, "Pipeline step failed or canceled.", true, true,
                    CancellationToken.None).ConfigureAwait(false);
            }
        }
    }

    private static JsonArray Strings(IEnumerable<string> values) =>
        new(values.Select(value => (JsonNode?)JsonValue.Create(value)).ToArray());
}

/// <summary>Contains action metadata submitted to the authoritative native graph.</summary>
/// <param name="name">Unique step identity.</param>
/// <param name="action">Guest-owned action callback.</param>
public sealed class NativePipelineStep(string name, Func<NativePipelineStepContext, Task> action)
{
    /// <summary>Gets the step identity.</summary>
    public string Name { get; } = name;
    /// <summary>Gets prerequisite step identities.</summary>
    public List<string> DependsOn { get; } = [];
    /// <summary>Gets targets which require this step.</summary>
    public List<string> RequiredBy { get; } = [];
    /// <summary>Gets descriptive selection tags.</summary>
    public List<string> Tags { get; } = [];
    /// <summary>Gets or sets the native resource attributed to this step.</summary>
    public string? Resource { get; set; }
    /// <summary>Gets or sets human-readable inspection metadata.</summary>
    public string? Description { get; set; }
    internal Func<NativePipelineStepContext, Task> Action { get; } = action;
}

/// <summary>Provides native-model access and bounded reporting to a claimed action.</summary>
/// <param name="server">Native reporting projection.</param>
/// <param name="step">Claimed step identity.</param>
/// <param name="publishingContext">Desired model and operation cancellation.</param>
public sealed class NativePipelineStepContext(
    NativeApplicationServer server, string step, PublishingContext publishingContext)
{
    /// <summary>Gets the native desired model and deployment coordinates.</summary>
    public PublishingContext PublishingContext { get; } = publishingContext;
    /// <summary>Gets cancellation shared by the operation's claimed actions.</summary>
    public CancellationToken CancellationToken => PublishingContext.CancellationToken;

    /// <summary>Adds a plain-text native deployment summary entry.</summary>
    /// <param name="key">Summary entry identity.</param>
    /// <param name="value">Non-sensitive display text.</param>
    /// <returns>The native reporting operation.</returns>
    public Task AddSummaryAsync(string key, string value) =>
        server.ReportPublishingProgressAsync(step, "summary", key, value, false, false, false, CancellationToken);

    /// <summary>Reports a bounded informational message through native progress.</summary>
    /// <param name="message">Non-sensitive display text.</param>
    /// <returns>The native reporting operation.</returns>
    public Task LogInformationAsync(string message) =>
        server.ReportPublishingProgressAsync(step, "log", step, message, false, false, false, CancellationToken);
}
