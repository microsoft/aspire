// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Cli.Projects;

internal static class ProjectFileExtensions
{
    public static readonly HashSet<string> Supported = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".csproj", ".fsproj", ".vbproj" };
}
