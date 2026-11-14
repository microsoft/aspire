// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// Classifies the contents of a JS workspace root directory: which lockfile is
// present, which optional config files exist, which directories must be
// included in the Dockerfile manifest layer, and whether the root has a
// package.json. The output drives Dockerfile generation; callers turn this
// into validation errors and COPY directives.
//
// Lockfile precedence mirrors what each package manager looks at:
// - npm  : package-lock.json, npm-shrinkwrap.json
// - yarn : yarn.lock
// - pnpm : pnpm-lock.yaml
// - bun  : bun.lock (textual), bun.lockb (binary)
//
// Optional root config files that we copy into the manifest layer when present:
// - pnpm-workspace.yaml — defines pnpm workspace members
// - .yarnrc.yml — Yarn Berry config
// - .yarnrc — Yarn classic config
// - .npmrc — npm/yarn classic config (registry, auth, etc.)
// - bunfig.toml — bun config
//
// Optional root directories that we copy into the manifest layer when present:
// - .yarn — Yarn Berry zero-installs cache, releases, etc.

namespace Aspire.Hosting.JavaScript.Internal.Workspace;

internal sealed record WorkspaceRootManifests(IReadOnlyList<string> RootFiles, IReadOnlyList<string> RootDirs, bool HasPackageJson, bool HasLockfile);

internal static class WorkspaceManifestDiscovery
{
    private static readonly string[] s_lockfileNames = [
        "package-lock.json",
        "npm-shrinkwrap.json",
        "yarn.lock",
        "pnpm-lock.yaml",
        "bun.lock",
        "bun.lockb",
    ];

    private static readonly string[] s_optionalRootManifestFiles = [
        "pnpm-workspace.yaml",
        ".yarnrc.yml",
        ".yarnrc",
        ".npmrc",
        "bunfig.toml",
    ];

    private static readonly string[] s_optionalRootDirs = [
        ".yarn",
    ];

    /// <summary>
    /// The set of recognized lockfile names, ordered by package-manager precedence.
    /// </summary>
    public static IReadOnlyList<string> RecognizedLockfileNames => s_lockfileNames;

    /// <summary>
    /// Inspects <paramref name="rootPath"/> and returns the set of files and directories
    /// that should be copied into the Dockerfile manifest layer.
    /// </summary>
    public static WorkspaceRootManifests Discover(string rootPath)
    {
        ArgumentNullException.ThrowIfNull(rootPath);

        var rootFiles = new List<string>();
        var hasPackageJson = File.Exists(Path.Combine(rootPath, "package.json"));
        if (hasPackageJson)
        {
            rootFiles.Add("package.json");
        }

        var hasLockfile = false;
        foreach (var lockName in s_lockfileNames)
        {
            if (File.Exists(Path.Combine(rootPath, lockName)))
            {
                rootFiles.Add(lockName);
                hasLockfile = true;
            }
        }

        foreach (var optional in s_optionalRootManifestFiles)
        {
            if (File.Exists(Path.Combine(rootPath, optional)))
            {
                rootFiles.Add(optional);
            }
        }

        var rootDirs = new List<string>();
        foreach (var dir in s_optionalRootDirs)
        {
            if (Directory.Exists(Path.Combine(rootPath, dir)))
            {
                rootDirs.Add(dir);
            }
        }

        return new WorkspaceRootManifests(rootFiles, rootDirs, hasPackageJson, hasLockfile);
    }
}
