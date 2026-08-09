// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using Aspire.Hosting.Utils;

namespace Aspire.Hosting.Dotnet;

/// <summary>
/// Project metadata for a C# project or file-based app that was added by path.
/// </summary>
[DebuggerDisplay("Type = {GetType().Name,nq}, ProjectPath = {ProjectPath}")]
internal sealed class DotnetProjectMetadata(string projectPath) : IProjectMetadata
{
    private string? _resolvedProjectPath;

    // Resolution is deferred so construction never touches the file system; an unresolvable path is
    // reported as a resource start failure instead.
    public string ProjectPath => _resolvedProjectPath ??= ProjectPathResolver.ResolveProjectPath(projectPath);
}
