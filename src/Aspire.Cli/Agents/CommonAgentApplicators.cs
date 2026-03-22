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
    /// Tries to add an applicator for creating/updating the Aspire skill file at the specified path.
    /// </summary>
    /// <param name="context">The scan context.</param>
    /// <param name="workspaceRoot">The workspace root directory.</param>
    /// <param name="skillRelativePath">The relative path to the skill file from workspace root (e.g., ".github/skills/aspire/SKILL.md").</param>
    /// <param name="description">The description to show in the applicator prompt.</param>
    /// <returns>True if the applicator was added, false if it was already added.</returns>
    public static bool TryAddSkillFileApplicator(
        AgentEnvironmentScanContext context,
        DirectoryInfo workspaceRoot,
        string skillRelativePath,
        string description)
    {
        // Check if we've already added an applicator for this specific skill path
        if (context.HasSkillFileApplicator(skillRelativePath))
        {
            return false;
        }

        var skillFilePath = Path.Combine(workspaceRoot.FullName, skillRelativePath);

        // Mark this skill path as having an applicator (whether file exists or not)
        context.MarkSkillFileApplicatorAdded(skillRelativePath);

        // Check if the skill file already exists
        if (File.Exists(skillFilePath))
        {
            // Read existing content and check if it differs from current content
            // Normalize line endings for comparison to handle cross-platform differences
            var existingContent = File.ReadAllText(skillFilePath);
            var normalizedExisting = NormalizeLineEndings(existingContent);
            var normalizedExpected = NormalizeLineEndings(SkillFileContent);

            if (!string.Equals(normalizedExisting, normalizedExpected, StringComparison.Ordinal))
            {
                // Content differs, offer to update
                context.AddApplicator(new AgentEnvironmentApplicator(
                    $"{description} (update - content has changed)",
                    ct => UpdateSkillFileAsync(skillFilePath, ct),
                    promptGroup: McpInitPromptGroup.SkillFiles,
                    priority: 0));
                return true;
            }

            // File exists and content is the same, nothing to do
            return false;
        }

        // Skill file doesn't exist, add applicator to create it
        context.AddApplicator(new AgentEnvironmentApplicator(
            description,
            ct => CreateSkillFileAsync(skillFilePath, ct),
            promptGroup: McpInitPromptGroup.SkillFiles,
            priority: 0));

        return true;
    }

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
        context.AddSkillBaseDirectory(skillBaseDirectory);

        // Only add the Playwright applicator prompt once across all environments
        if (context.PlaywrightApplicatorAdded)
        {
            return;
        }

        context.PlaywrightApplicatorAdded = true;
        context.AddApplicator(new AgentEnvironmentApplicator(
            "Install Playwright CLI (Recommended for browser automation)",
            ct => installer.InstallAsync(context, ct),
            promptGroup: McpInitPromptGroup.Tools,
            priority: 1));
    }

    /// <summary>
    /// Creates a skill file at the specified path.
    /// </summary>
    private static async Task CreateSkillFileAsync(string skillFilePath, CancellationToken cancellationToken)
    {
        // Ensure the directory exists
        var directory = Path.GetDirectoryName(skillFilePath);
        if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
        }

        // Only create the file if it doesn't already exist
        if (!File.Exists(skillFilePath))
        {
            await File.WriteAllTextAsync(skillFilePath, SkillFileContent, cancellationToken);
        }
    }

    /// <summary>
    /// Updates an existing skill file at the specified path with the latest content.
    /// </summary>
    private static async Task UpdateSkillFileAsync(string skillFilePath, CancellationToken cancellationToken)
    {
        await File.WriteAllTextAsync(skillFilePath, SkillFileContent, cancellationToken);
    }

    /// <summary>
    /// Normalizes line endings to LF for consistent comparison across platforms.
    /// </summary>
    private static string NormalizeLineEndings(string content)
    {
        return content.ReplaceLineEndings("\n");
    }

    /// <summary>
    /// Gets the content for the Aspire skill file.
    /// </summary>
    internal const string SkillFileContent =
        """
        ---
        name: aspire
        description: "Orchestrates Aspire distributed applications using the Aspire CLI for running, debugging, and managing distributed apps. USE FOR: aspire start, aspire stop, start aspire app, aspire describe, list aspire integrations, debug aspire issues, view aspire logs, add aspire resource, aspire dashboard, update aspire apphost. DO NOT USE FOR: non-Aspire .NET apps (use dotnet CLI), container-only deployments (use docker/podman), Azure deployment after local testing (use azure-deploy skill). INVOKES: Aspire CLI commands (aspire start, aspire describe, aspire otel logs, aspire docs search, aspire add), bash. FOR SINGLE OPERATIONS: Use Aspire CLI commands directly for quick resource status or doc lookups."
        ---

        # Aspire Skill

        This repository uses Aspire to orchestrate its distributed application. Resources are defined in the AppHost project (`apphost.cs` or `apphost.ts`).

        ## CLI command reference

        | Task | Command |
        |---|---|
        | Start the app | `aspire start` |
        | Start isolated (worktrees) | `aspire start --isolated` |
        | Restart the app | `aspire start` (stops previous automatically) |
        | Wait for resource healthy | `aspire wait <resource>` |
        | Stop the app | `aspire stop` |
        | List resources | `aspire describe` or `aspire resources` |
        | Run resource command | `aspire resource <resource> <command>` |
        | Start/stop/restart resource | `aspire resource <resource> start|stop|restart` |
        | View console logs | `aspire logs [resource]` |
        | View structured logs | `aspire otel logs [resource]` |
        | View traces | `aspire otel traces [resource]` |
        | Logs for a trace | `aspire otel logs --trace-id <id>` |
        | Add an integration | `aspire add` |
        | List running AppHosts | `aspire ps` |
        | Update AppHost packages | `aspire update` |
        | Search docs | `aspire docs search <query>` |
        | Get doc page | `aspire docs get <slug>` |
        | List doc pages | `aspire docs list` |
        | Environment diagnostics | `aspire doctor` |
        | List resource MCP tools | `aspire mcp tools` |
        | Call resource MCP tool | `aspire mcp call <resource> <tool> --input <json>` |

        Most commands support `--format Json` for machine-readable output. Use `--apphost <path>` to target a specific AppHost.

        ## Key workflows

        ### Running in agent environments

        Use `aspire start` to run the AppHost in the background. When working in a git worktree, use `--isolated` to avoid port conflicts and to prevent sharing user secrets or other local state with other running instances:

        ```bash
        aspire start --isolated
        ```

        Use `aspire wait <resource>` to block until a resource is healthy before interacting with it:

        ```bash
        aspire start --isolated
        aspire wait myapi
        ```

        Relaunching is safe — `aspire start` automatically stops any previous instance. Re-run `aspire start` whenever changes are made to the AppHost project.

        ### Debugging issues

        Before making code changes, inspect the app state:

        1. `aspire describe` — check resource status
        2. `aspire otel logs <resource>` — view structured logs
        3. `aspire logs <resource>` — view console output
        4. `aspire otel traces <resource>` — view distributed traces

        ### Adding integrations

        Use `aspire docs search` to find integration documentation, then `aspire docs get` to read the full guide. Use `aspire add` to add the integration package to the AppHost.

        After adding an integration, restart the app with `aspire start` for the new resource to take effect.

        ### Using resource MCP tools

        Some resources expose MCP tools (e.g. `WithPostgresMcp()` adds SQL query tools). Discover and call them via CLI:

        ```bash
        aspire mcp tools                                              # list available tools
        aspire mcp tools --format Json                                # includes input schemas
        aspire mcp call <resource> <tool> --input '{"key":"value"}'   # invoke a tool
        ```

        ## Important rules

        - **Always start the app first** (`aspire start`) before making changes to verify the starting state.
        - **To restart, just run `aspire start` again** — it automatically stops the previous instance. NEVER use `aspire stop` then `aspire run`. NEVER use `aspire run` at all.
        - Use `--isolated` when working in a worktree.
        - **Avoid persistent containers** early in development to prevent state management issues.
        - **Never install the Aspire workload** — it is obsolete.
        - Prefer `aspire.dev` and `learn.microsoft.com/microsoft/aspire` for official documentation.

        ## Playwright CLI

        If configured, use Playwright CLI for functional testing of resources. Get endpoints via `aspire describe`. Run `playwright-cli --help` for available commands.
        """;
}
