// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.EndToEnd.Tests.Helpers;
using Hex1b.Automation;
using Xunit;

namespace Aspire.Cli.EndToEnd.Tests;

/// <summary>
/// End-to-end coverage for the build configuration used by .NET projects launched from a TypeScript AppHost.
/// </summary>
public sealed class TypeScriptDotnetProjectBuildConfigurationTests(ITestOutputHelper output)
{
    // Regression coverage for https://github.com/microsoft/aspire/issues/16444: the prebuilt AppHost server ships
    // as Release, and its assembly configuration used to be forwarded as --configuration Release to user projects.
    [Fact]
    [CaptureWorkspaceOnFailure]
    public async Task DotnetProjectFromTypeScriptAppHostDoesNotUseAppHostServerBuildConfiguration()
    {
        var repoRoot = CliE2ETestHelpers.GetRepoRoot();
        var strategy = CliInstallStrategy.Detect(output.WriteLine);

        if (strategy.Mode == CliInstallMode.InstallScript && strategy.Quality is null && strategy.Version is null)
        {
            Assert.Skip("This test validates unreleased AppHost server behavior. Build a local Aspire CLI bundle or run in CI so the test uses current PR bits instead of the GA CLI.");
        }

        using var workspace = TemporaryWorkspace.Create(output);
        var localChannel = CliE2ETestHelpers.PrepareLocalChannel(
            repoRoot,
            strategy,
            [
                "Aspire.Hosting.CodeGeneration.TypeScript.",
                "Aspire.Hosting.Dotnet."
            ]);

        using var terminal = CliE2ETestHelpers.CreateDockerTestTerminal(
            repoRoot,
            strategy,
            output,
            variant: CliE2ETestHelpers.DockerfileVariant.DotNet,
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
        await auto.RunCommandAsync(
            "aspire init --language typescript --non-interactive --suppress-agent-init",
            counter,
            TimeSpan.FromMinutes(2));

        if (localChannel is not null)
        {
            CliE2ETestHelpers.WriteLocalChannelSettings(workspace.WorkspaceRoot.FullName, localChannel.SdkVersion);
        }

        await auto.TypeAsync("aspire add Aspire.Hosting.Dotnet --non-interactive");
        await auto.EnterAsync();
        await auto.WaitForAspireAddCompletionAsync(counter, TimeSpan.FromMinutes(2));

        await auto.RunCommandAsync(
            "dotnet new web --name Api --output Api",
            counter,
            TimeSpan.FromMinutes(2));

        var appHostPath = Path.Combine(workspace.WorkspaceRoot.FullName, "apphost.mts");
        await File.WriteAllTextAsync(appHostPath, """
            import { createBuilder } from './.aspire/modules/aspire.mjs';

            const builder = await createBuilder();

            await builder.addDotnetProject('api', './Api/Api.csproj');

            await builder.build().run();
            """);

        // The project must run with the SDK default (Debug) rather than the AppHost server's Release configuration.
        // The project.configuration property is parsed from the dotnet launch arguments, and the bin output shows
        // which configuration was actually built.
        var verificationScriptPath = Path.Combine(workspace.WorkspaceRoot.FullName, "verify-build-configuration.sh");
        var verificationScript = """
            #!/usr/bin/env bash
            set -euo pipefail

            cleanup() {
                aspire stop --non-interactive >/dev/null 2>&1 || true
            }
            trap cleanup EXIT

            ASPIRE_CLI_START_TIMEOUT=180 aspire start --non-interactive --format json > aspire-start.json
            aspire wait api --status up --timeout 240
            aspire describe api --format json > api.json

            configuration=$(jq -r '.resources[0].properties["project.configuration"] // empty' api.json)
            echo "project.configuration='$configuration'"
            if [ -n "$configuration" ] && [ "$configuration" != "Debug" ]; then
                echo "Expected the project to use the default Debug configuration but it used '$configuration'."
                exit 1
            fi

            if [ -d Api/bin/Release ]; then
                echo "Api/bin/Release exists: the project was built with the AppHost server's Release configuration."
                exit 1
            fi

            if [ ! -d Api/bin/Debug ]; then
                echo "Api/bin/Debug does not exist: the project was not built with the default Debug configuration."
                exit 1
            fi

            echo "BUILD_CONFIGURATION_OK"
            """;
        await File.WriteAllTextAsync(verificationScriptPath, verificationScript.ReplaceLineEndings("\n"));

        try
        {
            await auto.RunCommandAsync(
                "bash verify-build-configuration.sh",
                counter,
                TimeSpan.FromMinutes(10));
        }
        catch (InvalidOperationException)
        {
            // RunCommandAsync observes the error prompt before throwing but leaves the sequence counter
            // unchanged. Advance it so TerminalRun can capture diagnostics from the next prompt.
            counter.Increment();
            throw;
        }
    }
}
