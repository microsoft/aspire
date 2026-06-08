// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.JavaScript.Internal.Workspace;

namespace Aspire.Hosting.JavaScript;

/// <summary>
/// A resource that represents a Yarn workspace.
/// </summary>
/// <param name="name">The name of the resource.</param>
/// <param name="workingDirectory">The working directory of the workspace.</param>
public sealed class YarnWorkspaceResource(string name, string workingDirectory)
    : JavaScriptWorkspaceResource(name, workingDirectory)
{
    /// <inheritdoc />
    public override IReadOnlyList<string> GetRunScriptCommand(string projectName, string scriptName, IReadOnlyList<string> scriptArgs)
        => WorkspaceCommandFactories.Yarn(projectName, scriptName, scriptArgs);
}
