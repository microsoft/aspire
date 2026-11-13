// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.Azure.Provisioning.Internal;
using Aspire.Hosting.Utils;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aspire.Hosting.Azure.Tests;

public class BicepCliCompilerTests
{
    [Fact]
    public async Task ProvisioningCompilerUsesRegisteredRunnerForCompilation()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        var runner = new MockProcessRunner
        {
            RunAsync = (_, _) => Task.FromResult(new ProcessCaptureResult(0, "{\"resources\":[]}", ""))
        };
        builder.Services.AddSingleton<IProcessRunner>(runner);
        builder.AddAzureProvisioning();
        await using var application = builder.Build();
        var compiler = application.Services.GetRequiredService<IBicepCompiler>();
        Assert.Empty(runner.ExecutedCommands);

        Assert.Equal("{\"resources\":[]}", await compiler.CompileBicepToArmAsync("template.bicep", TestContext.Current.CancellationToken));

        Assert.Single(runner.ExecutedCommands);
    }

    [Theory]
    [InlineData("template with spaces.bicep", "{\r\n  \"name\": \"日本語\"\r\n}")]
    [InlineData("quotes\"and%literal!.bicep", "{\"resources\":[]}")]
    public async Task ReturnsExactTemplateFromStandaloneBicep(string path, string template)
    {
        var runner = new MockProcessRunner { RunAsync = (_, _) => Task.FromResult(new ProcessCaptureResult(0, template, "a warning")) };
        var compiler = new BicepCliCompiler(NullLogger<BicepCliCompiler>.Instance, runner);
        Assert.Empty(runner.ExecutedCommands);

        Assert.Equal(template, await compiler.CompileBicepToArmAsync(path, TestContext.Current.CancellationToken));

        var command = Assert.Single(runner.ExecutedCommands);
        Assert.Equal("bicep", command.ExecutablePath);
        Assert.Equal(["build", path, "--stdout"], command.Arguments);
        Assert.True(command.ResolveExecutablePath);
    }

    [Fact]
    public async Task MissingStandaloneToolFallsBackToAzureCli()
    {
        var runner = new MockProcessRunner
        {
            RunAsync = (command, _) => command.ExecutablePath == "bicep"
                ? throw new FileNotFoundException("Not on PATH.", "bicep")
                : Task.FromResult(new ProcessCaptureResult(0, "{\"resources\":[]}", ""))
        };
        var compiler = new BicepCliCompiler(NullLogger<BicepCliCompiler>.Instance, runner);
        Assert.Empty(runner.ExecutedCommands);

        Assert.Equal("{\"resources\":[]}", await compiler.CompileBicepToArmAsync("template name.bicep", TestContext.Current.CancellationToken));

        Assert.Equal(["bicep", "az"], runner.ExecutedCommands.Select(command => command.ExecutablePath));
        Assert.Equal(["bicep", "build", "--file", "template name.bicep", "--stdout"], runner.ExecutedCommands[1].Arguments);
    }

    [Theory]
    [InlineData("bicep")]
    [InlineData("az")]
    public async Task NonzeroCompilationReportsDiagnosticsWithoutTryingAnotherCompiler(string tool)
    {
        var runner = new MockProcessRunner
        {
            RunAsync = (command, _) => command.ExecutablePath != tool
                ? throw new FileNotFoundException("Not on PATH.", command.ExecutablePath)
                : Task.FromResult(new ProcessCaptureResult(1, "not a template", "\r\nlate diagnostic 日本語\r\n"))
        };
        var compiler = new BicepCliCompiler(NullLogger<BicepCliCompiler>.Instance, runner);
        Assert.Empty(runner.ExecutedCommands);

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => compiler.CompileBicepToArmAsync("broken.bicep", TestContext.Current.CancellationToken));

        Assert.Equal($"Failed to compile bicep file: broken.bicep{Environment.NewLine}late diagnostic 日本語", failure.Message);
        Assert.Equal(tool == "bicep" ? 1 : 2, runner.ExecutedCommands.Count);
    }

    [Fact]
    public async Task MissingBothCompilersReportsAzureCliUnavailable()
    {
        var runner = new MockProcessRunner { RunAsync = (command, _) => throw new FileNotFoundException("Not on PATH.", command.ExecutablePath) };
        var compiler = new BicepCliCompiler(NullLogger<BicepCliCompiler>.Instance, runner);
        Assert.Empty(runner.ExecutedCommands);

        await Assert.ThrowsAsync<AzureCliNotOnPathException>(() => compiler.CompileBicepToArmAsync("template.bicep", TestContext.Current.CancellationToken));

        Assert.Equal(["bicep", "az"], runner.ExecutedCommands.Select(command => command.ExecutablePath));
    }

    [Fact]
    public async Task MissingCompilationInputDoesNotTriggerToolFallback()
    {
        var failure = new FileNotFoundException("Input disappeared.", "template.bicep");
        var runner = new MockProcessRunner { RunAsync = (_, _) => throw failure };
        var compiler = new BicepCliCompiler(NullLogger<BicepCliCompiler>.Instance, runner);
        Assert.Empty(runner.ExecutedCommands);

        Assert.Same(failure, await Assert.ThrowsAsync<FileNotFoundException>(() => compiler.CompileBicepToArmAsync("template.bicep", TestContext.Current.CancellationToken)));

        Assert.Single(runner.ExecutedCommands);
    }

    [Fact]
    public async Task CancellationEndsInFlightCompilationRatherThanReturningTemplate()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runner = new MockProcessRunner
        {
            RunAsync = async (_, token) =>
            {
                started.SetResult();
                await release.Task.WaitAsync(token);
                return new ProcessCaptureResult(0, "{\"resources\":[]}", "");
            }
        };
        var compiler = new BicepCliCompiler(NullLogger<BicepCliCompiler>.Instance, runner);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var compilation = compiler.CompileBicepToArmAsync("template.bicep", cancellation.Token);
        await started.Task.WaitAsync(TestContext.Current.CancellationToken);
        Assert.False(compilation.IsCompleted);

        cancellation.Cancel();
        release.SetResult();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => compilation);
        Assert.Single(runner.ExecutedCommands);
    }
}
