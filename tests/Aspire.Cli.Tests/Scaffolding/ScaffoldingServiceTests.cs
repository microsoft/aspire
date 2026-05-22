// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json.Nodes;
using Aspire.Cli.Scaffolding;
using Aspire.Cli.Projects;

namespace Aspire.Cli.Tests.Scaffolding;

public class ScaffoldingServiceTests
{
    private static readonly LanguageInfo s_typeScriptLanguage = new(
        LanguageId: new LanguageId(KnownLanguageId.TypeScript),
        DisplayName: "TypeScript (Node.js)",
        PackageName: "@aspire/app-host",
        DetectionPatterns: ["apphost.mts", "apphost.ts"],
        CodeGenerator: "TypeScript",
        AppHostFileName: "apphost.mts");

    private static readonly LanguageInfo s_pythonLanguage = new(
        LanguageId: new LanguageId("python"),
        DisplayName: "Python",
        PackageName: "aspire-app-host",
        DetectionPatterns: ["apphost.py"],
        CodeGenerator: "python",
        AppHostFileName: "apphost.py");

    [Fact]
    public void GetScaffoldDirectory_UsesNestedPackage_ForBrownfieldTypeScript()
    {
        var rootDirectory = Directory.CreateTempSubdirectory();

        try
        {
            File.WriteAllText(Path.Combine(rootDirectory.FullName, "package.json"), "{}");

            var scaffoldDirectory = ScaffoldingService.GetScaffoldDirectory(rootDirectory, s_typeScriptLanguage);

            Assert.Equal(
                Path.Combine(rootDirectory.FullName, ScaffoldingService.BrownfieldTypeScriptAppHostDirectoryName),
                scaffoldDirectory.FullName);
        }
        finally
        {
            rootDirectory.Delete(recursive: true);
        }
    }

    [Fact]
    public void GetScaffoldDirectory_UsesRoot_ForGreenfieldTypeScript()
    {
        var rootDirectory = Directory.CreateTempSubdirectory();

        try
        {
            var scaffoldDirectory = ScaffoldingService.GetScaffoldDirectory(rootDirectory, s_typeScriptLanguage);

            Assert.Equal(rootDirectory.FullName, scaffoldDirectory.FullName);
        }
        finally
        {
            rootDirectory.Delete(recursive: true);
        }
    }

    [Fact]
    public void GetScaffoldDirectory_UsesRoot_ForNonTypeScript()
    {
        var rootDirectory = Directory.CreateTempSubdirectory();

        try
        {
            File.WriteAllText(Path.Combine(rootDirectory.FullName, "package.json"), "{}");

            var scaffoldDirectory = ScaffoldingService.GetScaffoldDirectory(rootDirectory, s_pythonLanguage);

            Assert.Equal(rootDirectory.FullName, scaffoldDirectory.FullName);
        }
        finally
        {
            rootDirectory.Delete(recursive: true);
        }
    }

    [Fact]
    public void SerializePackageJson_PreservesTrailingNewLine_WhenOriginalHadOne()
    {
        var packageJson = JsonNode.Parse("""{ "scripts": { "aspire:start": "npm --prefix aspire-apphost run aspire:start" } }""")!.AsObject();

        var serialized = ScaffoldingService.SerializePackageJson(packageJson, "{\n}\n");

        Assert.EndsWith("\n", serialized);
    }

    [Fact]
    public void SerializePackageJson_PreservesTrailingNewLineStyle_WhenOriginalHadWindowsNewLine()
    {
        var packageJson = JsonNode.Parse("""{ "scripts": { "aspire:start": "npm --prefix aspire-apphost run aspire:start" } }""")!.AsObject();

        var serialized = ScaffoldingService.SerializePackageJson(packageJson, "{\r\n}\r\n");

        Assert.EndsWith("\r\n", serialized);
    }

    [Fact]
    public void SerializePackageJson_DoesNotAddTrailingNewLine_WhenOriginalDidNotHaveOne()
    {
        var packageJson = JsonNode.Parse("""{ "scripts": { "aspire:start": "npm --prefix aspire-apphost run aspire:start" } }""")!.AsObject();

        var serialized = ScaffoldingService.SerializePackageJson(packageJson, "{}");

        Assert.False(serialized.EndsWith(Environment.NewLine, StringComparison.Ordinal));
    }

    [Fact]
    public void GetScaffoldedAppHostRelativePath_UsesActualScaffoldedFile_WhenDefaultFileNameDiffers()
    {
        var rootDirectory = Directory.CreateTempSubdirectory();

        try
        {
            var relativePath = ScaffoldingService.GetScaffoldedAppHostRelativePath(
                rootDirectory,
                rootDirectory,
                s_typeScriptLanguage,
                ["apphost.ts"]);

            Assert.Equal("apphost.ts", relativePath);
        }
        finally
        {
            rootDirectory.Delete(recursive: true);
        }
    }

    [Fact]
    public void GetScaffoldedAppHostRelativePath_UsesNestedActualScaffoldedFile_ForBrownfieldTypeScript()
    {
        var rootDirectory = Directory.CreateTempSubdirectory();

        try
        {
            var scaffoldDirectory = Directory.CreateDirectory(Path.Combine(rootDirectory.FullName, ScaffoldingService.BrownfieldTypeScriptAppHostDirectoryName));

            var relativePath = ScaffoldingService.GetScaffoldedAppHostRelativePath(
                rootDirectory,
                scaffoldDirectory,
                s_typeScriptLanguage,
                ["apphost.ts"]);

            Assert.Equal("aspire-apphost/apphost.ts", relativePath);
        }
        finally
        {
            rootDirectory.Delete(recursive: true);
        }
    }

    [Fact]
    public void GetConflictingScaffoldFiles_IgnoresMergeableFilesButReturnsOtherExistingFiles()
    {
        var rootDirectory = Directory.CreateTempSubdirectory();

        try
        {
            File.WriteAllText(Path.Combine(rootDirectory.FullName, ".gitignore"), "node_modules/\n");
            File.WriteAllText(Path.Combine(rootDirectory.FullName, "package.json"), "{}");
            File.WriteAllText(Path.Combine(rootDirectory.FullName, "apphost.mts"), string.Empty);

            var conflicts = ScaffoldingService.GetConflictingScaffoldFiles(
                rootDirectory.FullName,
                [".gitignore", "package.json", "apphost.mts"]);

            Assert.Equal(["apphost.mts"], conflicts);
        }
        finally
        {
            rootDirectory.Delete(recursive: true);
        }
    }

    [Fact]
    public void MergeGitIgnoreContent_AppendsMissingEntriesWithoutOverwritingExistingContent()
    {
        var existingContent = "node_modules/\ncustom/\n";
        var scaffoldContent = "node_modules/\ndist/\n.aspire/\n";

        var mergedContent = ScaffoldingService.MergeGitIgnoreContent(existingContent, scaffoldContent);
        var lines = mergedContent.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        Assert.Equal(
            ["node_modules/", "custom/", "dist/", ".aspire/"],
            lines);
    }

    [Fact]
    public void MergeGitIgnoreContent_DoesNotAddDuplicateAspireEntryWhenEquivalentEntryAlreadyExists()
    {
        var existingContent = "/.aspire/\n";
        var scaffoldContent = ".aspire/\n";

        var mergedContent = ScaffoldingService.MergeGitIgnoreContent(existingContent, scaffoldContent);

        Assert.Equal(existingContent, mergedContent);
    }
}
