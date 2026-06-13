// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.JavaScript.Internal.Workspace;

namespace Aspire.Hosting.JavaScript;

/// <summary>
/// A resource that represents a JavaScript workspace.
/// </summary>
/// <param name="name">The name of the resource.</param>
/// <param name="workingDirectory">The working directory of the workspace.</param>
public abstract class JavaScriptWorkspaceResource(string name, string workingDirectory)
    : Resource(name)
{
    private WorkspaceInfo? _workspaceInfo;

    /// <summary>
    /// Gets the working directory of the workspace.
    /// </summary>
    public string WorkingDirectory { get; } = workingDirectory;

    /// <summary>
    /// Discovers the workspace members once and caches the result so both the cache-stable
    /// install layer (member package.json COPYs) and the configuration validator read the same
    /// instance instead of re-walking the filesystem. The first call performs the discovery; the
    /// <paramref name="packageManagerExecutable"/> argument selects where the declaration is read
    /// from (pnpm-workspace.yaml for pnpm, root package.json "workspaces" otherwise) and is fixed
    /// per workspace type, so the cached value is stable.
    /// </summary>
    /// <param name="packageManagerExecutable">The package-manager executable name ("npm", "yarn", "pnpm", or "bun").</param>
    /// <returns>The discovered <see cref="WorkspaceInfo"/> for this workspace root.</returns>
    internal WorkspaceInfo GetWorkspaceInfo(string packageManagerExecutable)
        // The cache key is implicit: a workspace resource has one fixed package manager (its concrete
        // type), so every call passes the same executable and the first-call result stays valid. If a
        // resource could ever switch package managers, this cache would need to key on the executable.
        => _workspaceInfo ??= WorkspaceMemberDiscovery.Discover(WorkingDirectory, packageManagerExecutable);

    /// <summary>
    /// Builds the complete command line (argv) that runs a package script for a specific workspace
    /// member through this package manager's native workspace filter.
    /// </summary>
    /// <remarks>
    /// The returned argv starts with the package-manager executable (e.g. <c>["pnpm", "--filter",
    /// "web", "run", "build"]</c>). The resource owns the entire argv — not just a prefix — because
    /// the workspace selector is not uniformly placed: npm appends <c>--workspace=&lt;name&gt;</c> after
    /// the script, while yarn/pnpm/bun place their selector before <c>run</c>.
    /// <para>
    /// The command always scopes the script to the single target member. Selecting the member's
    /// dependencies too (e.g. pnpm's topological <c>"&lt;name&gt;..."</c> filter) would run the script in
    /// every dependency AND forward <paramref name="scriptArgs"/> to all of them, breaking dependencies
    /// whose script is unrelated to the target.
    /// Building dependencies first is a separate, args-free step — see <see cref="GetBuildDependenciesCommand"/>.
    /// </para>
    /// </remarks>
    /// <param name="workspaceProjectName">The name of the member (package.json name) within the workspace.</param>
    /// <param name="scriptName">The package.json script to run (for example <c>dev</c> or <c>build</c>).</param>
    /// <param name="scriptArgs">Additional arguments passed to the script.</param>
    /// <returns>The complete argv, beginning with the package-manager executable.</returns>
    public abstract IReadOnlyList<string> GetRunScriptCommand(string workspaceProjectName, string scriptName, IReadOnlyList<string> scriptArgs);

    /// <summary>
    /// Builds the command line (argv) that builds a workspace member's workspace dependencies — but
    /// not the member itself — in topological order, or returns <see langword="null"/> when this
    /// package manager has no native dependencies-first selector.
    /// </summary>
    /// <remarks>
    /// Publish-mode Dockerfile generation emits this command (when non-null) as its own step before
    /// the member's build command, so workspace libraries with build output (e.g. TypeScript compiled
    /// to <c>dist/</c>) exist before the member that imports them builds. It deliberately takes no
    /// script args: the member's build args must never be forwarded to dependency scripts. The base
    /// implementation returns <see langword="null"/> (npm/yarn/bun have no topological run selector);
    /// pnpm overrides it with its <c>"&lt;name&gt;^..."</c> filter. Tools that orchestrate dependency
    /// builds themselves (e.g. nx/turbo integrations layered on top of this resource) can also
    /// override this to plug in their own command.
    /// </remarks>
    /// <param name="workspaceProjectName">The name of the member (package.json name) within the workspace.</param>
    /// <param name="scriptName">The package.json build script to run in the dependencies (typically <c>build</c>).</param>
    /// <returns>The complete argv, or <see langword="null"/> when there is no dependency-build step.</returns>
    public virtual IReadOnlyList<string>? GetBuildDependenciesCommand(string workspaceProjectName, string scriptName)
        => null;
}
