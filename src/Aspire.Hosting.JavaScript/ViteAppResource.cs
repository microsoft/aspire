// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.JavaScript;

/// <summary>
/// Represents a Vite application resource that can be managed and executed within a Node.js environment.
/// </summary>
/// <param name="name">The unique name used to identify the Vite application resource.</param>
/// <param name="command">The command to execute the Vite application, such as the script or entry point.</param>
/// <param name="workingDirectory">The working directory from which the Vite application command is executed.</param>
[AspireExport(ExposeProperties = true)]
public class ViteAppResource(string name, string command, string workingDirectory)
    : JavaScriptAppResource(name, command, workingDirectory)
{
    /// <summary>
    /// Gets or sets the path to the Vite configuration file.
    /// </summary>
    internal string? ViteConfigPath { get; set; }
}
