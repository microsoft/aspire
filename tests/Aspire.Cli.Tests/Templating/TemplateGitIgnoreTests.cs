// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Cli.Tests.Templating;

public class TemplateGitIgnoreTests
{
    [Theory]
    [InlineData("src\\Aspire.Cli\\Templating\\Templates\\ts-starter\\.gitignore")]
    [InlineData("src\\Aspire.Cli\\Templating\\Templates\\py-starter\\.gitignore")]
    [InlineData("src\\Aspire.Cli\\Templating\\Templates\\java-starter\\.gitignore")]
    public void StarterTemplates_IgnoreWorkspaceAspireDirectory(string relativePath)
    {
        var filePath = Path.Combine(GetRepoRoot(), relativePath);

        Assert.True(File.Exists(filePath), $"Expected template .gitignore at {filePath}");

        var content = File.ReadAllText(filePath);
        Assert.Contains(".aspire/", content, StringComparison.Ordinal);
    }

    private static string GetRepoRoot()
        => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
}
