// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using System.Text.Json.Nodes;
using Aspire.Cli.Agents;
using Aspire.Cli.Agents.AspireSkills;
using Aspire.Cli.Configuration;
using Aspire.Cli.Git;
using Aspire.Cli.Interaction;
using Aspire.Cli.Resources;
using Aspire.Cli.Utils;

namespace Aspire.Cli.Migrations;

/// <summary>
/// Replaces copied Aspire skills in the project and user profile with the configured skill sources.
/// </summary>
internal sealed class AgentSkillsMigration(
    CliExecutionContext executionContext,
    IGitRepository gitRepository,
    IConfigurationService configurationService,
    IEnumerable<IAgentClient> clients,
    IAgentEnvironmentDetector agentEnvironmentDetector,
    IInteractionService interactionService) : IMigration
{
    public string Id => "aspire-skill-sources";

    public int Order => 200;

    public async Task<MigrationDescriptor?> DetectAsync(MigrationContext context, CancellationToken cancellationToken)
    {
        var (directory, workspaceRoot) = await GetDirectoriesAsync(cancellationToken);
        var local = LocalAspireSkills.Find(directory, workspaceRoot, clients, cancellationToken);
        // An unreadable SKILL.md can be a copy that discovery could not confirm, so it stays pending.
        // Invalid settings alone are not, because they can come from unrelated agent configuration.
        if (local.Directories.Count == 0 && !local.HasFileErrors)
        {
            return null;
        }

        // Inspection errors block ApplyAsync, so show them instead of the generic detail.
        return new MigrationDescriptor
        {
            Title = MigrationStrings.AgentSkillsTitle,
            Detail = local.Errors.Count > 0 ? string.Join(Environment.NewLine, local.Errors) : MigrationStrings.AgentSkillsDetail,
            Files = LocalAspireSkills.GetDisplayPaths(local.Directories, workspaceRoot, executionContext),
            Metadata = new JsonObject
            {
                ["workspaceRoot"] = workspaceRoot.FullName
            }
        };
    }

    public async Task ApplyAsync(MigrationContext context, CancellationToken cancellationToken)
    {
        var (directory, workspaceRoot) = await GetDirectoriesAsync(cancellationToken);
        var local = LocalAspireSkills.Find(directory, workspaceRoot, clients, cancellationToken);
        if (local.Directories.Count == 0 && !local.HasFileErrors)
        {
            return;
        }

        if (local.Errors.Count > 0)
        {
            throw new InvalidOperationException(string.Join(Environment.NewLine, local.Errors));
        }

        // Discovery follows links, so a copy reached through a linked folder such as
        // .agents/skills -> ~/shared-skills lives outside this workspace or profile. Deleting it
        // would remove shared files, so refuse before registering or deleting anything.
        var linkedDirectories = local.Directories.Where(skillDirectory => IsBehindLink(skillDirectory, workspaceRoot)).ToArray();
        if (linkedDirectories.Length > 0)
        {
            throw new InvalidOperationException(string.Format(CultureInfo.CurrentCulture, MigrationStrings.AgentSkillsLinked,
                string.Join(", ", LocalAspireSkills.GetDisplayPaths(linkedDirectories, workspaceRoot, executionContext))));
        }

        // Resolve sources from the AppHost directory when there is one, as `aspire agent init`
        // does, so the migration registers the same sources that agent initialization would.
        var sources = SkillSource.Resolve(context.AppHostFile?.Directory ?? directory,
            new FileInfo(configurationService.GetSettingsFilePath(isGlobal: true)));

        // Register the sources before deleting any copy, so a failed registration leaves the copies
        // in place. With no enabled source there is nothing to register, and reporting the untouched
        // files as configured would mislead.
        var message = MigrationStrings.AgentSkillsDeletedNoSources;
        if (sources.Count > 0)
        {
            // Register only for the agents that `aspire agent init` selects by default. A copy in a
            // directory that several agents read does not show that each of them is in use.
            var detectedEnvironments = await interactionService.ShowStatusAsync(
                McpCommandStrings.InitCommand_DetectingAgentEnvironments,
                async () => await agentEnvironmentDetector.DetectAsync(
                    new AgentEnvironmentScanContext { WorkingDirectory = directory, RepositoryRoot = workspaceRoot }, cancellationToken),
                emoji: KnownEmojis.Robot);
            var defaultClients = AgentEnvironmentDetector.GetDefaultClients(clients, detectedEnvironments);
            var consumers = local.Consumers.Where(consumer => defaultClients.Contains(consumer.Client)).ToArray();
            interactionService.DisplaySkillSources(sources, [.. consumers.Select(consumer => consumer.Client)], executionContext);

            List<AgentTargetResult> results = [];
            foreach (var scope in consumers.GroupBy(consumer => consumer.Scope))
            {
                results.AddRange(await AgentConfiguration.ApplyRemoteAsync(
                    scope.Select(consumer => consumer.Client), workspaceRoot, scope.Key, sources, cancellationToken));
            }
            if (interactionService.DisplayRemoteConfigurationResults(results, workspaceRoot, executionContext))
            {
                throw new InvalidOperationException(MigrationStrings.AgentSkillsRegistrationFailed);
            }
            message = consumers.Length > 0 ? MigrationStrings.AgentSkillsApplied : MigrationStrings.AgentSkillsDeletedNoAgents;
        }

        // Configured skill sources supersede the copies and the CLI no longer updates them, so
        // copies are deleted even when no source is registered. Older CLI versions copied each skill
        // with supporting files such as references/, so the whole skill directory is removed.
        // A directory can already be gone when two skills paths resolve to one location.
        foreach (var skillDirectory in local.Directories)
        {
            if (Directory.Exists(skillDirectory))
            {
                Directory.Delete(skillDirectory, recursive: true);
            }
        }

        interactionService.DisplaySuccess(message);
    }

    // Agent skills belong to the workspace and user profile rather than an AppHost, so they resolve
    // from the current directory, as `aspire doctor` and `aspire agent init` do. This keeps the
    // skills that `aspire update --migrate` replaces identical to the ones doctor reports.
    private async Task<(DirectoryInfo Directory, DirectoryInfo WorkspaceRoot)> GetDirectoriesAsync(CancellationToken cancellationToken)
    {
        var directory = executionContext.WorkingDirectory;
        return (directory, await gitRepository.GetRootAsync(cancellationToken) ?? directory);
    }

    // Checks the skill directory and its ancestors below the workspace root or user profile.
    // Those roots themselves can be links, such as a home directory on a mounted volume.
    private bool IsBehindLink(string skillDirectory, DirectoryInfo workspaceRoot)
    {
        for (var directory = new DirectoryInfo(skillDirectory); directory is not null; directory = directory.Parent)
        {
            if (Path.GetRelativePath(workspaceRoot.FullName, directory.FullName) == "." ||
                Path.GetRelativePath(executionContext.HomeDirectory.FullName, directory.FullName) == ".")
            {
                return false;
            }

            if (directory.LinkTarget is not null)
            {
                return true;
            }
        }

        return false;
    }
}
