// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Cli.Tests.Templating;

public class TemplateGitIgnoreTests
{
    [Theory]
    [InlineData("ts-starter")]
    [InlineData("py-starter")]
    [InlineData("java-starter")]
    public void StarterTemplates_IgnoreWorkspaceAspireDirectory(string templateName)
    {
        var filePath = Path.Combine(GetRepoRoot(), "src", "Aspire.Cli", "Templating", "Templates", templateName, ".gitignore");

        Assert.True(File.Exists(filePath), $"Expected template .gitignore at {filePath}");

        var lines = File.ReadAllLines(filePath);
        Assert.Contains(".aspire/", lines);
    }

    [Fact]
    public void StarterTemplatesWithFrontend_IgnoreNodeModulesAndBuildOutput()
    {
        var templatesDirectory = Path.Combine(GetRepoRoot(), "src", "Aspire.Cli", "Templating", "Templates");
        var frontendStarters = Directory
            .EnumerateFiles(templatesDirectory, "package.json", SearchOption.AllDirectories)
            .Where(path => string.Equals(Path.GetFileName(Path.GetDirectoryName(path)), "frontend", StringComparison.Ordinal))
            .Select(path => Directory.GetParent(Path.GetDirectoryName(path)!)!.FullName)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.NotEmpty(frontendStarters);

        foreach (var starterDirectory in frontendStarters)
        {
            var gitIgnorePath = Path.Combine(starterDirectory, ".gitignore");
            Assert.True(File.Exists(gitIgnorePath), $"Expected template .gitignore at {gitIgnorePath}");

            var lines = File.ReadAllLines(gitIgnorePath);
            Assert.Contains("node_modules/", lines);
            Assert.Contains("dist/", lines);
        }
    }

    private static string GetRepoRoot()
        => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
}
