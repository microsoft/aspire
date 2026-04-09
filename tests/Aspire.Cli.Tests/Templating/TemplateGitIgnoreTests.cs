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

        var content = File.ReadAllText(filePath);
        Assert.Contains(".aspire/", content, StringComparison.Ordinal);
    }

    private static string GetRepoRoot()
        => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
}
