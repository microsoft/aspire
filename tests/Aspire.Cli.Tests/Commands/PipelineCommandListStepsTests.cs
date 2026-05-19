// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.RegularExpressions;
using Aspire.Cli.Backchannel;
using Aspire.Cli.Commands;
using Aspire.Cli.Tests.Utils;
using Microsoft.Extensions.DependencyInjection;
using Spectre.Console;

namespace Aspire.Cli.Tests.Commands;

public partial class PipelineCommandListStepsTests(ITestOutputHelper outputHelper)
{
    [GeneratedRegex(@"\x1B\[[0-9;]*m")]
    private static partial Regex AnsiEscapeRegex();

    private static string StripAnsi(string text) => AnsiEscapeRegex().Replace(text, "");

    [Fact]
    public void PrintPipelineSteps_WithNoDependencies_ShowsNoDependencies()
    {
        var (command, writer) = CreateCommandWithCapturedOutput();

        command.PrintPipelineSteps([new PipelineStepInfo { Name = "parameter-prompt" }]);

        var output = StripAnsi(writer.ToString());
        Assert.Contains("1. parameter-prompt", output);
        Assert.Contains("No dependencies", output);
    }

    [Fact]
    public void PrintPipelineSteps_WithDependencies_ShowsDependsOn()
    {
        var (command, writer) = CreateCommandWithCapturedOutput();

        command.PrintPipelineSteps([
            new PipelineStepInfo { Name = "parameter-prompt" },
            new PipelineStepInfo { Name = "build-webapi", DependsOn = ["parameter-prompt"] }
        ]);

        var output = StripAnsi(writer.ToString());
        Assert.Contains("2. build-webapi", output);
        Assert.Contains("Depends on: parameter-prompt", output);
    }

    [Fact]
    public void PrintPipelineSteps_WithMultipleDependencies_ShowsAllDependencies()
    {
        var (command, writer) = CreateCommandWithCapturedOutput();

        command.PrintPipelineSteps([
            new PipelineStepInfo { Name = "deploy-webapi", DependsOn = ["build-webapi", "provision-redis"] }
        ]);

        var output = StripAnsi(writer.ToString());
        Assert.Contains("Depends on: build-webapi, provision-redis", output);
    }

    [Fact]
    public void PrintPipelineSteps_WithTags_ShowsTags()
    {
        var (command, writer) = CreateCommandWithCapturedOutput();

        command.PrintPipelineSteps([
            new PipelineStepInfo { Name = "build-webapi", DependsOn = ["parameter-prompt"], Tags = ["build-compute"] }
        ]);

        var output = StripAnsi(writer.ToString());
        Assert.Contains("Tags: build-compute", output);
    }

    [Fact]
    public void PrintPipelineSteps_WithDepsAndTags_ShowsBothConnectors()
    {
        var (command, writer) = CreateCommandWithCapturedOutput();

        command.PrintPipelineSteps([
            new PipelineStepInfo { Name = "provision-redis-infra", DependsOn = ["parameter-prompt"], Tags = ["provision-infra"] }
        ]);

        var output = StripAnsi(writer.ToString());
        Assert.Contains("Depends on: parameter-prompt", output);
        Assert.Contains("Tags: provision-infra", output);
    }

    [Fact]
    public void PrintPipelineSteps_WithEmptySteps_ShowsNoStepsMessage()
    {
        var (command, writer) = CreateCommandWithCapturedOutput();

        command.PrintPipelineSteps([]);

        var output = StripAnsi(writer.ToString());
        Assert.Contains("No pipeline steps found", output);
    }

    [Fact]
    public void PrintPipelineSteps_NumbersStepsSequentially()
    {
        var (command, writer) = CreateCommandWithCapturedOutput();

        command.PrintPipelineSteps([
            new PipelineStepInfo { Name = "step-a" },
            new PipelineStepInfo { Name = "step-b" },
            new PipelineStepInfo { Name = "step-c" }
        ]);

        var output = StripAnsi(writer.ToString());
        Assert.Contains("1. step-a", output);
        Assert.Contains("2. step-b", output);
        Assert.Contains("3. step-c", output);
    }

    [Fact]
    public void PrintPipelineSteps_FullPipelineOutput()
    {
        var (command, writer) = CreateCommandWithCapturedOutput();

        command.PrintPipelineSteps([
            new PipelineStepInfo { Name = "parameter-prompt" },
            new PipelineStepInfo { Name = "provision-redis-infra", DependsOn = ["parameter-prompt"], Tags = ["provision-infra"] },
            new PipelineStepInfo { Name = "provision-postgres-infra", DependsOn = ["parameter-prompt"], Tags = ["provision-infra"] },
            new PipelineStepInfo { Name = "build-webapi", DependsOn = ["parameter-prompt"], Tags = ["build-compute"] },
            new PipelineStepInfo { Name = "build-frontend", DependsOn = ["parameter-prompt"], Tags = ["build-compute"] },
            new PipelineStepInfo { Name = "deploy-webapi", DependsOn = ["provision-redis-infra", "provision-postgres-infra", "build-webapi"], Tags = ["deploy-compute"] },
            new PipelineStepInfo { Name = "deploy-frontend", DependsOn = ["build-frontend", "deploy-webapi"], Tags = ["deploy-compute"] },
        ]);

        var output = StripAnsi(writer.ToString());
        Assert.Contains("1. parameter-prompt", output);
        Assert.Contains("7. deploy-frontend", output);
        Assert.Contains("No dependencies", output);
        Assert.Contains("Depends on: provision-redis-infra, provision-postgres-infra, build-webapi", output);
        Assert.Contains("Tags: provision-infra", output);
        Assert.Contains("Tags: build-compute", output);
        Assert.Contains("Tags: deploy-compute", output);
    }

    private (PipelineCommandBase Command, StringWriter Writer) CreateCommandWithCapturedOutput()
    {
        using var tempRepo = TemporaryWorkspace.Create(outputHelper);
        var writer = new StringWriter();
        var console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Out = new AnsiConsoleOutput(writer),
            Ansi = AnsiSupport.No,
            Interactive = InteractionSupport.No
        });

        var services = CliTestHelper.CreateServiceCollection(tempRepo, outputHelper);
        services.AddSingleton<IAnsiConsole>(console);
        using var provider = services.BuildServiceProvider();
        return (provider.GetRequiredService<DoCommand>(), writer);
    }
}
