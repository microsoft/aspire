// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using System.Text.Json;
using Aspire.Cli.Resources;

namespace Aspire.Cli.Agents.AspireSkills;

internal static class LocalAspireSkills
{
    // These names identify skills copied by older CLIs, not the current plugin catalog.
    // Keep them independent of bundles so leftover files remain discoverable after bundle removal.
    private static readonly string[] s_legacySkillNames =
    [
        "aspire",
        "aspire-deployment",
        "aspire-init",
        "aspire-monitoring",
        "aspire-orchestration",
        "aspire-project-v2-migration",
        "aspireify"
    ];

    internal static LocalAspireSkillsResult Find(
        DirectoryInfo workingDirectory,
        DirectoryInfo workspaceRoot,
        IEnumerable<IAgentClient> agents,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // Clients load project skills along the active ancestor chain, not just at its root.
        // Clamping to the workspace excludes sibling projects and unrelated current directories.
        var projectDirectories = AgentPath.ProjectDirectories(workingDirectory, workspaceRoot).ToArray();
        var skills = new HashSet<string>(StringComparers.FileSystemPath);
        var errors = new HashSet<string>(StringComparer.Ordinal);
        var consumers = new HashSet<(IAgentClient Client, AgentConfigurationScope Scope)>();
        var hasFileErrors = false;
        foreach (var agent in agents)
        {
            foreach (var scope in new[] { AgentConfigurationScope.Project, AgentConfigurationScope.Global })
            {
                cancellationToken.ThrowIfCancellationRequested();
                List<string> directories = [];
                DirectoryInfo[] roots = scope is AgentConfigurationScope.Project ? projectDirectories : [workspaceRoot];
                foreach (var root in roots)
                {
                    // Clients yield their standard skill roots before parsing settings that add more.
                    // Collect as they are yielded so a settings failure still probes the standard roots,
                    // and isolate each project directory so invalid settings in a nested directory
                    // do not hide skills in its ancestors.
                    try
                    {
                        foreach (var directory in agent.GetLocalSkillDirectories(scope, root))
                        {
                            directories.Add(directory);
                        }
                    }
                    catch (Exception ex) when (ex is JsonException or InvalidOperationException or ArgumentNullException)
                    {
                        // Invalid skill settings block plugin setup, not independently selected MCP.
                        errors.Add(string.Format(CultureInfo.CurrentCulture, AgentCommandStrings.LocalSkills_ReadFailed, agent.DisplayName, ex.Message));
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
                    {
                        hasFileErrors = true;
                        errors.Add(string.Format(CultureInfo.CurrentCulture, AgentCommandStrings.LocalSkills_ReadFailed, agent.DisplayName, ex.Message));
                    }
                }

                foreach (var directory in directories)
                {
                    foreach (var name in s_legacySkillNames)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        // Clients can share a skill directory, so each client probes its own paths
                        // and is recorded as a consumer of every copied skill it loads.
                        var skillDirectory = Path.Combine(directory, name);
                        var path = Path.Combine(skillDirectory, "SKILL.md");
                        try
                        {
                            // Older CLI versions copied Markdown verbatim, without an ownership
                            // stamp or installed version. Names identify possible collisions only;
                            // even byte-identical direct installs cannot be classified as obsolete.
                            using var stream = File.OpenRead(path);
                            skills.Add(Path.GetFullPath(skillDirectory));
                            consumers.Add((agent, scope));
                        }
                        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
                        {
                            // Missing entry points are expected; unreadable ones remain errors.
                        }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
                        {
                            hasFileErrors = true;
                            errors.Add(string.Format(CultureInfo.CurrentCulture, AgentCommandStrings.LocalSkills_ReadFailed, path, ex.Message));
                        }
                    }
                }
            }
        }

        return new(skills.Order(StringComparers.FileSystemPath).ToArray(), errors.Order(StringComparer.Ordinal).ToArray(), hasFileErrors, consumers.ToArray());
    }

    internal static IReadOnlyList<string> GetDisplayPaths(IEnumerable<string> directories, DirectoryInfo workspaceRoot, CliExecutionContext executionContext)
        => directories.Select(directory => AgentPath.GetDisplayPath(directory, workspaceRoot, executionContext))
            .Order(StringComparer.Ordinal).ToArray();
}

internal sealed record LocalAspireSkillsResult(
    IReadOnlyList<string> Directories,
    IReadOnlyList<string> Errors,
    bool HasFileErrors,
    IReadOnlyList<(IAgentClient Client, AgentConfigurationScope Scope)> Consumers);
