// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using Aspire.Cli.Resources;

namespace Aspire.Cli.Agents;

/// <summary>
/// Finds local Aspire skill entry points without assuming who installed them or changing their content.
/// </summary>
internal static class LocalAspireSkills
{
    private static readonly string[] s_skillNames =
    [
        "aspire", "aspire-init", "aspireify", "aspire-deployment",
        "aspire-monitoring", "aspire-orchestration", "aspire-project-v2-migration"
    ];

    public static LocalAspireSkillScan Find(
        DirectoryInfo workingDirectory,
        DirectoryInfo workspaceRoot,
        IEnumerable<IAgentClient> agents,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // Clients load project skills along the active ancestor chain, not just at its root.
        // Clamping to the workspace excludes sibling projects and unrelated current directories.
        var projectDirectories = AgentPath.ProjectDirectories(workingDirectory, workspaceRoot).ToArray();
        var files = new Dictionary<string, string>(AgentPath.Comparer);
        var errors = new HashSet<string>(StringComparer.Ordinal);
        var inspected = new HashSet<string>(AgentPath.Comparer);
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
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
                {
                    errors.Add(string.Format(CultureInfo.CurrentCulture, AgentCommandStrings.LocalSkills_ReadFailed, agent.DisplayName, ex.Message));
                    continue;
                }

                foreach (var directory in directories)
                {
                    foreach (var name in s_skillNames)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var path = Path.Combine(directory, name, "SKILL.md");
                        if (!inspected.Add(path))
                        {
                            continue;
                        }

                        try
                        {
                            var physicalPath = AgentPath.Resolve(path);
                            // Older CLI versions copied Markdown verbatim, without an ownership
                            // stamp or installed version. Names identify possible collisions only;
                            // even byte-identical direct installs cannot be classified as obsolete.
                            using var stream = File.OpenRead(physicalPath);
                            files.TryAdd(physicalPath, path);
                        }
                        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
                        {
                            // Missing entry points are expected; unreadable ones remain errors.
                        }
                        catch (Exception ex) when (ex is AgentConfigurationException or IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
                        {
                            errors.Add(string.Format(CultureInfo.CurrentCulture, AgentCommandStrings.LocalSkills_ReadFailed, path, ex.Message));
                        }
                    }
                }
            }
        }

        return new(files.Values.Order(AgentPath.Comparer).ToArray(), errors.Order(StringComparer.Ordinal).ToArray());
    }
}

internal sealed record LocalAspireSkillScan(IReadOnlyList<string> Files, IReadOnlyList<string> Errors);
