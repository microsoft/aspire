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
        cancellationToken.ThrowIfCancellationRequested();
        var workingDirectory = context.AppHostFile?.Directory ?? executionContext.WorkingDirectory;
        var root = GetWorkspaceRoot(workingDirectory);

        var scan = await LocalAspireSkills.FindAsync(workingDirectory, root, agents, executionContext, environment, cancellationToken);
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
        cancellationToken.ThrowIfCancellationRequested();
        var workingDirectory = context.AppHostFile?.Directory ?? executionContext.WorkingDirectory;
        var root = GetWorkspaceRoot(workingDirectory);

        // Re-detect after the update confirmation; files can disappear or change meanwhile.
        // The old installer supplied no version/ownership marker. Neither a skill name nor
        // successful offline registration authorizes deleting a potentially customized file.
        var scan = await LocalAspireSkills.FindAsync(workingDirectory, root, agents, executionContext, environment, cancellationToken);
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
        var result = await agentInit.MigrateLocalSkillsAsync(root, workingDirectory, defaultScope, cancellationToken);
        if (result.ExitCode != CliExitCodes.Success || result.RegisteredEnvironments.Count == 0)
        {
            interactionService.DisplayMessage(KnownEmojis.Warning, AgentCommandStrings.LocalSkills_MigrationIncomplete);
        }
        else
        {
            interactionService.DisplayMessage(KnownEmojis.Warning, AgentCommandStrings.LocalSkills_MigrationReview);
        }
    }

    private DirectoryInfo GetWorkspaceRoot(DirectoryInfo start)
    {
        DirectoryInfo? solutionRoot = null;

        // Resolve from the selected AppHost, not a different repository containing the CLI's
        // current directory. .git can be a directory or a worktree's gitdir pointer file.
        for (var directory = start; directory is not null; directory = directory.Parent)
        {
            if (Path.Exists(Path.Combine(directory.FullName, ".git")))
            {
                return directory;
            }

            // Personal configuration is not a project boundary. Do not adopt a solution
            // in or above the user's home when resolving a project underneath it.
            if (Path.GetRelativePath(executionContext.HomeDirectory.FullName, directory.FullName) == ".")
            {
                break;
            }

            if (solutionRoot is null &&
                directory.EnumerateFiles("*.sln").Concat(directory.EnumerateFiles("*.slnx")).Any())
            {
                solutionRoot = directory;
            }
        }

        // An ancestor's aspire.config.json can be unrelated (including a user-level file).
        // A solution supplies the non-Git boundary; otherwise retain the selected directory.
        return solutionRoot ?? start;
    }
}
