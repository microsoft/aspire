// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;
using System.Threading.Channels;

namespace Aspire.Hosting.Native.Api;

internal sealed partial class NativeApplicationServer
{
    internal NativePublishingState Publishing { get; } = new();

    /// <summary>Registers and seals a bounded declarative pipeline graph.</summary>
    [AspireExport]
    public void ConfigurePublishing(string graphJson) => Publishing.Configure(graphJson);

    /// <summary>Selects a target and its transitive prerequisites for execution.</summary>
    [AspireExport]
    public void BeginPublishing(string target) => Publishing.Begin(target);

    /// <summary>Atomically claims ready steps, each at most once.</summary>
    [AspireExport]
    public string[] ClaimPublishingSteps() => Publishing.Claim();

    /// <summary>Reports the terminal result of a claimed step, or updates its status.</summary>
    [AspireExport]
    public void ReportPublishingActivity(string step, string status, bool complete, bool failed) =>
        Publishing.Report(step, status, complete, failed);

    /// <summary>Reports bounded task, log, or summary data for a running step.</summary>
    [AspireExport]
    public void ReportPublishingProgress(string step, string type, string id, string status, bool complete, bool failed, bool markdown) =>
        Publishing.Progress(step, type, id, status, complete, failed, markdown);

    /// <summary>Completes publishing after all claimed steps have settled.</summary>
    [AspireExport]
    public void CompletePublishing(bool failed) => Publishing.Complete(failed);

    /// <summary>Waits until the CLI acknowledges its stop response.</summary>
    [AspireExport]
    public Task WaitForPublishingStop() => Publishing.WaitForStop;
}

internal sealed record NativePipelineDescription(string Name, string[] DependsOn, string? Description, string[] Tags, string? ResourceName);
internal sealed record NativePipelineSummaryItem(string Key, string Value, bool EnableMarkdown);
internal sealed record NativePublishingActivity(string Type, NativePublishingActivityData Data);
internal sealed record NativePublishingActivityData(string Id, string StatusText, string CompletionState)
{
    public bool IsComplete => CompletionState != "InProgress";
    public bool IsError => CompletionState == "CompletedWithError";
    public bool IsWarning => CompletionState == "CompletedWithWarning";
    public string? StepId { get; init; }
    public string? LogLevel { get; init; }
    public DateTimeOffset? Timestamp { get; init; }
    public bool EnableMarkdown { get; init; }
    public NativePipelineSummaryItem[]? PipelineSummary { get; init; }
}

/// <summary>Owns graph validation, target selection, scheduling, and CLI progress independently of the guest.</summary>
internal sealed class NativePublishingState
{
    private readonly object _gate = new();
    private readonly TaskCompletionSource _configured = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _stop = new(TaskCreationOptions.RunContinuationsAsynchronously);
    // At most 1024 step starts, 1024 step completions, 4096 user progress events, and
    // one publish-complete fit even if inspection has not attached a progress reader.
    // Reserve terminal capacity so overflowing user logs cannot strand claimed steps.
    private Channel<NativePublishingActivity> _activities = Channel.CreateBounded<NativePublishingActivity>(8192);
    private readonly List<NativePipelineSummaryItem> _summary = [];
    private readonly Dictionary<string, bool> _tasks = new(StringComparer.Ordinal);
    private readonly HashSet<string> _warnings = new(StringComparer.Ordinal);
    private readonly HashSet<string> _taskFailures = new(StringComparer.Ordinal);
    private NativePipelineDescription[]? _steps;
    private Dictionary<string, int>? _states; // 0 pending, 1 claimed, 2 completed, 3 failed
    private bool _failed;
    private bool _complete;
    private int _progressCount;
    private bool _waitsForStop;

    internal bool IsConfigured { get { lock (_gate) { return _steps is not null; } } }
    internal bool WaitsForStop { get { lock (_gate) { return _waitsForStop; } } }
    internal Task WaitForStop => _stop.Task;
    internal void Stop() => _stop.TrySetResult();
    internal IAsyncEnumerable<NativePublishingActivity> ReadAsync(CancellationToken cancellationToken) =>
        _activities.Reader.ReadAllAsync(cancellationToken);

