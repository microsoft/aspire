// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.JavaScript;

internal sealed class JavaScriptWorkspaceAppPathAnnotation(string appDirectory, string packagePath) : IResourceAnnotation
{
    public string AppDirectory { get; } = appDirectory;

    public string PackagePath { get; } = packagePath;
}