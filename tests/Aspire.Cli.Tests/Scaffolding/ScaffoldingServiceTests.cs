// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Scaffolding;

namespace Aspire.Cli.Tests.Scaffolding;

public class ScaffoldingServiceTests
{
    [Fact]
    public void GetConflictingScaffoldFiles_IgnoresMergeableFilesButReturnsOtherExistingFiles()
    {
        var rootDirectory = Directory.CreateTempSubdirectory();

        try
        {
            File.WriteAllText(Path.Combine(rootDirectory.FullName, ".gitignore"), "node_modules/\n");
            File.WriteAllText(Path.Combine(rootDirectory.FullName, "package.json"), "{}");
            File.WriteAllText(Path.Combine(rootDirectory.FullName, "apphost.ts"), string.Empty);

            var conflicts = ScaffoldingService.GetConflictingScaffoldFiles(
                rootDirectory.FullName,
                [".gitignore", "package.json", "apphost.ts"]);

            Assert.Equal(["apphost.ts"], conflicts);
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
        var scaffoldContent = "node_modules/\n.modules/\ndist/\n.aspire/\n";

        var mergedContent = ScaffoldingService.MergeGitIgnoreContent(existingContent, scaffoldContent);
        var lines = mergedContent.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        Assert.Equal(
            ["node_modules/", "custom/", ".modules/", "dist/", ".aspire/"],
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
