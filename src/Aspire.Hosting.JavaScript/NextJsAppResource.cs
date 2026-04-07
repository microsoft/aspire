// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.JavaScript;

/// <summary>
/// Represents a Next.js application resource.
/// </summary>
/// <param name="name">The unique name used to identify the Next.js application resource.</param>
/// <param name="command">The command to execute the application.</param>
/// <param name="workingDirectory">The working directory from which the application command is executed.</param>
[AspireExport(ExposeProperties = true)]
public class NextJsAppResource(string name, string command, string workingDirectory)
    : JavaScriptAppResource(name, command, workingDirectory);
