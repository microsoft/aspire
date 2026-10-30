// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Templating;
using Aspire.Cli.Tests.Utils;
using Microsoft.Extensions.DependencyInjection;

namespace Aspire.Cli.Tests.Templating;

public class CliTemplateFactoryConfigLineEndingTests(ITestOutputHelper outputHelper)
{
    /// <summary>
    /// Embedded files keep the line breaks of the checkout that built the CLI, so a CLI built on one OS can
    /// carry templates whose line breaks are not the OS line break of the machine that runs it. The token
    /// replacer returns the other style to reproduce that whatever the line breaks of this repository's
    /// checkout are, so the assertion only passes if the copy itself converts the config files.
    /// </summary>
    [Theory]
    [InlineData("empty-apphost", "aspire.config.json")]
    [InlineData("empty-apphost", "apphost.run.json")]
    [InlineData("go-starter", "aspire.config.json")]
    [InlineData("java-starter", "aspire.config.json")]
    [InlineData("py-starter", "aspire.config.json")]
    [InlineData("ts-starter", "aspire.config.json")]
    public async Task CopyTemplateTreeToDiskAsync_WritesConfigFileWithOsLineBreaksAndSingleFinalNewline(string templateRoot, string fileName)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);

        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper);
        using var serviceProvider = services.BuildServiceProvider();
        var factory = ActivatorUtilities.CreateInstance<CliTemplateFactory>(serviceProvider);

        var outputDirectory = Path.Combine(workspace.WorkspaceRoot.FullName, "out");

        await factory.CopyTemplateTreeToDiskAsync(
            templateRoot,
            outputDirectory,
            content => content.ReplaceLineEndings(LineEndingAssert.NonOsNewLine),
            CancellationToken.None);

        LineEndingAssert.FileUsesOsLineBreaksAndSingleFinalNewline(Path.Combine(outputDirectory, fileName));
    }
}
