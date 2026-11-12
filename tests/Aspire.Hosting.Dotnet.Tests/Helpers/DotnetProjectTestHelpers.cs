// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.Dotnet.Tests;

internal static class DotnetProjectTestHelpers
{
    public static void WriteProgram(string projectPath, string csharp, string fsharp, string visualBasic)
    {
        var (fileName, source) = Path.GetExtension(projectPath).ToLowerInvariant() switch
        {
            ".csproj" => ("Program.cs", csharp),
            ".fsproj" => ("Program.fs", fsharp),
            ".vbproj" => ("Program.vb", visualBasic),
            _ => throw new ArgumentException("Expected a C#, F#, or Visual Basic project.", nameof(projectPath))
        };

        File.WriteAllText(Path.Combine(Path.GetDirectoryName(projectPath)!, fileName), source);
    }

    public static string GetCompileItems(string extension) =>
        extension.Equals(".fsproj", StringComparison.OrdinalIgnoreCase)
            ? """<ItemGroup><Compile Include="Program.fs" /></ItemGroup>"""
            : "";
}
