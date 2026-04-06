// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.JavaScript;

/// <summary>
/// A resource that represents a package installer for a JavaScript app.
/// </summary>
public class JavaScriptInstallerResource : ExecutableResource
{
    /// <summary>
    /// Initializes a new instance of the <see cref="JavaScriptInstallerResource"/> class.
    /// </summary>
    /// <param name="name">The name of the resource.</param>
    /// <param name="workingDirectory">The working directory to use for the command.</param>
    public JavaScriptInstallerResource(string name, string workingDirectory)
        : base(name, "node", workingDirectory, skipValidation: true) // Validation is skipped because appending "-installer" to the parent name can exceed the 64-char limit.
    {
    }
}
