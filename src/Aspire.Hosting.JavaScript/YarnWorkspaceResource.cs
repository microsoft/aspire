// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.JavaScript;

/// <summary>
/// A resource that represents a Yarn workspace.
/// </summary>
/// <param name="name">The name of the resource.</param>
/// <param name="workingDirectory">The working directory of the workspace.</param>
public class YarnWorkspaceResource(string name, string workingDirectory)
    : JavaScriptWorkspaceResource(name, workingDirectory)
{
    /// <inheritdoc />
    public override string[] GetCommandPrefix(string projectName) => ["workspace", projectName];
}
