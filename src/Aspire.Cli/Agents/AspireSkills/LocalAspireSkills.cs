// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Agents.ClaudeCode;
using Aspire.Cli.Agents.Copilot;
using Aspire.Cli.Agents.OpenCode;

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

    private static readonly string[] s_universalSkillOwners = [CopilotAgentClient.ClientId, OpenCodeAgentClient.ClientId];

    // The locations older CLIs copied skills into, with the clients each was installed for.
    // Agent-specific roots such as CLAUDE_CONFIG_DIR or OpenCode skills.paths never received copies.
    private static readonly (string Directory, string[] Owners)[] s_projectSkillDirectories =
    [
        (AgentPath.UniversalSkillsDirectory, s_universalSkillOwners),
        (Path.Combine(".claude", "skills"), [ClaudeCodeAgentClient.ClientId]),
        (Path.Combine(".github", "skills"), [CopilotAgentClient.ClientId]),
        (Path.Combine(".opencode", "skill"), [OpenCodeAgentClient.ClientId])
    ];

    /// <summary>
    /// Finds skill directories copied by older CLIs in the workspace and in the user-level standard location,
    /// with the IDs of the clients that read each one.
    /// </summary>
    internal static IReadOnlyList<(string Directory, AgentConfigurationScope Scope, string[] Owners)> Find(
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
            .SelectMany(root => s_projectSkillDirectories.Select(location =>
                (Path: Path.Combine(root.FullName, location.Directory), Scope: AgentConfigurationScope.Project, location.Owners)))
            .Append((Path: Path.Combine(homeDirectory.FullName, AgentPath.UniversalSkillsDirectory), Scope: AgentConfigurationScope.Global, Owners: s_universalSkillOwners));

        // A workspace rooted at the home directory shares the user-level location, so a copy there
        // is reported under both scopes and removal at either scope covers it.
        var found = new List<(string Directory, AgentConfigurationScope Scope, string[] Owners)>();
        foreach (var (skillsDirectory, scope, owners) in candidates)
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
                    found.Add((path, scope, owners));
                }
            }
        }

        return found
            .OrderBy(entry => entry.Directory, StringComparers.FileSystemPath)
            .ThenBy(entry => entry.Scope)
            .ToArray();
    }
}