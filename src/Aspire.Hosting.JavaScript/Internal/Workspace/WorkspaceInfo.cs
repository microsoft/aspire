// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.JavaScript.Internal.Workspace;

/// <summary>
/// A single resolved workspace member: the relative directory under the workspace
/// root (forward-slash) and the package name declared in that member's package.json.
/// </summary>
/// <remarks>
/// Both the directory and the name are load-bearing: the Dockerfile manifest layer
/// copies <c>{RelativeDir}/package.json</c>, while the package-manager workspace
/// filter (<c>pnpm --filter</c>, <c>npm --workspace=</c>, <c>yarn workspace</c>,
/// <c>bun --filter</c>) and member-typo validation select by <see cref="PackageName"/>.
/// </remarks>
internal sealed record WorkspaceMember(string RelativeDir, string PackageName);

internal sealed record WorkspaceInfo(
    IReadOnlyList<string> RootFiles,
    IReadOnlyList<string> RootDirs,
    IReadOnlyList<string> WorkspaceDirs,
    IReadOnlyList<WorkspaceMember> Members,
    string AppName);
