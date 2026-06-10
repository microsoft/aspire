// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Projects;
using Aspire.Cli.Tests.Utils;

namespace Aspire.Cli.Tests.Projects;

public sealed class LanguageInfoTests(ITestOutputHelper outputHelper)
{
    private static readonly LanguageInfo s_csharp = new(
        LanguageId: new LanguageId("csharp"),
        DisplayName: "C# (.NET)",
        PackageName: "",
        DetectionPatterns: ["*.csproj", "*.fsproj", "apphost.cs"],
        CodeGenerator: "");

    private static readonly LanguageInfo s_typescript = new(
        LanguageId: new LanguageId("typescript/nodejs"),
        DisplayName: "TypeScript (Node.js)",
        PackageName: "Aspire.Hosting.CodeGeneration.TypeScript",
        DetectionPatterns: ["apphost.ts"],
        CodeGenerator: "TypeScript",
        AppHostFileName: "apphost.ts");

    [Theory]
    [InlineData("MyApp.csproj", true)]
    [InlineData("Foo.fsproj", true)]
    [InlineData("apphost.cs", true)]
    [InlineData("MYAPP.CSPROJ", true)]   // case-insensitive extension
    [InlineData("apphost.ts", false)]     // not a C# pattern
    [InlineData("readme.txt", false)]
    [InlineData("csproj", false)]         // no dot — should not match *.csproj
    public void MatchesFile_CSharpPatterns(string fileName, bool expected)
    {
        Assert.Equal(expected, s_csharp.MatchesFile(fileName));
    }

    [Theory]
    [InlineData("apphost.ts", true)]
    [InlineData("APPHOST.TS", true)]      // case-insensitive exact match
    [InlineData("apphost.cs", false)]
    [InlineData("MyApp.csproj", false)]
    public void MatchesFile_TypeScriptPatterns(string fileName, bool expected)
    {
        Assert.Equal(expected, s_typescript.MatchesFile(fileName));
    }

    [Theory]
    [InlineData("*.csproj", "Foo.csproj", true)]
    [InlineData("*.csproj", "FOO.CSPROJ", true)]
    [InlineData("*.csproj", "csproj", false)]           // no dot
    [InlineData("*.csproj", "Foo.fsproj", false)]
    [InlineData("apphost.ts", "apphost.ts", true)]
    [InlineData("apphost.ts", "APPHOST.TS", true)]
    [InlineData("apphost.ts", "apphost.cs", false)]
    public void MatchesPattern_HandlesWildcardAndExact(string pattern, string fileName, bool expected)
    {
        Assert.Equal(expected, LanguageInfo.MatchesPattern(fileName, pattern));
    }

    [Fact]
    public void FindInDirectory_FindsExactFile()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        File.WriteAllText(Path.Combine(workspace.WorkspaceRoot.FullName, "apphost.ts"), "");

        var result = s_typescript.FindInDirectory(workspace.WorkspaceRoot.FullName);
        Assert.NotNull(result);
        Assert.EndsWith("apphost.ts", result);
    }

    [Fact]
    public void FindInDirectory_FindsGlobPattern()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        File.WriteAllText(Path.Combine(workspace.WorkspaceRoot.FullName, "MyApp.csproj"), "");

        var result = s_csharp.FindInDirectory(workspace.WorkspaceRoot.FullName);
        Assert.NotNull(result);
        Assert.EndsWith("MyApp.csproj", result);
    }

    [Fact]
    public void FindInDirectory_FindsInNestedDirectory()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var nested = Path.Combine(workspace.WorkspaceRoot.FullName, "src", "AppHost");
        Directory.CreateDirectory(nested);
        File.WriteAllText(Path.Combine(nested, "apphost.cs"), "");

        var result = s_csharp.FindInDirectory(workspace.WorkspaceRoot.FullName);
        Assert.NotNull(result);
        Assert.EndsWith("apphost.cs", result);
    }

    [Fact]
    public void FindInDirectory_RespectsRecurseLimit()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        // Create a file deeper than DetectionRecurseLimit (5)
        var deep = Path.Combine(workspace.WorkspaceRoot.FullName, "a", "b", "c", "d", "e", "f");
        Directory.CreateDirectory(deep);
        File.WriteAllText(Path.Combine(deep, "apphost.cs"), "");

        var result = s_csharp.FindInDirectory(workspace.WorkspaceRoot.FullName);
        Assert.Null(result);
    }

    [Fact]
    public void FindInDirectory_ReturnsNullForEmptyDirectory()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        var result = s_csharp.FindInDirectory(workspace.WorkspaceRoot.FullName);
        Assert.Null(result);
    }

    [Fact]
    public void FindInDirectory_ReturnsNullForNonExistentDirectory()
    {
        var result = s_csharp.FindInDirectory(Path.Combine("C:", "NonExistent", "Dir"));
        Assert.Null(result);
    }
}
