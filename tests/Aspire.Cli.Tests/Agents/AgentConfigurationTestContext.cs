// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Agents;
using Aspire.Cli.Agents.Configuration;
using Aspire.Cli.Agents.Hooks;
using Aspire.Cli.Tests.Utils;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aspire.Cli.Tests.Agents;

/// <summary>
/// Isolates every native user, project and file-based policy destination from the test host.
/// </summary>
internal sealed class AgentConfigurationTestContext : IDisposable
{
    private readonly Dictionary<string, string?> _variables;

    public AgentConfigurationTestContext(ITestOutputHelper output)
    {
        Workspace = TemporaryWorkspace.CreateForCli(output);
        Project = Workspace.CreateDirectory("project");
        Home = Workspace.CreateDirectory("home");
        _variables = new Dictionary<string, string?>
        {
            ["ProgramFiles"] = Path.Combine(Workspace.WorkspaceRoot.FullName, "system"),
            ["ProgramData"] = Path.Combine(Workspace.WorkspaceRoot.FullName, "system-data")
        };
        // Simulate Windows path selection on every OS, keeping system policy reads under
        // the fixture rather than depending on policies installed on the test machine.
        Environment = TestEnvironment.CreateWindows(_variables);
        ExecutionContext = TestExecutionContextHelper.CreateExecutionContext(Project, homeDirectory: Home);
        Paths = new AgentConfigurationPaths(ExecutionContext, Environment);
        Writer = new AgentConfigurationWriter(NullLogger<AgentConfigurationWriter>.Instance);
        HookInstaller = new TestAgentConfigurationHookInstaller(ExecutionContext);
        Hooks = new TelemetryHookConfigurator(HookInstaller, ExecutionContext, Paths, NullLogger<TelemetryHookConfigurator>.Instance);
        SkillInstaller = new TestAgentConfigurationSkillInstaller();
        Planner = new AgentConfigurationPlanner(
        [
            new CopilotConfigurationHandler(Paths),
            new ClaudeCodeConfigurationHandler(Paths),
            new VsCodeConfigurationHandler(Paths),
            new OpenCodeConfigurationHandler(Paths, Environment)
        ]);
        Service = new AgentInitService(Planner, Writer, SkillInstaller, Hooks);
    }

    public TemporaryWorkspace Workspace { get; }
    public DirectoryInfo Project { get; }
    public DirectoryInfo Home { get; }
    public TestEnvironment Environment { get; }
    public CliExecutionContext ExecutionContext { get; }
    public AgentConfigurationPaths Paths { get; }
    public AgentConfigurationWriter Writer { get; }
    public AgentConfigurationPlanner Planner { get; }
    public TestAgentConfigurationHookInstaller HookInstaller { get; }
    public TestAgentConfigurationSkillInstaller SkillInstaller { get; }
    public TelemetryHookConfigurator Hooks { get; }
    public AgentInitService Service { get; }

    public void SetVariable(string name, string value) => _variables[name] = value;

    public AgentInitRequest Request(
        IReadOnlyList<AgentClientKind> clients,
        bool skills = true,
        bool mcp = false,
        bool playwright = false,
        bool dotnetInspect = false,
        IReadOnlyList<AgentClientDetection>? detections = null)
        => new(Project, new AgentAssetSelection(mcp, playwright, dotnetInspect, skills), clients, detections ?? []);

    public Task<IReadOnlyList<AgentTargetResult>> ConfigureNativeAsync(AgentInitRequest request, CancellationToken cancellationToken = default)
        => Writer.ApplyAsync(Planner.GetTargets(request), cancellationToken);

    public static async Task WriteAsync(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, content);
    }

    public void Dispose() => Workspace.Dispose();
}

/// <summary>
/// Records embedded-hook delivery requests without extracting scripts or executing a client.
/// </summary>
internal sealed class TestAgentConfigurationHookInstaller(CliExecutionContext context) : ITelemetryHookInstaller
{
    public int Calls { get; private set; }
    public Exception? Error { get; set; }

    public Task<TelemetryHookScripts> EnsureInstalledAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Calls++;
        if (Error is not null)
        {
            return Task.FromException<TelemetryHookScripts>(Error);
        }

        return Task.FromResult(new TelemetryHookScripts(
            Path.Combine(context.AspireHomeDirectory.FullName, "hooks", "track-telemetry.sh"),
            Path.Combine(context.AspireHomeDirectory.FullName, "hooks", "track-telemetry.ps1")));
    }
}

/// <summary>
/// Records managed acquisition invocations independently of native registration.
/// </summary>
internal sealed class TestAgentConfigurationSkillInstaller : IAgentSkillInstaller
{
    public List<AgentInitRequest> Requests { get; } = [];
    public IReadOnlyList<AgentTargetResult> Results { get; set; } = [];

    public Task<IReadOnlyList<AgentTargetResult>> InstallAsync(AgentInitRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Requests.Add(request);
        return Task.FromResult(Results);
    }
}
