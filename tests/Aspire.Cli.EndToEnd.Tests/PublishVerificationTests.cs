// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.EndToEnd.Tests.Helpers;
using Hex1b.Automation;
using Xunit;

namespace Aspire.Cli.EndToEnd.Tests;

/// <summary>
/// End-to-end coverage for Git-aware publish output verification.
/// </summary>
public sealed class PublishVerificationTests(ITestOutputHelper output)
{
    private const string ProjectName = "PublishVerificationApp";

    [Fact]
    [CaptureWorkspaceOnFailure]
    public async Task PublishVerify_DetectsAllDriftWithoutMutatingTargets()
    {
        var repoRoot = CliE2ETestHelpers.GetRepoRoot();
        var strategy = CliInstallStrategy.Detect(output.WriteLine);
        if (strategy.Mode == CliInstallMode.InstallScript &&
            strategy.Quality is null &&
            strategy.Version is null)
        {
            Assert.Skip("This test requires the current CLI and Hosting output-plan contract.");
        }

        using var workspace = TemporaryWorkspace.Create(output);
        using var terminal = CliE2ETestHelpers.CreateDockerTestTerminal(
            repoRoot,
            strategy,
            output,
            workspace: workspace);
        var counter = new SequenceCounter();
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: TimeSpan.FromSeconds(500));
        await using var terminalRun = CliE2ETestHelpers.StartRun(
            terminal,
            workspace,
            auto,
            counter,
            output,
            TestContext.Current.CancellationToken);

        await auto.PrepareDockerEnvironmentAsync(counter, workspace);
        await auto.InstallAspireCliAsync(strategy, counter);
        await auto.AspireNewCSharpEmptyAppHostAsync(ProjectName, counter);
        WriteVerificationAppHost(workspace);

        await auto.RunCommandAsync($"cd {ProjectName}", counter);
        await auto.RunCommandAsync("unset ASPIRE_PLAYGROUND", counter);
        await auto.RunCommandAsync("git init", counter);
        await auto.RunCommandAsync("git config user.email aspire-tests@example.com", counter);
        await auto.RunCommandAsync("git config user.name \"Aspire Tests\"", counter);

        await auto.RunCommandAsync(
            "aspire publish -o checked-output --non-interactive",
            counter,
            TimeSpan.FromMinutes(5));
        await auto.RunCommandAsync(
            "git add -f apphost.cs checked-output .configgen .pipelines && git commit -m generated",
            counter);

        await auto.RunCommandAsync(
            "aspire publish -o checked-output --verify --non-interactive",
            counter,
            TimeSpan.FromMinutes(5));

        await auto.RunCommandAsync(
            "printf 'stale\\n' > checked-output/primary.txt && " +
            "rm .configgen/config.json && " +
            "printf 'orphaned\\n' > .pipelines/orphan.yml",
            counter);

        await auto.TypeAsync("aspire publish -o checked-output --verify --non-interactive");
        await auto.EnterAsync();
        await auto.WaitUntilTextAsync(
            "Stale files in 'checked-output':",
            timeout: TimeSpan.FromMinutes(5));
        await auto.WaitUntilTextAsync(
            "Missing files in '.configgen':",
            timeout: TimeSpan.FromSeconds(30));
        await auto.WaitUntilTextAsync(
            "Orphaned files in '.pipelines':",
            timeout: TimeSpan.FromSeconds(30));
        await auto.WaitUntilTextAsync(
            "Regenerate with:",
            timeout: TimeSpan.FromSeconds(30));
        await auto.WaitUntilTextAsync(
            $"[{counter.Value} ERR:{CliExitCodes.PublishVerificationFailed}] $ ",
            timeout: TimeSpan.FromSeconds(30));
        counter.Increment();

        await auto.RunCommandAsync(
            "test \"$(cat checked-output/primary.txt)\" = stale && " +
            "test ! -e .configgen/config.json && " +
            "test \"$(cat .pipelines/orphan.yml)\" = orphaned",
            counter);
    }

    private static void WriteVerificationAppHost(TemporaryWorkspace workspace)
    {
        var appHostPath = Path.Combine(
            workspace.WorkspaceRoot.FullName,
            ProjectName,
            "apphost.cs");
        var sdkDirective = File.ReadLines(appHostPath)
            .First(line => line.StartsWith("#:sdk ", StringComparison.Ordinal));
        File.WriteAllText(appHostPath, $$"""
            {{sdkDirective}}

            #pragma warning disable ASPIREPIPELINES001
            #pragma warning disable ASPIREPIPELINES004

            using Aspire.Hosting.Pipelines;

            var builder = DistributedApplication.CreateBuilder(args);

            var configOutput = new PipelineOutputDefinition(
                "config",
                ".configgen",
                PipelineOutputKind.Directory);
            var pipelinesOutput = new PipelineOutputDefinition(
                "pipelines",
                ".pipelines",
                PipelineOutputKind.Directory);

            builder.Pipeline.AddStep(new PipelineStep
            {
                Name = "publish",
                Outputs = [configOutput, pipelinesOutput],
                SupportsOutputPathRelocation = true,
                Action = context =>
                {
                    WriteOutput(context.Outputs.PrimaryOutput, "primary.txt");
                    WriteOutput(context.Outputs.Resolve(configOutput), "config.json");
                    WriteOutput(context.Outputs.Resolve(pipelinesOutput), "build.yml");
                    return Task.CompletedTask;
                }
            });

            builder.Build().Run();

            static void WriteOutput(ResolvedPipelineOutput output, string fileName)
            {
                Directory.CreateDirectory(output.OutputPath);
                File.WriteAllText(
                    Path.Combine(output.OutputPath, fileName),
                    $"{output.OutputPath}\n{output.LogicalTargetPath}\n");
            }
            """);
    }
}
