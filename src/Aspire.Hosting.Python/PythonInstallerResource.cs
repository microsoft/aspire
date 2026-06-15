// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.Python;

/// <summary>
/// A resource that represents a Python package installer task for a Python application.
/// </summary>
/// <param name="name">The name of the resource.</param>
/// <param name="parent">The parent Python application resource.</param>
internal sealed class PythonInstallerResource(string name, PythonAppResource parent)
    : ExecutableResource(name, "python", parent.WorkingDirectory)
{
}
