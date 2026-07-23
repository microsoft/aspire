// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// Per-package-manager factories that turn an (workspace member name, script,
// extra args) tuple into the argv that runs the script *via the package
// manager's native workspace filter*. These run at Dockerfile build time and
// at run time, so the shape mirrors what each PM expects on the CLI:
//
//   npm  : npm run <script> --workspace=<name> [-- <args...>]
//          https://docs.npmjs.com/cli/v10/using-npm/workspaces
//   yarn : yarn workspace <name> run <script> [args...]
//          https://yarnpkg.com/cli/workspace
//   pnpm : pnpm --filter <name> run <script> [args...]
//          https://pnpm.io/filtering
//   bun  : bun --filter=<name> run <script> [args...]
//          https://bun.com/docs/cli/run#run-scripts-in-workspaces
//
// The per-workspace-resource GetRunScriptCommand overrides delegate to these
// factories so the Dockerfile generator and the run-mode wiring don't have to
// switch on executable name in multiple places.

namespace Aspire.Hosting.JavaScript.Internal.Workspace;

/// <summary>
/// Workspace-aware run-script command factories for npm/yarn/pnpm/bun. Each
/// factory is the same shape: <c>(workspaceName, scriptName, scriptArgs) =&gt;
/// argv</c> and always scopes the script to the single target member.
/// <see cref="PnpmBuildDependencies"/> is the separate publish-time step that
/// builds a member's workspace dependencies first.
/// </summary>
internal static class WorkspaceCommandFactories
{
    public static readonly Func<string, string, IReadOnlyList<string>, IReadOnlyList<string>> Npm =
        static (workspaceName, scriptName, scriptArgs) =>
        {
            var argv = new List<string> { "npm", "run", scriptName, $"--workspace={workspaceName}" };
            if (scriptArgs.Count > 0)
            {
                argv.Add("--");
                argv.AddRange(scriptArgs);
            }
            return argv;
        };

    public static readonly Func<string, string, IReadOnlyList<string>, IReadOnlyList<string>> Yarn =
        static (workspaceName, scriptName, scriptArgs) =>
        {
            var argv = new List<string> { "yarn", "workspace", workspaceName, "run", scriptName };
            argv.AddRange(scriptArgs);
            return argv;
        };

    public static readonly Func<string, string, IReadOnlyList<string>, IReadOnlyList<string>> Pnpm =
        static (workspaceName, scriptName, scriptArgs) =>
        {
            // Plain "<name>" filter only — never the topological "<name>..." suffix here, because
            // pnpm would run the script in every workspace dependency and forward scriptArgs to all
            // of them. See the file header and PnpmBuildDependencies for the dependency-build step.
            var argv = new List<string> { "pnpm", "--filter", workspaceName, "run", scriptName };
            argv.AddRange(scriptArgs);
            return argv;
        };

    /// <summary>
    /// Builds the publish-time command that builds a member's workspace dependencies before the
    /// member itself: <c>pnpm --filter "&lt;name&gt;^..." run --if-present &lt;script&gt;</c>. The
    /// <c>"^..."</c> suffix selects the dependencies in topological order while EXCLUDING the member,
    /// and the command deliberately carries no user args so dependency scripts never receive args
    /// meant for the member's own build. A member with no workspace dependencies yields a no-op
    /// ("No projects matched the filters", exit 0). See https://pnpm.io/filtering#--filter-package_name_1.
    /// </summary>
    /// <remarks>
    /// <c>--if-present</c> is required: not every workspace package defines a build script, and when
    /// NONE of the selected dependencies has one, pnpm otherwise fails the recursive run with
    /// <c>ERR_PNPM_RECURSIVE_RUN_NO_SCRIPT</c> ("None of the selected packages has a ... script",
    /// observed on pnpm 11.3). With the flag, packages missing the script are skipped and the build
    /// continues. https://pnpm.io/cli/run#--if-present
    /// </remarks>
    public static readonly Func<string, string, IReadOnlyList<string>> PnpmBuildDependencies =
        static (workspaceName, scriptName) =>
            ["pnpm", "--filter", $"{workspaceName}^...", "run", "--if-present", scriptName];

    /// <summary>
    /// Builds the publish-time command that copies a single workspace member into a self-contained,
    /// production-only directory: <c>pnpm --filter &lt;name&gt; deploy --prod &lt;target&gt;</c>. The target
    /// receives the member's files plus a real <c>node_modules</c> with its dependencies — workspace
    /// dependencies are injected (copied in), not symlinked outside the directory — so the runtime image
    /// can copy just the target directory instead of the whole workspace. The member and its
    /// workspace dependencies must already be built (deploy copies files, it does not run build scripts).
    /// </summary>
    /// <remarks>
    /// pnpm 10's deploy uses the injected-dependencies implementation, which requires
    /// <c>injectWorkspacePackages: true</c> in <c>pnpm-workspace.yaml</c>; the workspace configuration
    /// validator enforces that. See https://pnpm.io/cli/deploy.
    /// </remarks>
    public static readonly Func<string, string, IReadOnlyList<string>> PnpmDeploy =
        static (workspaceName, targetDirectory) =>
            ["pnpm", "--filter", workspaceName, "deploy", "--prod", targetDirectory];

    public static readonly Func<string, string, IReadOnlyList<string>, IReadOnlyList<string>> Bun =
        static (workspaceName, scriptName, scriptArgs) =>
        {
            // Use the attached form "--filter=<name>" rather than the space-separated "--filter <name>".
            // With the space-separated form, bun (observed on 1.3.14) fails to associate the value with
            // the flag once the "run" subcommand follows and reports "error: No packages matched the
            // filter", even though `bun pm ls` lists the member. The "--filter=<name>" form parses
            // correctly in both orderings and matches npm's "--workspace=<name>" attached-value style.
            var argv = new List<string> { "bun", $"--filter={workspaceName}", "run", scriptName };
            argv.AddRange(scriptArgs);
            return argv;
        };
}
