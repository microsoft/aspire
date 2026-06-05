// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREDOCKERFILEBUILDER001

//
// Re-attaches the package manager and install-command annotations using the
// workspace root for lockfile detection. Called from
// JavaScriptWorkspaceExtensions.WithWorkspaceRoot when the user configured the
// workspace AFTER a package manager (or after an auto-attached npm).
//
// The lockfile-dependent verb (npm "ci" vs "install") and lockfile flags
// (--frozen-lockfile / --immutable) are recomputed against the workspace root
// because their value depends on which lockfile is present at the install
// location. User-supplied extras from a previous WithNpm/WithYarn/WithPnpm/
// WithBun call (other than the install verb itself and any well-known lockfile
// flag) are preserved here, so that:
//
//   builder.AddViteApp("web", "./apps/web")
//          .WithNpm("ci", ["--audit"])
//          .WithWorkspaceRoot("..")
//
// keeps "--audit" in the final Dockerfile invocation.

using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.JavaScript.Internal.Workspace;

internal static class WorkspacePackageManagerReconfigurer
{
    // Well-known lockfile-related flags that we may emit ourselves and therefore strip from the
    // user's pre-existing install args before re-merging. We do not strip the inverse forms
    // (--no-frozen-lockfile, --no-immutable); a user that wants to keep them should call
    // WithYarn/WithPnpm/WithBun *after* WithWorkspaceRoot so the install annotation is built
    // fresh against the workspace root.
    private static readonly HashSet<string> s_workspaceLockfileFlags = new(StringComparer.Ordinal)
    {
        "--frozen-lockfile",
        "--immutable",
    };

    /// <summary>
    /// Re-attaches package manager and install command annotations using the
    /// workspace root for lockfile detection.
    /// </summary>
    public static void Reconfigure<TResource>(IResourceBuilder<TResource> builder, string executableName, string workspaceRoot)
        where TResource : JavaScriptAppResource
    {
        var existingArgs = builder.Resource.TryGetLastAnnotation<JavaScriptInstallCommandAnnotation>(out var existingInstall)
            ? existingInstall.Args
            : [];

        switch (executableName)
        {
            case "npm":
                ReconfigureNpm(builder, workspaceRoot, existingArgs);
                break;
            case "yarn":
                ReconfigureYarn(builder, workspaceRoot, existingArgs);
                break;
            case "pnpm":
                ReconfigurePnpm(builder, workspaceRoot, existingArgs);
                break;
            case "bun":
                ReconfigureBun(builder, workspaceRoot, existingArgs);
                break;
        }
    }

    private static void ReconfigureNpm<TResource>(IResourceBuilder<TResource> builder, string workspaceRoot, string[] existingArgs)
        where TResource : JavaScriptAppResource
    {
        var installCommand = File.Exists(Path.Combine(workspaceRoot, "package-lock.json")) &&
                             builder.ApplicationBuilder.ExecutionContext.IsPublishMode
            ? "ci"
            : "install";

        builder
            .WithAnnotation(new JavaScriptPackageManagerAnnotation("npm", runScriptCommand: "run", cacheMount: "/root/.npm")
            {
                PackageFilesPatterns = { new CopyFilePattern("package*.json", "./") },
                WorkspaceCommandFactory = WorkspaceCommandFactories.Npm,
            })
            .WithAnnotation(new JavaScriptInstallCommandAnnotation(MergeNpmInstallArgs(installCommand, existingArgs))
            {
                ProductionInstallArgs = "--omit=dev"
            });
    }

