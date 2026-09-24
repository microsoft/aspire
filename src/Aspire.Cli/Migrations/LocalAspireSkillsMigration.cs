// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using System.Text.Json.Nodes;
using Aspire.Cli.Agents;
using Aspire.Cli.Commands;
using Aspire.Cli.Interaction;
using Aspire.Cli.Resources;

namespace Aspire.Cli.Migrations;

/// <summary>
/// Offers plugin registration for projects with local Aspire skills, preserving their unverified content.
/// </summary>
internal sealed class LocalAspireSkillsMigration(
    IEnumerable<IAgentEnvironmentScanner> agents,
    AgentInitCommand agentInit,
    CliExecutionContext executionContext,
    IEnvironment environment,
    IInteractionService interactionService) : IMigration
{
    public string Id => "local-aspire-skills";
    public int Order => 200;

    public async Task<MigrationDescriptor?> DetectAsync(MigrationContext context, CancellationToken cancellationToken)
    {
        var root = GetWorkspaceRoot(context);
        var scan = await LocalAspireSkills.FindAsync(root, agents, executionContext, environment, cancellationToken);
        if (scan.Files.Count == 0 && scan.Errors.Count == 0)
        {
            return null;
        }

        var paths = string.Join(Environment.NewLine, scan.Files.Select(file => file.Path).Concat(scan.Errors));
        return new MigrationDescriptor
        {
            Title = string.Format(CultureInfo.CurrentCulture, AgentCommandStrings.LocalSkills_MigrationTitle, paths),
            Detail = string.Format(CultureInfo.CurrentCulture, AgentCommandStrings.LocalSkills_MigrationDetail, paths),
            Fix = string.Format(CultureInfo.CurrentCulture, AgentCommandStrings.LocalSkills_MigrationGuidance, AspireSkillsPluginConfiguration.RepositoryUrl),
            Metadata = new JsonObject
            {
                ["workspaceRoot"] = root.FullName,
                ["files"] = new JsonArray(scan.Files.Select(file => (JsonNode)new JsonObject
                {
                    ["path"] = file.Path,
                    ["scope"] = file.Scope is AgentConfigurationScope.Project ? "project" : "user"
                }).ToArray()),
                ["preservesLocalFiles"] = true
            }
        };
    }

    public async Task ApplyAsync(MigrationContext context, CancellationToken cancellationToken)
    {
        var root = GetWorkspaceRoot(context);
        // Re-detect after the update confirmation; files can disappear or change meanwhile.
        // The old installer supplied no version/ownership marker. Neither a skill name nor
        // successful offline registration authorizes deleting a potentially customized file.
        var scan = await LocalAspireSkills.FindAsync(root, agents, executionContext, environment, cancellationToken);
        foreach (var error in scan.Errors)
        {
            interactionService.DisplayMessage(KnownEmojis.Warning, error);
        }
        if (scan.Files.Count == 0)
        {
            return;
        }

        var defaultScope = scan.Files.All(file => file.Scope is AgentConfigurationScope.User)
            ? AgentConfigurationScope.User : AgentConfigurationScope.Project;
        var result = await agentInit.MigrateLocalSkillsAsync(root, defaultScope, cancellationToken);
        if (result.ExitCode != CliExitCodes.Success || result.RegisteredEnvironments.Count == 0)
        {
            interactionService.DisplayMessage(KnownEmojis.Warning, AgentCommandStrings.LocalSkills_MigrationIncomplete);
        }
        else
        {
            interactionService.DisplayMessage(KnownEmojis.Warning, AgentCommandStrings.LocalSkills_MigrationReview);
        }
    }

    private DirectoryInfo GetWorkspaceRoot(MigrationContext context)
    {
        var start = context.AppHostFile?.Directory ?? executionContext.WorkingDirectory;
        // Resolve from the selected AppHost, not a different repository containing the CLI's
        // current directory. .git can be a directory or a worktree's gitdir pointer file.
        for (var directory = start; directory is not null; directory = directory.Parent)
        {
            if (Path.Exists(Path.Combine(directory.FullName, ".git")))
            {
                return directory;
            }
        }

        // An ancestor's aspire.config.json can be unrelated (including a user-level file).
        // Without a repository boundary, keep discovery and registration at the selected directory.
        return start;
    }
}
