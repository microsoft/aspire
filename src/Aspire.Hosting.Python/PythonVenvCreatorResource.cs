// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.Python;

/// <summary>
/// A resource that represents a Python virtual environment creator task for a Python application.
/// </summary>
/// <param name="name">The name of the resource.</param>
/// <param name="parent">The parent Python application resource.</param>
/// <param name="venvPath">The path where the virtual environment should be created.</param>
internal sealed class PythonVenvCreatorResource(string name, PythonAppResource parent, string venvPath)
    : ExecutableResource(name, "python", parent.WorkingDirectory)
{
    /// <summary>
    /// Gets the path where the virtual environment will be created.
    /// </summary>
    public string VenvPath => venvPath;
}
