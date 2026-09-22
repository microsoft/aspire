// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Concurrent;
using Aspire.Cli.Agents;
using Aspire.Cli.Agents.ClaudeCode;
using Aspire.Cli.Agents.Copilot;
using Aspire.Cli.Agents.OpenCode;
using Aspire.Cli.Agents.VsCode;
using Microsoft.Extensions.Logging.Abstractions;
using Semver;

namespace Aspire.Cli.Tests.TestServices;

internal sealed class TestAgentCliRunner : ICopilotCliRunner, IClaudeCodeCliRunner, IOpenCodeCliRunner, IVsCodeCliRunner
{
    private readonly ConcurrentQueue<string> _commands = new();

    public SemVersion? CopilotVersion { get; init; }
    public SemVersion? ClaudeCodeVersion { get; init; }
    public SemVersion? OpenCodeVersion { get; init; }
    public SemVersion? VsCodeVersion { get; init; }
    public SemVersion? VsCodeInsidersVersion { get; init; }
    public IReadOnlyList<string> Commands => _commands.ToArray();
    public Func<string, CancellationToken, Task<SemVersion?>>? GetVersionAsyncCallback { get; init; }

    public AgentClientCatalog CreateCatalog(CliExecutionContext executionContext, IEnvironment environment)
        => new(
            new CopilotAgentEnvironmentScanner(this, new CopilotAppInstallationDetector(environment, executionContext), executionContext, environment, NullLogger<CopilotAgentEnvironmentScanner>.Instance),
            new VsCodeAgentEnvironmentScanner(this, executionContext, environment, NullLogger<VsCodeAgentEnvironmentScanner>.Instance),
            new ClaudeCodeAgentEnvironmentScanner(this, executionContext, environment, NullLogger<ClaudeCodeAgentEnvironmentScanner>.Instance),
            new OpenCodeAgentEnvironmentScanner(this, executionContext, environment, NullLogger<OpenCodeAgentEnvironmentScanner>.Instance));

    Task<SemVersion?> ICopilotCliRunner.GetVersionAsync(CancellationToken cancellationToken)
        => GetVersionAsync("copilot", CopilotVersion, cancellationToken);

    Task<SemVersion?> IClaudeCodeCliRunner.GetVersionAsync(CancellationToken cancellationToken)
        => GetVersionAsync("claude", ClaudeCodeVersion, cancellationToken);

    Task<SemVersion?> IOpenCodeCliRunner.GetVersionAsync(CancellationToken cancellationToken)
        => GetVersionAsync("opencode", OpenCodeVersion, cancellationToken);

    public Task<SemVersion?> GetVersionAsync(VsCodeRunOptions options, CancellationToken cancellationToken)
        => options.UseInsiders
            ? GetVersionAsync("code-insiders", VsCodeInsidersVersion, cancellationToken)
            : GetVersionAsync("code", VsCodeVersion, cancellationToken);

    private Task<SemVersion?> GetVersionAsync(string command, SemVersion? version, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _commands.Enqueue(command);

        return GetVersionAsyncCallback?.Invoke(command, cancellationToken) ?? Task.FromResult(version);
    }
}
