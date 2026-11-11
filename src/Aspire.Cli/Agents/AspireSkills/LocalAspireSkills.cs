// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

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

    // The fixed locations older CLIs copied skills into (their --skill-location choices).
    // Only these are probed: copies never went to agent-specific roots such as CLAUDE_CONFIG_DIR
    // or OpenCode skills.paths, so reading those would inspect files the CLI never wrote.
    private static readonly string[] s_projectSkillDirectories =
    [
        AgentPath.UniversalSkillsDirectory,
        Path.Combine(".claude", "skills"),
        Path.Combine(".github", "skills"),
        Path.Combine(".opencode", "skill")
    ];

    /// <summary>
    /// Finds skill directories copied by older CLIs in the workspace (tagged <see cref="AgentConfigurationScope.Project"/>)
    /// and in the user-level standard location (tagged <see cref="AgentConfigurationScope.Global"/>).
    /// </summary>
    internal static IReadOnlyList<(string Directory, AgentConfigurationScope Scope)> Find(
        DirectoryInfo workingDirectory,
        DirectoryInfo workspaceRoot,
        DirectoryInfo homeDirectory,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // Clients load project skills along the active ancestor chain, not just at its root.
        // Clamping to the workspace excludes sibling projects and unrelated current directories.
        // Older CLIs only installed user-level copies to the standard location.
        var candidates = AgentPath.ProjectDirectories(workingDirectory, workspaceRoot)
            .SelectMany(root => s_projectSkillDirectories.Select(directory =>
                (Path: Path.Combine(root.FullName, directory), Scope: AgentConfigurationScope.Project)))
            .Append((Path: Path.Combine(homeDirectory.FullName, AgentPath.UniversalSkillsDirectory), Scope: AgentConfigurationScope.Global));

        // A workspace rooted at the home directory shares the user-level location; the first
        // (project) match wins so project-scoped removal still covers it.
        var found = new Dictionary<string, AgentConfigurationScope>(StringComparers.FileSystemPath);
        foreach (var (skillsDirectory, scope) in candidates)
        {
            foreach (var name in s_legacySkillNames)
            {
                cancellationToken.ThrowIfCancellationRequested();

                // Older CLIs copied each skill as a directory with supporting files (for example
                // references/) and no ownership stamp, so the directory name is the only signal.
                // Directory.Exists treats inaccessible paths as missing, which is correct here:
                // an agent running as the same user cannot load them either.
                var path = Path.GetFullPath(Path.Combine(skillsDirectory, name));
                if (Directory.Exists(path))
                {
                    found.TryAdd(path, scope);
                }
            }
        }

        return found
            .Select(entry => (Directory: entry.Key, Scope: entry.Value))
            .OrderBy(entry => entry.Directory, StringComparers.FileSystemPath)
            .ToArray();
    }
}