// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Agents;
using Aspire.Cli.Agents.ClaudeCode;
using Aspire.Cli.Agents.Copilot;
using Aspire.Cli.Agents.Hooks;
using Aspire.Cli.Agents.OpenCode;
using Aspire.Cli.Agents.Playwright;
using Aspire.Cli.Agents.VsCode;
using Aspire.Cli.Commands;
using Aspire.Cli.Npm;
using Aspire.Cli.Tests.TestServices;
using Aspire.Cli.Tests.Utils;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Configuration;
using Semver;

namespace Aspire.Cli.Tests.Agents;

/// <summary>
/// Isolates every native user, project and file-based policy destination from the test host.
/// </summary>
internal sealed class AgentConfigurationTestContext : IDisposable
{
    private readonly Dictionary<string, string?> _variables;

    public AgentConfigurationTestContext(ITestOutputHelper output)
    {
        Workspace = TemporaryWorkspace.Create(output);
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
        CliRunner = new TestAgentCliRunner();
        Environments = CliRunner.CreateScanners(ExecutionContext, Environment);
        Writer = new AgentConfigurationWriter(NullLogger<AgentConfigurationWriter>.Instance);
        HookInstaller = new TestAgentConfigurationHookInstaller(ExecutionContext);
        Hooks = new TelemetryHookConfigurator(HookInstaller, Environments, ExecutionContext, NullLogger<TelemetryHookConfigurator>.Instance);
        SkillInstaller = new TestAgentConfigurationSkillInstaller();
    }

    public TemporaryWorkspace Workspace { get; }
    public DirectoryInfo Project { get; }
    public DirectoryInfo Home { get; }
    public TestEnvironment Environment { get; }
    public CliExecutionContext ExecutionContext { get; }
    public IReadOnlyList<IAgentEnvironmentScanner> Environments { get; }
    public IAgentEnvironmentScanner Copilot => Environments.Single(scanner => scanner.Id == "copilot");
    public IAgentEnvironmentScanner VsCode => Environments.Single(scanner => scanner.Id == "vscode");
    public IAgentEnvironmentScanner ClaudeCode => Environments.Single(scanner => scanner.Id == "claude");
    public IAgentEnvironmentScanner OpenCode => Environments.Single(scanner => scanner.Id == "opencode");
    public TestAgentCliRunner CliRunner { get; }
    public string CopilotDirectory => CopilotPaths.GetConfigDirectory(ExecutionContext, Environment);
    public string ClaudeDirectory => ClaudeCodeAgentEnvironmentScanner.GetConfigDirectory(ExecutionContext, Environment);
    public string ClaudeMcpFile => ClaudeCodeAgentEnvironmentScanner.GetMcpFile(ExecutionContext, Environment);
    public string ClaudeManagedDirectory => ClaudeCodeAgentEnvironmentScanner.GetManagedDirectory(ExecutionContext, Environment);
    public string OpenCodeDirectory => OpenCodeAgentEnvironmentScanner.GetConfigDirectory(ExecutionContext, Environment);
    public AgentConfigurationWriter Writer { get; }
    public TestAgentConfigurationHookInstaller HookInstaller { get; }
    public TestAgentConfigurationSkillInstaller SkillInstaller { get; }
    public TelemetryHookConfigurator Hooks { get; }
    public FakeNpmRunner Npm { get; } = new() { ResolveResult = new NpmPackageInfo { Version = new SemVersion(0, 1, 7) } };
    public FakePlaywrightCliRunner Playwright { get; } = new();
    public FakeNpmProvenanceChecker Provenance { get; } = new();

    public AgentSkillInstaller CreateManagedSkillInstaller(DirectoryInfo? workingDirectory = null, DirectoryInfo? homeDirectory = null)
        => new(new PlaywrightCliInstaller(Npm, Provenance, Playwright, new TestInteractionService(),
            new ConfigurationBuilder().Build(), NullLogger<PlaywrightCliInstaller>.Instance),
            workingDirectory is null && homeDirectory is null
                ? ExecutionContext
                : TestExecutionContextHelper.CreateExecutionContext(workingDirectory ?? Project, homeDirectory: homeDirectory ?? Home),
            Environment, NullLogger<AgentSkillInstaller>.Instance);

    public AgentInitRequest ManagedRequest(IReadOnlyList<IAgentEnvironmentScanner> clients, bool playwright = true, bool dotnetInspect = true)
        => Request(clients, skills: false, playwright: playwright, dotnetInspect: dotnetInspect);

    public void SetVariable(string name, string value) => _variables[name] = value;

    public string VsCodeUserDirectory(bool insiders) => VsCodeAgentEnvironmentScanner.GetUserDirectory(insiders, ExecutionContext, Environment);

    public AgentInitRequest Request(
        IReadOnlyList<IAgentEnvironmentScanner> clients,
        bool skills = true,
        bool mcp = false,
        bool playwright = false,
        bool dotnetInspect = false,
        IReadOnlyList<AgentClientDetection>? detections = null)
        => new(Project, new AgentAssetSelection(mcp, playwright, dotnetInspect, skills), clients, detections ?? []);

    public Task<IReadOnlyList<AgentTargetResult>> ConfigureNativeAsync(AgentInitRequest request, CancellationToken cancellationToken = default)
        => Writer.ApplyAsync(request.Environments.Distinct().SelectMany(environment => environment.GetTargets(request)), cancellationToken);

    public Task<AgentInitResult> ConfigureAsync(AgentInitRequest request, CancellationToken cancellationToken)
        => AgentInitCommand.ConfigureAsync(request, Writer, SkillInstaller, Hooks, cancellationToken);

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
    public Func<AgentInitRequest, CancellationToken, Task<IReadOnlyList<AgentTargetResult>>>? InstallAsyncCallback { get; set; }

    public Task<IReadOnlyList<AgentTargetResult>> InstallAsync(AgentInitRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Requests.Add(request);
        return InstallAsyncCallback?.Invoke(request, cancellationToken) ?? Task.FromResult(Results);
    }
}
