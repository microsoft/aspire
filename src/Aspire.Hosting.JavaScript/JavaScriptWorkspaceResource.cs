// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.JavaScript;

/// <summary>
/// A resource that represents a JavaScript workspace.
/// </summary>
/// <param name="name">The name of the resource.</param>
/// <param name="workingDirectory">The working directory of the workspace.</param>
public abstract class JavaScriptWorkspaceResource(string name, string workingDirectory)
    : Resource(name)
{
    /// <summary>
    /// Gets the working directory of the workspace.
    /// </summary>
    public string WorkingDirectory { get; } = workingDirectory;

    /// <summary>
    /// Gets the package manager command prefix for running commands in a specific workspace project.
    /// For example, Yarn returns <c>["workspace", projectName]</c> and pnpm returns <c>["--filter", projectName]</c>.
    /// </summary>
    /// <param name="projectName">The name of the project in the workspace.</param>
    /// <returns>The command prefix arguments.</returns>
    public abstract string[] GetCommandPrefix(string projectName);
}
