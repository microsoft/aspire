// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.JavaScript;

/// <summary>
/// A resource that represents a package installer for a JavaScript app.
/// </summary>
/// <param name="name">The name of the resource.</param>
/// <param name="workingDirectory">The working directory to use for the command.</param>
public class JavaScriptInstallerResource(string name, string workingDirectory)
    : ExecutableResource(name, "node", workingDirectory);
