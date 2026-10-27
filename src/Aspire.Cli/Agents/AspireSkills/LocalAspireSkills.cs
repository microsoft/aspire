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

    internal static (IReadOnlyList<string> Files, IReadOnlyList<string> Errors, bool HasFileErrors) Find(
        DirectoryInfo workingDirectory,
        DirectoryInfo workspaceRoot,
        IEnumerable<IAgentClient> agents,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // Clients load project skills along the active ancestor chain, not just at its root.
        // Clamping to the workspace excludes sibling projects and unrelated current directories.
        var projectDirectories = AgentPath.ProjectDirectories(workingDirectory, workspaceRoot).ToArray();
        var files = new Dictionary<string, string>(StringComparers.FileSystemPath);
        var errors = new HashSet<string>(StringComparer.Ordinal);
        var inspected = new HashSet<string>(StringComparers.FileSystemPath);
        var hasFileErrors = false;
        foreach (var agent in agents)
        {
            foreach (var scope in new[] { AgentConfigurationScope.Project, AgentConfigurationScope.Global })
            {
                cancellationToken.ThrowIfCancellationRequested();
                string[] directories;
                try
                {
                    directories = scope is AgentConfigurationScope.Project
                        ? projectDirectories.SelectMany(directory => agent.GetLocalSkillDirectories(scope, directory)).ToArray()
                        : agent.GetLocalSkillDirectories(scope, workspaceRoot).ToArray();
                }
                catch (Exception ex) when (ex is JsonException or InvalidOperationException or ArgumentNullException)
                {
                    // Invalid skill settings block plugin setup, not independently selected MCP.
                    errors.Add(string.Format(CultureInfo.CurrentCulture, AgentCommandStrings.LocalSkills_ReadFailed, agent.DisplayName, ex.Message));
                    continue;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
                {
                    hasFileErrors = true;
                    errors.Add(string.Format(CultureInfo.CurrentCulture, AgentCommandStrings.LocalSkills_ReadFailed, agent.DisplayName, ex.Message));
                    continue;
                }

                foreach (var directory in directories)
                {
                    foreach (var name in s_legacySkillNames)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var path = Path.Combine(directory, name, "SKILL.md");
                        if (!inspected.Add(path))
                        {
                            continue;
                        }

                        try
                        {
                            var fullPath = Path.GetFullPath(path);
                            // Older CLI versions copied Markdown verbatim, without an ownership
                            // stamp or installed version. Names identify possible collisions only;
                            // even byte-identical direct installs cannot be classified as obsolete.
                            using var stream = File.OpenRead(fullPath);
                            files.TryAdd(fullPath, path);
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

        return (files.Values.Order(StringComparers.FileSystemPath).ToArray(), errors.Order(StringComparer.Ordinal).ToArray(), hasFileErrors);
    }
}
