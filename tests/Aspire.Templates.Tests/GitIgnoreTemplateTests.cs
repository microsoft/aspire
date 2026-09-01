// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Xunit;

namespace Aspire.Templates.Tests;

public class GitIgnoreTemplateTests(ITestOutputHelper testOutput) : TemplateTestsBase(testOutput)
{
    [Theory]
    [InlineData("aspire-starter", "aspire-starter", "--skipRestore")]
    [InlineData("aspire-empty", "aspire", "--no-restore")]
    [InlineData("aspire-ts-cs-starter", "aspire-ts-cs-starter", "--skipRestore")]
    [InlineData("aspire-apphost", "aspire-apphost", "--no-restore")]
    [InlineData("aspire-apphost-singlefile", "aspire-apphost-singlefile", "")]
    public async Task TemplateIgnoresAspireWorkingDirectory(string templateDirectory, string templateName, string extraArgs)
    {
        var projectName = GetNewProjectId(prefix: $"gitignore_{templateDirectory}");
        var outputPath = Path.Combine(BuildEnvironment.TestRootPath, projectName);
        var buildEnvironment = BuildEnvironment.ForDefaultFramework;

        using var command = new DotNetNewCommand(
            _testOutput,
            buildEnv: buildEnvironment)
            .WithWorkingDirectory(BuildEnvironment.TestRootPath);

        var result = await command.ExecuteAsync($"{templateName} {extraArgs} -o \"{outputPath}\"");
        result.EnsureSuccessful();

        Assert.Equal([".aspire/"], await File.ReadAllLinesAsync(Path.Combine(outputPath, ".gitignore")));
    }
}
