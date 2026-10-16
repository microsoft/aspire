// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Agents;
using Aspire.Cli.Agents.ClaudeCode;
using Aspire.Cli.Agents.Copilot;
using Aspire.Cli.Agents.OpenCode;
using Aspire.Cli.Tests.Utils;

namespace Aspire.Cli.Tests.Agents;

/// <summary>
/// Isolates native project, user, and managed-policy files from the test host.
/// </summary>
internal sealed class AgentConfigurationTestContext : IDisposable
{
    private readonly Dictionary<string, string?> _variables;

    public AgentConfigurationTestContext(ITestOutputHelper output)
    {
        Workspace = TemporaryWorkspace.Create(output);
        Project = Workspace.CreateDirectory("project");
        Home = Workspace.CreateDirectory("home");
        _variables = new()
        {
            ["ProgramFiles"] = Path.Combine(Workspace.WorkspaceRoot.FullName, "system"),
            ["ProgramData"] = Path.Combine(Workspace.WorkspaceRoot.FullName, "system-data")
        };
        // Keep platform-specific policy reads inside the fixture on every test OS.
        Environment = TestEnvironment.CreateWindows(_variables);
        ExecutionContext = TestExecutionContextHelper.CreateExecutionContext(Project, homeDirectory: Home);
        Copilot = new CopilotAgentClient(ExecutionContext, Environment);
        ClaudeCode = new ClaudeCodeAgentClient(ExecutionContext, Environment);
        OpenCode = new OpenCodeAgentClient(ExecutionContext, Environment);
        Clients = [Copilot, ClaudeCode, OpenCode];
    }

    public TemporaryWorkspace Workspace { get; }
    public DirectoryInfo Project { get; }
    public DirectoryInfo Home { get; }
    public TestEnvironment Environment { get; }
    public CliExecutionContext ExecutionContext { get; }
    public IReadOnlyList<IAgentClient> Clients { get; }
    public IAgentClient Copilot { get; }
    public IAgentClient ClaudeCode { get; }
    public IAgentClient OpenCode { get; }
    public string CopilotDirectory => AgentPath.Expand(CopilotPaths.GetConfigDirectory(Home, Environment), ExecutionContext);
    public string ClaudeDirectory => ClaudeCodePaths.GetConfigDirectory(ExecutionContext, Environment);
    public string ClaudeManagedDirectory => AgentPath.GetManagedSettingsDirectory(ExecutionContext, Environment, "ClaudeCode", "claude-code");
    public string OpenCodeDirectory => OpenCodePaths.GetConfigDirectory(ExecutionContext, Environment);

    public void SetVariable(string name, string value) => _variables[name] = value;

    public async Task<IReadOnlyList<AgentTargetResult>> ConfigureNativeAsync(
        AgentConfigurationScope scope, IReadOnlyList<IAgentClient> clients, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var edits = clients.Distinct().Select(client => client.CreateNativeSkillsEdit(Project, scope)).ToArray();

        return await AgentConfigurationEditor.ApplyAsync(edits, cancellationToken);
    }

    public static async Task WriteAsync(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, content);
    }

    public void Dispose() => Workspace.Dispose();
}
