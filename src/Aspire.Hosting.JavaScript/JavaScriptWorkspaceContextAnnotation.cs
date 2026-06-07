// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.JavaScript;

/// <summary>
/// An annotation that associates a JavaScript application resource with a workspace,
/// carrying the workspace-specific command prefix for running and building.
/// </summary>
/// <param name="workspace">The workspace resource this app belongs to.</param>
/// <param name="workspaceProjectName">The name of the project within the workspace.</param>
/// <param name="commandPrefix">The package manager command prefix (e.g. <c>["workspace", "name"]</c> for Yarn, <c>["--filter", "name"]</c> for pnpm).</param>
public sealed class JavaScriptWorkspaceContextAnnotation(
    JavaScriptWorkspaceResource workspace,
    string workspaceProjectName,
    string[] commandPrefix) : IResourceAnnotation
{
    /// <summary>
    /// Gets the workspace resource this app belongs to.
    /// </summary>
    public JavaScriptWorkspaceResource Workspace { get; } = workspace;

    /// <summary>
    /// Gets the name of the project within the workspace.
    /// </summary>
    public string WorkspaceProjectName { get; } = workspaceProjectName;

    /// <summary>
    /// Gets the package manager command prefix for workspace-scoped commands.
    /// </summary>
    public string[] CommandPrefix { get; } = commandPrefix;
}