    internal void Configure(string graphJson)
    {
        ArgumentNullException.ThrowIfNull(graphJson);
        if (graphJson.Length > 1024 * 1024)
        {
            throw new ArgumentException("The pipeline graph exceeds 1 MiB.", nameof(graphJson));
        }
        // The ATS envelope is exactly:
        // {"mode":"publish","steps":[{"name":"compile","dependsOn":["restore"],"requiredBy":["publish"],
        // "description":null,"tags":["build-compute"],"resource":null}]}.
        // Mode defaults to publish; run-mode BeforeStart uses "run" and keeps ordinary CLI shutdown.
        // Unknown fields and non-string array members are rejected, not silently ignored.
        using var document = JsonDocument.Parse(graphJson, new() { MaxDepth = 16 });
        var root = document.RootElement;
        Fields(root, ["steps", "mode"]);
        var mode = root.TryGetProperty("mode", out var modeValue) ? Text(modeValue, 16) : "publish";
        if (mode is not ("run" or "publish"))
        {
            throw new ArgumentException("Pipeline mode must be 'run' or 'publish'.");
        }
        if (!root.TryGetProperty("steps", out var array) || array.ValueKind != JsonValueKind.Array || array.GetArrayLength() > 1024)
        {
            throw new ArgumentException("The pipeline requires a 'steps' array with at most 1024 steps.");
        }
        var dependencies = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var requiredBy = new Dictionary<string, string[]>(StringComparer.Ordinal);
        var steps = new List<NativePipelineDescription>();
        foreach (var item in array.EnumerateArray())
        {
            Fields(item, ["name", "dependsOn", "requiredBy", "description", "tags", "resource"]);
            if (!item.TryGetProperty("name", out var nameValue))
            {
                throw new ArgumentException("Every pipeline step requires a 'name'.");
            }
            var name = Text(nameValue, 256);
            if (!dependencies.TryAdd(name, new(Strings(item, "dependsOn"), StringComparer.Ordinal)))
            {
                throw new ArgumentException($"Duplicate pipeline step '{name}'.");
            }
            requiredBy.Add(name, Strings(item, "requiredBy"));
            steps.Add(new(name, [], OptionalText(item, "description", 4096), Strings(item, "tags"), OptionalText(item, "resource", 256)));
        }
        foreach (var (name, targets) in requiredBy)
        {
            foreach (var target in targets)
            {
                if (!dependencies.TryGetValue(target, out var prerequisites))
                {
                    throw new ArgumentException($"Pipeline step '{name}' is required by missing step '{target}'.");
                }
                prerequisites.Add(name);
            }
        }
        foreach (var (name, prerequisites) in dependencies)
        {
            foreach (var prerequisite in prerequisites)
            {
                if (!dependencies.ContainsKey(prerequisite))
                {
                    throw new ArgumentException($"Pipeline step '{name}' depends on missing step '{prerequisite}'.");
                }
            }
        }
        // Kahn ordering also avoids recursion on adversarial graphs. Preserve declaration order among peers.
        var ordered = new List<NativePipelineDescription>();
        var visited = new HashSet<string>(StringComparer.Ordinal);
        while (ordered.Count < steps.Count)
        {
            var ready = steps.Where(step => !visited.Contains(step.Name) && dependencies[step.Name].All(visited.Contains)).ToArray();
            if (ready.Length == 0)
            {
                throw new ArgumentException($"Pipeline dependency cycle involving: {string.Join(", ", steps.Where(step => !visited.Contains(step.Name)).Select(step => step.Name))}.");
            }
            foreach (var step in ready)
            {
                ordered.Add(step with { DependsOn = dependencies[step.Name].Order(StringComparer.Ordinal).ToArray() });
                visited.Add(step.Name);
            }
        }
        lock (_gate)
        {
            if (_steps is not null && !(_complete && !_waitsForStop && mode == "run"))
            {
                throw new InvalidOperationException("The publishing pipeline is already sealed.");
            }
            if (_complete)
            {
                if (_steps is null)
                {
                    throw new InvalidOperationException("The publishing pipeline has already completed.");
                }
                // A CLI run can own several initial application generations. Startup reporters
                // have no publishing-stream subscriber; each completed run graph can be replaced.
                _activities = Channel.CreateBounded<NativePublishingActivity>(8192);
                _states = null;
                _summary.Clear();
                _tasks.Clear();
                _warnings.Clear();
                _taskFailures.Clear();
                _failed = false;
                _complete = false;
                _progressCount = 0;
            }
            _steps = ordered.ToArray();
            _waitsForStop = mode == "publish";
            _configured.TrySetResult();
        }
    }