    private static void ReconfigureYarn<TResource>(IResourceBuilder<TResource> builder, string workspaceRoot, string[] existingArgs)
        where TResource : JavaScriptAppResource
    {
        var hasYarnLock = File.Exists(Path.Combine(workspaceRoot, "yarn.lock"));
        var hasYarnrc = File.Exists(Path.Combine(workspaceRoot, ".yarnrc.yml"));
        var hasYarnBerryDir = Directory.Exists(Path.Combine(workspaceRoot, ".yarn"));
        var hasYarnBerry = hasYarnrc || hasYarnBerryDir;

        string[] lockfileFlags = builder.ApplicationBuilder.ExecutionContext.IsPublishMode && hasYarnLock
            ? hasYarnBerry ? ["--immutable"] : ["--frozen-lockfile"]
            : [];

        var cacheMount = hasYarnBerry ? ".yarn/cache" : "/root/.cache/yarn";
        builder
            .WithAnnotation(new JavaScriptPackageManagerAnnotation("yarn", runScriptCommand: "run", cacheMount)
            {
                CommandSeparator = null,
                WorkspaceCommandFactory = WorkspaceCommandFactories.Yarn,
            })
            .WithAnnotation(new JavaScriptInstallCommandAnnotation(MergeInstallArgs(existingArgs, lockfileFlags))
            {
                ProductionInstallArgs = "--production"
            });
    }

    private static void ReconfigurePnpm<TResource>(IResourceBuilder<TResource> builder, string workspaceRoot, string[] existingArgs)
        where TResource : JavaScriptAppResource
    {
        var hasPnpmLock = File.Exists(Path.Combine(workspaceRoot, "pnpm-lock.yaml"));
        string[] lockfileFlags = builder.ApplicationBuilder.ExecutionContext.IsPublishMode && hasPnpmLock
            ? ["--frozen-lockfile"]
            : [];

        builder
            .WithAnnotation(new JavaScriptPackageManagerAnnotation("pnpm", runScriptCommand: "run", cacheMount: "/pnpm/store")
            {
                CommandSeparator = null,
                InitializeDockerBuildStage = stage => stage.Run("corepack enable pnpm"),
                WorkspaceCommandFactory = WorkspaceCommandFactories.Pnpm,
            })
            .WithAnnotation(new JavaScriptInstallCommandAnnotation(MergeInstallArgs(existingArgs, lockfileFlags))
            {
                ProductionInstallArgs = "--prod"
            });
    }

    private static void ReconfigureBun<TResource>(IResourceBuilder<TResource> builder, string workspaceRoot, string[] existingArgs)
        where TResource : JavaScriptAppResource
    {
        var hasBunLock = File.Exists(Path.Combine(workspaceRoot, "bun.lock")) ||
                         File.Exists(Path.Combine(workspaceRoot, "bun.lockb"));
        string[] lockfileFlags = builder.ApplicationBuilder.ExecutionContext.IsPublishMode && hasBunLock
            ? ["--frozen-lockfile"]
            : [];

        builder
            .WithAnnotation(new JavaScriptPackageManagerAnnotation("bun", runScriptCommand: "run", cacheMount: "/root/.bun/install/cache")
            {
                CommandSeparator = null,
                WorkspaceCommandFactory = WorkspaceCommandFactories.Bun,
            })
            .WithAnnotation(new JavaScriptInstallCommandAnnotation(MergeInstallArgs(existingArgs, lockfileFlags))
            {
                ProductionInstallArgs = "--production"
            });
    }

    // npm install args have the lockfile-dependent verb in args[0] ("ci" vs "install"). Replace
    // args[0] with the workspace-derived verb and preserve everything else (e.g. --audit).
    private static string[] MergeNpmInstallArgs(string installCommand, string[] existingArgs)
    {
        if (existingArgs.Length <= 1)
        {
            return [installCommand];
        }
        var merged = new string[existingArgs.Length];
        merged[0] = installCommand;
        Array.Copy(existingArgs, 1, merged, 1, existingArgs.Length - 1);
        return merged;
    }

    // yarn/pnpm/bun install args are always [install, *flags]. Drop any prior well-known
    // lockfile flag so we don't duplicate it, then prepend the workspace-derived flag(s) and
    // re-append the user's remaining extras.
    private static string[] MergeInstallArgs(string[] existingArgs, string[] lockfileFlags)
    {
        var merged = new List<string>(1 + lockfileFlags.Length + Math.Max(0, existingArgs.Length - 1))
        {
            "install",
        };
        merged.AddRange(lockfileFlags);
        for (var i = 1; i < existingArgs.Length; i++)
        {
            var arg = existingArgs[i];
            if (s_workspaceLockfileFlags.Contains(arg))
            {
                continue;
            }
            merged.Add(arg);
        }
        return [.. merged];
    }
}
