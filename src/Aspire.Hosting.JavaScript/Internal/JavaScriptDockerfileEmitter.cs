// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREDOCKERFILEBUILDER001

//
// Helpers that translate JavaScriptPackageManagerAnnotation +
// JavaScriptInstallCommandAnnotation + (optionally) JavaScriptWorkspaceAnnotation
// into the actual lines emitted by the Dockerfile builder. These are kept
// separate from the public extension class so the builder callbacks (which
// already do a lot of resource-shape branching) stay focused on shape and
// delegate the "what gets written" decisions to single-purpose helpers.

using Aspire.Hosting.ApplicationModel.Docker;

namespace Aspire.Hosting.JavaScript.Internal;

internal static class JavaScriptDockerfileEmitter
{
    /// <summary>
    /// Emits a <c>RUN &lt;pm&gt; install [args...]</c> line, using a BuildKit cache
    /// mount when the package manager declares one (so package downloads are
    /// reused across builds).
    /// </summary>
    public static void EmitInstall(
        DockerfileStage stage,
        JavaScriptPackageManagerAnnotation packageManager,
        JavaScriptInstallCommandAnnotation installCommand)
    {
        var installCmd = $"{packageManager.ExecutableName} {string.Join(' ', installCommand.Args)}";
        if (!string.IsNullOrEmpty(packageManager.CacheMount))
        {
            stage.Run($"--mount=type=cache,target={packageManager.CacheMount} {installCmd}");
        }
        else
        {
            stage.Run(installCmd);
        }
    }

    /// <summary>
    /// Emits the workspace manifest layer: workspace-root files (lockfile,
    /// pnpm-workspace.yaml, .yarnrc.yml, etc.), workspace-root directories
    /// (Yarn Berry's <c>.yarn</c>), and every declared workspace member's
    /// <c>package.json</c>. Copying member manifests ahead of source means a
    /// single file change in app code does not bust the install layer cache.
    /// </summary>
    public static void EmitWorkspaceManifestLayer(DockerfileStage stage, JavaScriptWorkspaceAnnotation workspace)
    {
        foreach (var rootFile in workspace.RootFiles)
        {
            stage.Copy(rootFile, "./" + rootFile);
        }

        foreach (var rootDir in workspace.RootDirs)
        {
            stage.Copy(rootDir, "./" + rootDir);
        }

        foreach (var dir in workspace.WorkspaceDirs)
        {
            stage.Copy($"{dir}/package.json", $"./{dir}/package.json");
        }
    }

    /// <summary>
    /// Builds the argv used to invoke a script via the configured package
    /// manager. In workspace mode this routes through the package manager's
    /// native workspace filter (npm <c>--workspace=&lt;name&gt;</c>, yarn
    /// <c>workspace</c>, pnpm/bun <c>--filter</c>) so build/run scripts execute
    /// in the right member directory; otherwise it falls back to the plain
    /// <c>&lt;pm&gt; [run] &lt;script&gt; [args...]</c> shape.
    /// </summary>
    public static IReadOnlyList<string> BuildPackageManagerCommand(
        JavaScriptPackageManagerAnnotation packageManager,
        string scriptName,
        IReadOnlyList<string> scriptArgs,
        JavaScriptWorkspaceAnnotation? workspace)
    {
        if (workspace is not null && packageManager.WorkspaceCommandFactory is { } factory)
        {
            return factory(workspace.AppName, scriptName, scriptArgs);
        }

        var commandArgs = new List<string> { packageManager.ExecutableName };
        if (!string.IsNullOrEmpty(packageManager.ScriptCommand))
        {
            commandArgs.Add(packageManager.ScriptCommand);
        }
        commandArgs.Add(scriptName);
        commandArgs.AddRange(scriptArgs);
        return commandArgs;
    }
}
