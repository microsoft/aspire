// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.CommandLine;
using Aspire.Cli.Agents;
using Aspire.Cli.Agents.AspireSkills;
using Aspire.Cli.Agents.Playwright;
using Aspire.Cli.Git;
using Aspire.Cli.NuGet;
using Aspire.Cli.Projects;
using Aspire.Cli.Resources;

namespace Aspire.Cli.Commands;

/// <summary>
/// Legacy command 'aspire mcp init' that delegates to the new AgentInitCommand.
/// This is kept for backward compatibility but is hidden from help.
/// </summary>
internal sealed class McpInitCommand : BaseCommand, IPackageMetaPrefetchingCommand
{
    private readonly AgentInitCommand _agentInitCommand;

    /// <summary>
    /// McpInitCommand does not need template package metadata prefetching.
    /// </summary>
    public bool PrefetchesTemplatePackageMetadata => false;

    /// <summary>
    /// McpInitCommand does not need CLI package metadata prefetching.
    /// </summary>
    public bool PrefetchesCliPackageMetadata => false;

    public McpInitCommand(
        IAgentEnvironmentDetector agentEnvironmentDetector,
        IAspireSkillsInstaller aspireSkillsInstaller,
        PlaywrightCliInstaller playwrightCliInstaller,
        IGitRepository gitRepository,
        ILanguageDiscovery languageDiscovery,
        CommonCommandServices services)
        : base("init", McpCommandStrings.InitCommand_Description, services)
    {
        // Create the AgentInitCommand to delegate execution to
        _agentInitCommand = new AgentInitCommand(
            agentEnvironmentDetector,
            aspireSkillsInstaller,
            playwrightCliInstaller,
            gitRepository,
            languageDiscovery,
            services);
    }

    protected override async Task<CommandResult> ExecuteAsync(ParseResult parseResult, CancellationToken cancellationToken)
    {
        // Display deprecation warning
        InteractionService.DisplayMarkupLine($"[yellow]⚠ {McpCommandStrings.DeprecatedCommandWarning}[/]");
        InteractionService.DisplayEmptyLine();

        // Delegate to the new AgentInitCommand
        return await _agentInitCommand.ExecuteCommandAsync(parseResult, cancellationToken);
    }
}