    internal async Task<NativePipelineDescription[]> GetStepsAsync(string? target, CancellationToken cancellationToken)
    {
        await _configured.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        lock (_gate)
        {
            return Select(target);
        }
    }

    private NativePipelineDescription[] Select(string? target)
    {
        if (_steps is null)
        {
            throw new InvalidOperationException("Configure the publishing pipeline first.");
        }
        if (target is null)
        {
            return _steps.ToArray();
        }
        var selected = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Stack<string>();
        pending.Push(target);
        while (pending.TryPop(out var name))
        {
            if (!selected.Add(name))
            {
                continue;
            }
            var step = _steps.FirstOrDefault(step => step.Name == name)
                ?? throw new ArgumentException($"Unknown pipeline target '{name}'. Registered steps: {string.Join(", ", _steps.Select(step => step.Name))}.");
            foreach (var dependency in step.DependsOn)
            {
                pending.Push(dependency);
            }
        }

        return _steps.Where(step => selected.Contains(step.Name)).ToArray();
    }

    internal void Begin(string target)
    {
        lock (_gate)
        {
            if (_states is not null || _complete)
            {
                throw new InvalidOperationException("Publishing execution has already begun or completed.");
            }
            _states = Select(target).ToDictionary(step => step.Name, _ => 0, StringComparer.Ordinal);
        }
    }

    internal string[] Claim()
    {
        lock (_gate)
        {
            if (_states is null)
            {
                throw new InvalidOperationException("Begin publishing before claiming steps.");
            }
            if (_failed || _complete || _stop.Task.IsCompleted)
            {
                return [];
            }
            var ready = _steps!.Where(step => _states.GetValueOrDefault(step.Name, -1) == 0 &&
                step.DependsOn.All(name => _states[name] == 2)).Select(step => step.Name).ToArray();
            foreach (var name in ready)
            {
                _states[name] = 1;
                Emit(new("step", new(name, "Starting.", "InProgress")));
            }

            return ready;
        }
    }

    internal void Report(string step, string status, bool complete, bool failed)
    {
        lock (_gate)
        {
            Running(step);
            status = Bounded(status);
            if (failed && !complete)
            {
                throw new ArgumentException("A failed step must be complete.");
            }
            if (complete)
            {
                if (!failed && (_taskFailures.Contains(step) || _tasks.Any(task => task.Key.StartsWith(step + "\0", StringComparison.Ordinal) && !task.Value)))
                {
                    throw new InvalidOperationException($"Step '{step}' has unfinished or failed reporting tasks.");
                }
                _states![step] = failed ? 3 : 2;
                _failed |= failed;
            }
            else
            {
                CountProgress();
            }
            Emit(new("step", new(step, status, complete && !failed && _warnings.Contains(step) ? "CompletedWithWarning" : State(complete, failed))));
        }
    }

    internal void Progress(string step, string type, string id, string status, bool complete, bool failed, bool markdown)
    {
        lock (_gate)
        {
            Running(step);
            id = Bounded(id);
            status = Bounded(status);
            if (failed && !complete)
            {
                throw new ArgumentException("Failed publishing progress must be complete.");
            }
            if (type == "summary")
            {
                if (_summary.Count == 1024)
                {
                    throw new InvalidOperationException("Pipeline summary exceeds 1024 entries.");
                }
                _summary.Add(new(id, status, markdown));
                return;
            }
            if (type == "step-warning")
            {
                _warnings.Add(step);
                return;
            }
            var warning = type == "task-warning";
            if (warning)
            {
                if (!complete)
                {
                    throw new ArgumentException("Warning task progress must be complete.");
                }
                type = "task";
                _warnings.Add(step);
            }
            var level = type switch
            {
                "log-error" => "Error", "log-warning" => "Warning", "log-debug" => "Debug",
                "log-trace" => "Trace", "log-critical" => "Critical", _ => "Information"
            };
            if (type is "log-error" or "log-warning" or "log-debug" or "log-trace" or "log-critical")
            {
                type = "log";
            }
            if (type is not ("task" or "log"))
            {
                throw new ArgumentException("Publishing progress must be a task, log, or summary.");
            }
            CountProgress();
            if (type == "task")
            {
                var key = step + "\0" + id;
                if (_tasks.GetValueOrDefault(key))
                {
                    throw new InvalidOperationException($"Reporting task '{id}' has already completed.");
                }
                if (!_tasks.ContainsKey(key) && (_tasks.Count == 1024 || complete))
                {
                    throw new InvalidOperationException("Create a reporting task before completing it (maximum 1024).");
                }
                _tasks[key] = complete;
                if (failed)
                {
                    _taskFailures.Add(step);
                    _failed = true;
                }
            }
            Emit(new(type, new(id, status, warning && complete ? "CompletedWithWarning" : State(complete, failed))
            {
                StepId = step, EnableMarkdown = markdown,
                LogLevel = type == "log" ? level : null,
                Timestamp = type == "log" ? DateTimeOffset.UtcNow : null
            }));
        }
    }

