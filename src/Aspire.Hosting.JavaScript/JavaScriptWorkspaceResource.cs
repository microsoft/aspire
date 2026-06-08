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
        => _workspaceInfo ??= WorkspaceMemberDiscovery.Discover(WorkingDirectory, packageManagerExecutable);

    /// <summary>
    /// Builds the complete command line (argv) that runs a package script for a specific workspace
    /// member through this package manager's native workspace filter.
    /// </summary>
    /// <remarks>
    /// The returned argv starts with the package-manager executable (e.g. <c>["pnpm", "--filter",
    /// "web...", "run", "build"]</c>). The resource owns the entire argv — not just a prefix — because
    /// the workspace selector is not uniformly placed: npm appends <c>--workspace=&lt;name&gt;</c> after
    /// the script, while yarn/pnpm/bun place their selector before <c>run</c>. pnpm additionally uses
    /// the topological <c>"&lt;name&gt;..."</c> selector so a member's workspace dependencies build first.
    /// </remarks>
    /// <param name="workspaceProjectName">The name of the member (package.json name) within the workspace.</param>
    /// <param name="scriptName">The package.json script to run (for example <c>dev</c> or <c>build</c>).</param>
    /// <param name="scriptArgs">Additional arguments passed to the script.</param>
    /// <returns>The complete argv, beginning with the package-manager executable.</returns>
    public abstract IReadOnlyList<string> GetRunScriptCommand(string workspaceProjectName, string scriptName, IReadOnlyList<string> scriptArgs);
}
