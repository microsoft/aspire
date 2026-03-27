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
        context.AddSkillBaseDirectory(skillBaseDirectory);

        // Only add the Playwright applicator prompt once across all environments
        if (context.PlaywrightApplicatorAdded)
        {
            return;
        }

        context.PlaywrightApplicatorAdded = true;
        context.AddApplicator(new AgentEnvironmentApplicator(
            "Install Playwright CLI (Recommended for browser automation)",
            ct => installer.InstallAsync(context.RepositoryRoot.FullName, context.SkillBaseDirectories.ToHashSet(StringComparer.OrdinalIgnoreCase), ct),
            promptGroup: McpInitPromptGroup.Tools,
            priority: 1));
    }

    /// <summary>
    /// Gets the content for the Aspire skill file.
    /// </summary>
    internal const string SkillFileContent =
        """
        ---
        name: aspire
        description: "Orchestrates Aspire distributed applications using the Aspire CLI for running, debugging, deploying, and managing distributed apps. USE FOR: aspire start, aspire stop, start aspire app, aspire describe, list aspire integrations, debug aspire issues, view aspire logs, add aspire resource, aspire dashboard, update aspire apphost, aspire publish, aspire deploy, aspire secrets, aspire config. DO NOT USE FOR: non-Aspire .NET apps (use dotnet CLI), container-only deployments (use docker/podman). INVOKES: Aspire CLI commands (aspire start, aspire describe, aspire otel logs, aspire docs search, aspire add, aspire publish, aspire deploy), bash. FOR SINGLE OPERATIONS: Use Aspire CLI commands directly for quick resource status or doc lookups."
        ---

        # Aspire Skill

        This repository uses Aspire to orchestrate its distributed application. Resources are defined in the AppHost project (`apphost.cs` or `apphost.ts`).

        ## TypeScript AppHost (.modules folder)

        When using a TypeScript AppHost (`apphost.ts`), the `.modules/` folder at the project root contains auto-generated TypeScript modules that expose the Aspire APIs available to `apphost.ts`. Key files include `aspire.ts` (the main API surface with `createBuilder` and resource methods), `base.ts`, and `transport.ts`.

        - **Do not edit files in `.modules/` directly** — they are regenerated automatically.
        - To add new APIs (e.g., a new integration), run `aspire add <package>`. This updates the NuGet references and regenerates the `.modules/` folder with the new APIs.
        - After running `aspire add`, check the updated `.modules/aspire.ts` to discover the newly available APIs.
        - The `tsconfig.json` includes `.modules/**/*.ts` in its compilation scope.

        ## CLI command reference

        | Task | Command |
        |---|---|
        | Create a new project | `aspire new` |
        | Initialize Aspire in existing project | `aspire init` |
        | Start the app (background) | `aspire start` |
        | Start isolated (worktrees) | `aspire start --isolated` |
        | Restart the app | `aspire start` (stops previous automatically) |
        | Run the app (foreground) | `aspire run` |
        | Wait for resource healthy | `aspire wait <resource>` |
        | Wait with custom status/timeout | `aspire wait <resource> --status up --timeout 60` |
        | Stop the app | `aspire stop` |
        | List resources | `aspire describe` or `aspire resources` |
        | Run resource command | `aspire resource <resource> <command>` |
        | Start/stop/restart resource | `aspire resource <resource> start\|stop\|restart` |
        | View console logs | `aspire logs [resource]` |
        | View structured logs | `aspire otel logs [resource]` |
        | View traces | `aspire otel traces [resource]` |
        | View spans | `aspire otel spans [resource]` |
        | Logs for a trace | `aspire otel logs --trace-id <id>` |
        | Export telemetry and resource data to zip | `aspire export [resource]` |
        | Add an integration | `aspire add` |
        | List running AppHosts | `aspire ps` |
        | Update AppHost packages | `aspire update` |
        | Set a user secret | `aspire secret set <key> <value>` |
        | Get a user secret | `aspire secret get <key>` |
        | List user secrets | `aspire secret list` |
        | Set CLI config | `aspire config set <key> <value>` |
        | Get CLI config | `aspire config get <key>` |
        | List CLI config | `aspire config list` |
        | Search docs | `aspire docs search <query>` |
        | Get doc page | `aspire docs get <slug>` |
        | List doc pages | `aspire docs list` |
        | Publish deployment artifacts | `aspire publish` |
        | Deploy to targets | `aspire deploy` |
        | Run a pipeline step | `aspire do <step>` |
        | Environment diagnostics | `aspire doctor` |
        | List resource MCP tools | `aspire mcp tools` |
        | Call resource MCP tool | `aspire mcp call <resource> <tool> --input <json>` |
        | Configure agent integrations | `aspire agent init` |

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
        5. `aspire export` — export telemetry and resource data to a zip for deeper analysis

        ### Adding integrations

        Use `aspire docs search` to find integration documentation, then `aspire docs get` to read the full guide. Use `aspire add` to add the integration package to the AppHost.

        After adding an integration, restart the app with `aspire start` for the new resource to take effect.

        ### Managing secrets

        Use `aspire secret` to manage AppHost user secrets for connection strings, passwords, and API keys:

        ```bash
        aspire secret set Parameters:postgres-password MySecretValue
        aspire secret list
        ```

        ### Publishing and deploying

        Generate deployment artifacts (Bicep, Docker Compose, etc.):

        ```bash
        aspire publish
        ```

        Deploy to configured targets:

        ```bash
        aspire deploy
        aspire deploy --clear-cache  # reset cached deployment state
        ```

        ### Using resource MCP tools

        Some resources expose MCP tools (e.g. `WithPostgresMcp()` adds SQL query tools). Discover and call them via CLI:

        ```bash
        aspire mcp tools                                              # list available tools
        aspire mcp tools --format Json                                # includes input schemas
        aspire mcp call <resource> <tool> --input '{"key":"value"}'   # invoke a tool
        ```

        ## Important rules

        - **Always start the app first** (`aspire start`) before making changes to verify the starting state.
        - **To restart, just run `aspire start` again** — it automatically stops the previous instance. NEVER use `aspire stop` then `aspire run`. NEVER use `aspire run` at all — it blocks the terminal.
        - Use `--isolated` when working in a worktree.
        - **Avoid persistent containers** early in development to prevent state management issues.
        - **Never install the Aspire workload** — it is obsolete.
        - Prefer `aspire.dev` and `learn.microsoft.com/microsoft/aspire` for official documentation.

        ## Playwright CLI

        If configured, use Playwright CLI for functional testing of resources. Get endpoints via `aspire describe`. Run `playwright-cli --help` for available commands.
        """;

    /// <summary>
    /// Gets the content for the dotnet-inspect skill file.
    /// See: <a href="https://github.com/richlander/dotnet-inspect/blob/main/skills/dotnet-inspect/SKILL.md">dotnet-inspect skill file</a>.
    /// </summary>
    internal const string DotnetInspectSkillFileContent =
        """
        ---
        name: dotnet-inspect
        description: "Query .NET APIs across NuGet packages, platform libraries, and local files. Search for types, list API surfaces, compare and diff versions, find extension methods and implementors. Use whenever you need to answer questions about .NET library contents."
        ---

        # dotnet-inspect

        Query .NET library APIs — the same commands work across NuGet packages, platform libraries (System.*, Microsoft.AspNetCore.*), and local .dll/.nupkg files.

        ## Quick Decision Tree

        - **Code broken?** → `diff --package Foo@old..new` first, then `member --oneline`
        - **Need API surface?** → `member Type --package Foo --oneline` (token-efficient)
        - **Need signatures?** → `member Type --package Foo -m Method` (default shows full signatures + docs)
        - **Need source/IL?** → `member Type --package Foo -m Method -v:d` (adds Source, Lowered C#, IL)
        - **Need constructors?** → `member 'Type<T>' --package Foo -m .ctor` (use `<T>` not `<>`)
        - **Need all overloads?** → `member Type --package Foo --select` (shows `Name:N` indices)

        ## When to Use This Skill

        - **"What types are in this package?"** — `type` discovers types (terse), `find` searches by pattern
        - **"What's the API surface?"** — `type` for discovery, `member` for detailed inspection (docs on)
        - **"What changed between versions?"** — `diff` classifies breaking/additive changes
        - **"This code uses an old API — fix it"** — `diff` the old..new version, then `member --oneline` to see the new API
        - **"What extends this type?"** — `extensions` finds extension methods/properties
        - **"What implements this interface?"** — `implements` finds concrete types
        - **"What does this type depend on?"** — `depends` walks the type hierarchy upward
        - **"What version/metadata does this have?"** — `package` and `library` inspect metadata
        - **"Show me something cool"** — `demo` runs curated showcase queries

        ## Key Patterns

        Use `--oneline` as the default for scanning — it works on `type`, `member`, `find`, `diff`, and `implements`:

        ```bash
        dnx dotnet-inspect -y -- member JsonSerializer --package System.Text.Json --oneline  # scan members
        dnx dotnet-inspect -y -- type --package System.Text.Json --oneline                   # scan types
        dnx dotnet-inspect -y -- diff --package System.CommandLine@2.0.0-beta4.22272.1..2.0.3 --oneline  # triage changes
        ```

        Use `--shape` to understand a type's hierarchy and surface at a glance:

        ```bash
        dnx dotnet-inspect -y -- type 'HashSet<T>' --platform System.Collections --shape
        ```

        Use `diff` first when fixing broken code — `--oneline` for triage, then full detail on specific types:

        ```bash
        dnx dotnet-inspect -y -- diff --package System.CommandLine@2.0.0-beta4.22272.1..2.0.3 --oneline  # what changed?
        dnx dotnet-inspect -y -- diff -t Command --package System.CommandLine@2.0.0-beta4.22272.1..2.0.3  # detail on Command
        dnx dotnet-inspect -y -- member Command --package System.CommandLine@2.0.3 --oneline              # new API surface
        ```

        ## Search Scope

        Search commands (`find`, `extensions`, `implements`, `depends`) use scope flags:

        - **(no flags)** — platform frameworks + Microsoft.Extensions.AI
        - **`--platform`** — all platform frameworks
        - **`--extensions`** — curated Microsoft.Extensions.* packages
        - **`--aspnetcore`** — curated Microsoft.AspNetCore.* packages
        - **`--package Foo`** — specific NuGet package (combinable with scope flags)

        `type`, `member`, `library`, `diff` accept `--platform <name>` as a string for a specific platform library.

        ## Command Reference

        | Command | Purpose |
        | ------- | ------- |
        | `type` | **Discover types** — terse output, no docs, use `--shape` for hierarchy |
        | `member` | **Inspect members** — docs on by default, supports dotted syntax (`-m Type.Member`) |
        | `find` | Search for types by glob pattern across any scope |
        | `diff` | Compare API surfaces between versions — breaking/additive classification |
        | `extensions` | Find extension methods/properties for a type |
        | `implements` | Find types implementing an interface or extending a base class |
        | `depends` | Walk the type dependency hierarchy upward (interfaces, base classes) |
        | `package` | Package metadata, files, versions, dependencies, `search` for NuGet discovery |
        | `library` | Library metadata, symbols, references, dependencies |
        | `demo` | Run curated showcase queries — list, invoke, or feeling-lucky |

        ## Output Limiting

        **Do not pipe output through `head`, `tail`, or `Select-Object`.** The tool has built-in line limiting that preserves headers and formatting:

        ```bash
        dnx dotnet-inspect -y -- member JsonSerializer --package System.Text.Json --oneline -10  # first 10 lines
        dnx dotnet-inspect -y -- find "*Logger*" -n 5                                            # first 5 lines
        dnx dotnet-inspect -y -- member JsonSerializer --package System.Text.Json -v:q -s Methods  # select specific section
        ```

        - **`-n N` or `-N`** — line limit, like `head`. Keeps headers, truncates cleanly.
        - **`-s Section`** — show only a specific section (glob-capable). Use `-s` alone to list available sections.
        - **`-v:q`** — quiet verbosity for compact summary output.

        ## Key Syntax

        - **Generic types** need quotes: `'Option<T>'`, `'IEnumerable<T>'`
        - **Use `<T>` not `<>`** for generic types — `"Option<>"` resolves to the abstract base, `'Option<T>'` resolves to the concrete generic with constructors
        - **`type` uses `-t`** for type filtering, **`member` uses `-m`** for member filtering (not `--filter`)
        - **Dotted syntax** for `member`: `-m JsonSerializer.Deserialize`
        - **Diff ranges** use `..`: `--package System.Text.Json@9.0.0..10.0.0`
        - **Signatures** include `params` and default values from metadata
        - **Derived types** only show their own members — query the base type too (e.g., `RootCommand` inherits `Add()` and `SetAction()` from `Command`)

        ## Installation

        Use `dnx` (like `npx`). Always use `-y` and `--` to prevent interactive prompts:

        ```bash
        dnx dotnet-inspect -y -- <command>
        ```

        ## Full Documentation

        For comprehensive syntax, edge cases, and the flag compatibility matrix:

        ```bash
        dnx dotnet-inspect -y -- llmstxt
        ```
        """;
}
