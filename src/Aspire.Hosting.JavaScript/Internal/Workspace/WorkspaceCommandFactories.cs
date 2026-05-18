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
//   pnpm : pnpm --filter <name>... run <script> [args...]
//          https://pnpm.io/filtering — the trailing "..." selects <name> AND
//          its workspace dependencies in topological order, so building a
//          target also builds the workspace libraries it depends on.
//   bun  : bun --filter <name> run <script> [args...]
//          https://bun.com/docs/cli/run#run-scripts-in-workspaces
//
// These delegates are stored on JavaScriptPackageManagerAnnotation so the
// Dockerfile generator and the run-mode wiring don't have to switch on
// executable name in multiple places.

namespace Aspire.Hosting.JavaScript.Internal.Workspace;

/// <summary>
/// Workspace-aware run-script command factories for npm/yarn/pnpm/bun. Each
/// factory is the same shape: <c>(workspaceName, scriptName, scriptArgs) =&gt;
/// argv</c>.
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
            // pnpm filter syntax: "<name>..." (suffix) selects <name> AND its workspace dependencies
            // in topological order. When the package has no workspace deps, this is equivalent to
            // "--filter <name>". This makes monorepo builds correct by default — a target's workspace
            // libraries are built before the target itself. See https://pnpm.io/filtering.
            var argv = new List<string> { "pnpm", "--filter", $"{workspaceName}...", "run", scriptName };
            argv.AddRange(scriptArgs);
            return argv;
        };

    public static readonly Func<string, string, IReadOnlyList<string>, IReadOnlyList<string>> Bun =
        static (workspaceName, scriptName, scriptArgs) =>
        {
            var argv = new List<string> { "bun", "--filter", workspaceName, "run", scriptName };
            argv.AddRange(scriptArgs);
            return argv;
        };
}