    internal void Complete(bool failed)
    {
        lock (_gate)
        {
            if (_complete)
            {
                throw new InvalidOperationException("Publishing has already completed.");
            }
            if (_states?.Values.Any(state => state == 1) == true)
            {
                throw new InvalidOperationException("Publishing still has running steps.");
            }
            if (!failed && (_states is null || _failed || _states.Values.Any(state => state != 2)))
            {
                throw new InvalidOperationException("Publishing cannot succeed with pending or failed steps.");
            }
            _complete = true;
            Emit(new("publish-complete", new("publish", failed ? "Publishing failed." : "Publishing completed.",
                !failed && _warnings.Count > 0 ? "CompletedWithWarning" : State(true, failed))
            {
                PipelineSummary = _summary.ToArray()
            }));
            _activities.Writer.TryComplete();
        }
    }

    private void Running(string step)
    {
        if (_complete || _states is null || _states.GetValueOrDefault(step, -1) != 1)
        {
            throw new InvalidOperationException($"Pipeline step '{step}' is not running.");
        }
    }

    private void Emit(NativePublishingActivity activity)
    {
        // Ordinary run clients do not subscribe to the publishing activity stream.
        // The guest writes startup diagnostics to its console instead; native state
        // still validates every claim, task transition, and terminal completion.
        if (!_waitsForStop)
        {
            return;
        }
        if (!_activities.Writer.TryWrite(activity))
        {
            throw new InvalidOperationException("Publishing activity buffer is full; consume CLI progress before reporting more events.");
        }
    }

    private void CountProgress()
    {
        if (!_waitsForStop)
        {
            return;
        }
        if (_progressCount >= 4096)
        {
            throw new InvalidOperationException("Publishing exceeds 4096 user progress events; reduce verbose task updates or logs.");
        }
        _progressCount++;
    }

    private static string State(bool complete, bool failed) => !complete ? "InProgress" : failed ? "CompletedWithError" : "Completed";
    private static string Bounded(string text) => text.Length <= 4096 ? text : throw new ArgumentException("Publishing text exceeds 4096 characters.");
    private static string Text(JsonElement element, int limit) =>
        element.ValueKind == JsonValueKind.String && element.GetString() is { Length: > 0 } value && value.Length <= limit && !value.Contains('\0')
            ? value : throw new ArgumentException("Pipeline text must be a nonempty bounded string without null characters.");
    private static string? OptionalText(JsonElement element, string name, int limit) =>
        !element.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null ? null : Text(value, limit);
    private static string[] Strings(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value))
        {
            return [];
        }
        if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() > 1024)
        {
            throw new ArgumentException($"Pipeline '{name}' must be an array of at most 1024 strings.");
        }

        return value.EnumerateArray().Select(item => Text(item, 256)).ToArray();
    }

    private static void Fields(JsonElement element, string[] allowed)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw new ArgumentException("Pipeline entries must be objects.");
        }
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            if (!allowed.Contains(property.Name, StringComparer.Ordinal) || !seen.Add(property.Name))
            {
                throw new ArgumentException($"Unknown or duplicate pipeline field '{property.Name}'.");
            }
        }
    }
}
