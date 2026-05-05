// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.JavaScript;

/// <summary>
/// Represents the annotation for the JavaScript package manager's build script.
/// </summary>
/// <param name="scriptName">The name of the JavaScript package manager's build script.</param>
/// <param name="args">The command line arguments for the JavaScript package manager's build script.</param>
public sealed class JavaScriptBuildScriptAnnotation(string scriptName, string[]? args) : IResourceAnnotation
{
    /// <summary>
    /// Gets the name of the script used to build.
    /// </summary>
    public string ScriptName { get; } = scriptName;

    /// <summary>
    /// Gets the command-line arguments supplied to the build script.
    /// </summary>
    public string[] Args { get; } = args ?? [];
}
