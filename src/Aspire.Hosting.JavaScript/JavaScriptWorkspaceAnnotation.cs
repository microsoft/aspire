// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.JavaScript;

/// <summary>
/// Indicates that a JavaScript application is a member of a JavaScript workspace (yarn / npm / pnpm / bun
/// monorepo) and carries the metadata required to generate a workspace-aware Dockerfile.
/// </summary>
/// <remarks>
/// This annotation is attached by <c>WithWorkspaceRoot</c>. When present, Dockerfile generation switches
/// the build context to <see cref="RootPath"/>, copies workspace-level manifests, runs install at the root,
/// and uses the package manager's native workspace filter to build and start <see cref="AppName"/>.
/// </remarks>
public sealed class JavaScriptWorkspaceAnnotation : IResourceAnnotation
{
    /// <summary>
    /// Initializes a new instance of the <see cref="JavaScriptWorkspaceAnnotation"/> class.
    /// </summary>
    /// <param name="rootPath">The absolute path to the workspace root directory.</param>
    /// <param name="appName">The package name of the application as declared in <c>&lt;appDir&gt;/package.json</c>.</param>
    /// <param name="appRelativePath">The forward-slash relative path from <paramref name="rootPath"/> to the application directory.</param>
    /// <param name="workspaceDirs">Resolved workspace member directories (forward-slash relative paths under <paramref name="rootPath"/>).</param>
    /// <param name="rootFiles">Workspace-root files to copy into the manifest layer (forward-slash relative paths). Includes the lockfile and PM-specific config files when present.</param>
    /// <param name="rootDirs">Workspace-root directories to copy into the manifest layer (forward-slash relative paths). For example, Yarn Berry's <c>.yarn</c> directory.</param>
    public JavaScriptWorkspaceAnnotation(
        string rootPath,
        string appName,
        string appRelativePath,
        IReadOnlyList<string> workspaceDirs,
        IReadOnlyList<string> rootFiles,
        IReadOnlyList<string> rootDirs)
    {
        ArgumentException.ThrowIfNullOrEmpty(rootPath);
        ArgumentException.ThrowIfNullOrEmpty(appName);
        ArgumentException.ThrowIfNullOrEmpty(appRelativePath);
        ArgumentNullException.ThrowIfNull(workspaceDirs);
        ArgumentNullException.ThrowIfNull(rootFiles);
        ArgumentNullException.ThrowIfNull(rootDirs);

        RootPath = rootPath;
        AppName = appName;
        AppRelativePath = appRelativePath;
        WorkspaceDirs = workspaceDirs;
        RootFiles = rootFiles;
        RootDirs = rootDirs;
    }

    /// <summary>
    /// Gets the absolute path to the workspace root directory. This becomes the Docker build context.
    /// </summary>
    public string RootPath { get; }

    /// <summary>
    /// Gets the package name of this application as declared in its <c>package.json</c>. Used to drive
    /// the package manager's native workspace filter (for example <c>pnpm --filter &lt;name&gt;</c>).
    /// </summary>
    public string AppName { get; }

    /// <summary>
    /// Gets the forward-slash relative path from <see cref="RootPath"/> to this application's directory.
    /// Used to set the runtime stage <c>WORKDIR</c> so that entry points and build outputs resolve correctly.
    /// </summary>
    public string AppRelativePath { get; }

    /// <summary>
    /// Gets the resolved workspace member directories (forward-slash relative paths under
    /// <see cref="RootPath"/>). Each entry's <c>package.json</c> is copied into the Dockerfile manifest
    /// layer for cache-friendly dependency installation.
    /// </summary>
    public IReadOnlyList<string> WorkspaceDirs { get; }

    /// <summary>
    /// Gets the workspace-root files to copy into the Dockerfile manifest layer (forward-slash relative
    /// paths). Includes the lockfile and any package-manager-specific config files (for example
    /// <c>pnpm-workspace.yaml</c>, <c>.yarnrc.yml</c>, <c>.npmrc</c>, <c>bunfig.toml</c>) when present.
    /// </summary>
    public IReadOnlyList<string> RootFiles { get; }

    /// <summary>
    /// Gets the workspace-root directories to copy into the Dockerfile manifest layer (forward-slash
    /// relative paths). For example, Yarn Berry's <c>.yarn</c> directory.
    /// </summary>
    public IReadOnlyList<string> RootDirs { get; }
}
