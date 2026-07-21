// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Agents.Playwright;

namespace Aspire.Cli.Agents;

/// <summary>
/// Provides factory methods for creating common agent applicators that are shared across different agent environments.
/// </summary>
internal static class CommonAgentApplicators
{
    /// <summary>
    /// The name of the Aspire skill.
    /// </summary>
    internal const string AspireSkillName = "aspire";

    /// <summary>
    /// The name of the Aspire deployment skill.
    /// </summary>
    internal const string AspireDeploymentSkillName = "aspire-deployment";

    /// <summary>
    /// The name of the Aspireify skill.
    /// </summary>
    internal const string AspireifySkillName = "aspireify";

    /// <summary>
    /// The name of the dotnet-inspect skill.
    /// </summary>
    internal const string DotnetInspectSkillName = "dotnet-inspect";

    /// <summary>
    /// Adds a single Playwright CLI installation applicator if not already added.
    /// Called by scanners that detect an environment supporting Playwright.
    /// The applicator uses <see cref="PlaywrightCliInstaller"/> to securely install the CLI and generate skill files.
    /// </summary>
    /// <param name="context">The scan context.</param>
    /// <param name="installer">The Playwright CLI installer that handles secure installation.</param>
    /// <param name="skillBaseDirectory">The relative path to the skill base directory for this agent environment (e.g., ".claude/skills", ".github/skills").</param>
    public static void AddPlaywrightCliApplicator(
        AgentEnvironmentScanContext context,
        PlaywrightCliInstaller installer,
        string skillBaseDirectory)
    {
        // Register the skill base directory so skill files can be mirrored to all environments
        context.AddAssetBaseDirectory(AgentAssetKind.Skill, skillBaseDirectory);

        // Only add the Playwright applicator prompt once across all environments
        if (context.PlaywrightApplicatorAdded)
        {
            return;
        }

        context.PlaywrightApplicatorAdded = true;
        context.AddApplicator(new AgentEnvironmentApplicator(
            "Install Playwright CLI (Recommended for browser automation)",
            ct => installer.InstallAsync(context.RepositoryRoot.FullName, context.AgentAssetBaseDirectories(AgentAssetKind.Skill), ct),
            promptGroup: McpInitPromptGroup.Tools,
            priority: 1));
    }

    /// <summary>
    /// Gets the content for the dotnet-inspect skill file.
    /// See: <a href="https://github.com/richlander/dotnet-inspect/blob/main/skills/dotnet-inspect/SKILL.md">dotnet-inspect skill file</a>.
    /// </summary>
    internal const string DotnetInspectSkillFileContent =
        """
        ---
        name: dotnet-inspect
        description: Find evidence for .NET packages, platform libraries, assemblies, APIs, dependencies, SourceLink/source, and API version diffs.
        ---

        # dotnet-inspect

        Use dotnet-inspect for evidence instead of guesses about .NET packages, platform libraries, local assemblies, APIs, dependencies, SourceLink/source, or version-to-version API changes.

        Invoke with `dnx` (like `npx`); always pass `-y` and `--` to avoid interactive prompts:

        ```bash
        dnx dotnet-inspect -y -- <command>
        ```

        This bundled skill is intentionally only a bootstrapper. For non-trivial work, first run the version-matched embedded guide. It always matches the installed tool, so prefer it whenever commands, output modes, section names, or workflow guidance differ:

        ```bash
        dnx dotnet-inspect -y -- skill
        ```

        ## Seed commands

        | Goal | Command |
        | ---- | ------- |
        | Find where an API lives | `find Pattern` |
        | Inspect types or members | `type Type --package Foo`, then `member Type --package Foo` |
        | Compare versions | `diff --package Foo@old..new --breaking` |
        | Inspect package or library signals | `package Foo -S Signals` or `library Foo -S Signals` |
        | Locate source or implementation | `source Type --package Foo` or `member Type Member:1 -S "Decompiled Source"` |
        | Explore relationships | `depends Type`, `extensions Type`, `implements Interface` |

        After `find`, reuse the package, library, or platform scope it reports. Quote generic type names such as `'List<T>'`; use `<T>`, not `<>`.
        """;
}
