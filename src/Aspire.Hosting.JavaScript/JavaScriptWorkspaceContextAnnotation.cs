// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.JavaScript;

/// <summary>
/// An annotation that associates a JavaScript application resource with a workspace.
/// </summary>
/// <remarks>
/// The full run/build command is resolved lazily via <see cref="JavaScriptWorkspaceResource.GetRunScriptCommand"/>
/// at the point each command is assembled, because run mode and publish (build) mode pass different
/// script names and arguments. The annotation therefore carries only the workspace and the member name.
/// </remarks>
/// <param name="workspace">The workspace resource this app belongs to.</param>
/// <param name="workspaceProjectName">The name of the project within the workspace.</param>
public sealed class JavaScriptWorkspaceContextAnnotation(
    JavaScriptWorkspaceResource workspace,
    string workspaceProjectName) : IResourceAnnotation
{
    /// <summary>
    /// Gets the workspace resource this app belongs to.
    /// </summary>
    public JavaScriptWorkspaceResource Workspace { get; } = workspace;

    /// <summary>
    /// Gets the name of the project within the workspace.
    /// </summary>
    public string WorkspaceProjectName { get; } = workspaceProjectName;
}
