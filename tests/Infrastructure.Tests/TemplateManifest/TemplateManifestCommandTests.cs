// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.CommandLine;
using GenerateTemplateManifest;
using Xunit;

namespace Infrastructure.Tests.TemplateManifest;

public sealed class TemplateManifestCommandTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(null, false)]
    [InlineData("--plan-only", false)]
    [InlineData("--plan-only", true)]
    public void ParsesTypedPathsAndOptionsInEitherPosition(string? option, bool optionFirst)
    {
        using var workspace = TemporaryWorkspace.Create(output);
        var arguments = CreateArguments(workspace);
        var command = new TemplateManifestCommand();
        var result = command.Parse(option is null ? arguments :
            optionFirst ? [option, .. arguments] : [.. arguments, option]);

        Assert.Empty(result.Errors);
        Assert.Equal(arguments[0], result.GetValue(command.SourceDirectory)!.FullName);
        Assert.Equal(arguments[1], result.GetValue(command.ProcessedDirectory)!.FullName);
        Assert.Equal(arguments[2], result.GetValue(command.ManifestPath)!.FullName);
        Assert.Equal(arguments[3], result.GetValue(command.NuGetConfigPath)!.FullName);
        Assert.Equal(arguments[4], result.GetValue(command.LocalPackageFeed)!.FullName);
        Assert.Equal(arguments[5], result.GetValue(command.RestoreDirectory)!.FullName);
        Assert.Equal(option == "--plan-only", result.GetValue(command.PlanOnly));
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("extra")]
    [InlineData("unknown-option")]
    [InlineData("removed-verify-option")]
    [InlineData("missing-source")]
    [InlineData("missing-processed")]
    [InlineData("missing-config")]
    public async Task RejectsInvalidArgumentsWithoutExecutingTheHandler(string scenario)
    {
        using var workspace = TemporaryWorkspace.Create(output);
        var arguments = CreateArguments(workspace);
        switch (scenario)
        {
            case "missing":
                arguments = arguments[..^1];
                break;
            case "extra":
                arguments = [.. arguments, "unexpected"];
                break;
            case "unknown-option":
                arguments = [.. arguments, "--unknown"];
                break;
            case "removed-verify-option":
                arguments = [.. arguments, "--verify"];
                break;
            case "missing-source":
                arguments[0] = Path.Combine(workspace.Path, "missing");
                break;
            case "missing-processed":
                arguments[1] = Path.Combine(workspace.Path, "missing");
                break;
            case "missing-config":
                arguments[3] = Path.Combine(workspace.Path, "missing.config");
                break;
        }
        var command = new TemplateManifestCommand();
        var called = false;
        command.SetAction(_ => called = true);
        var result = command.Parse(arguments);
        using var errors = new StringWriter();
        using var help = new StringWriter();

        Assert.NotEmpty(result.Errors);
        var exitCode = await result.InvokeAsync(new InvocationConfiguration { Output = help, Error = errors });

        Assert.NotEqual(0, exitCode);
        Assert.False(called);
        Assert.NotEmpty(errors.ToString());
    }

    [Fact]
    public async Task HelpSucceedsWithoutPathsOrRunningTheHandler()
    {
        var command = new TemplateManifestCommand();
        var called = false;
        command.SetAction(_ => called = true);
        using var help = new StringWriter();
        using var errors = new StringWriter();

        var exitCode = await command.Parse("--help").InvokeAsync(new InvocationConfiguration { Output = help, Error = errors });

        Assert.Equal(0, exitCode);
        Assert.False(called);
        Assert.NotEmpty(help.ToString());
        Assert.Empty(errors.ToString());
    }

    private static string[] CreateArguments(TemporaryWorkspace workspace)
    {
        var source = workspace.CreateDirectory("source templates").FullName;
        var processed = workspace.CreateDirectory("processed templates").FullName;
        var config = Path.Combine(workspace.Path, "nuget.config");
        File.WriteAllText(config, "<configuration />");
        return
        [
            source, processed, Path.Combine(workspace.Path, "cgmanifest.json"), config,
            Path.Combine(workspace.Path, "package feed"), Path.Combine(workspace.Path, "restore directory")
        ];
    }
}
