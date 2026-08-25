// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.EndToEnd.Tests.Helpers;
using Hex1b.Automation;
using Xunit;

namespace Aspire.Cli.EndToEnd.Tests;

/// <summary>
/// End-to-end coverage for Blazor gateway APIs invoked from a TypeScript AppHost.
/// </summary>
public sealed class TypeScriptBlazorGatewayTests(ITestOutputHelper output)
{
    [Fact]
    [CaptureWorkspaceOnFailure]
    public async Task DotnetProjectBlazorGatewayRunsFromTypeScriptAppHost()
    {
        var repoRoot = CliE2ETestHelpers.GetRepoRoot();
        var strategy = CliInstallStrategy.Detect(output.WriteLine);

        if (strategy.Mode == CliInstallMode.InstallScript && strategy.Quality is null && strategy.Version is null)
        {
            Assert.Skip("This test exercises unreleased Blazor polyglot APIs. Build a local Aspire CLI bundle or run in CI so the test uses current PR bits instead of the GA CLI.");
        }

        using var workspace = TemporaryWorkspace.Create(output);
        var localChannel = CliE2ETestHelpers.PrepareLocalChannel(
            repoRoot,
            strategy,
            [
                "Aspire.Hosting.Blazor.",
                "Aspire.Hosting.CodeGeneration.TypeScript.",
                "Aspire.Hosting.Dotnet."
            ]);

        // The gateway is a file-based .NET app, so this scenario needs both Node.js for the
        // TypeScript AppHost and the .NET SDK for the gateway and Blazor client builds.
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

        await auto.TypeAsync("aspire add Aspire.Hosting.Blazor --non-interactive");
        await auto.EnterAsync();
        await auto.WaitForAspireAddCompletionAsync(counter, TimeSpan.FromMinutes(2));

        await auto.RunCommandAsync(
            "dotnet new blazorwasm --name Client --output Client --no-restore",
            counter,
            TimeSpan.FromMinutes(2));

        var appHostPath = Path.Combine(workspace.WorkspaceRoot.FullName, "apphost.mts");
        await File.WriteAllTextAsync(appHostPath, """
            import { createBuilder } from './.aspire/modules/aspire.mjs';

            const builder = await createBuilder();

            const client = await builder.addBlazorWasmProject('client', './Client/Client.csproj');
            const gateway = await builder.addDotnetProjectBlazorGateway('gateway');
            await gateway.withBlazorClientApp(client);

            await builder.build().run();
            """);

        var verificationScriptPath = Path.Combine(workspace.WorkspaceRoot.FullName, "verify-blazor-gateway.sh");
        var verificationScript = """
            #!/usr/bin/env bash
            set -euo pipefail

            cleanup() {
                aspire stop --non-interactive >/dev/null 2>&1 || true
            }
            trap cleanup EXIT

            ASPIRE_CLI_START_TIMEOUT=180 aspire start --non-interactive --format json > aspire-start.json
            aspire wait gateway --status up --timeout 240

            found=false
            for i in $(seq 1 20); do
                if ! aspire describe gateway --format json > gateway.json; then
                    sleep 2
                    continue
                fi

                if ! GATEWAY_URL=$(jq -r '[.resources[0].urls[]? | select(.name == "http" and .isInternal != true) | .url][0] // empty' gateway.json); then
                    sleep 2
                    continue
                fi

                if [ -n "$GATEWAY_URL" ] &&
                   curl --connect-timeout 2 --max-time 5 -fsS "$GATEWAY_URL/client/" -o client-index.html &&
                   grep -q '<title>Client</title>' client-index.html &&
                   grep -q '_framework/blazor.webassembly' client-index.html; then
                    found=true
                    break
                fi

                sleep 2
            done

            $found
            """;
        await File.WriteAllTextAsync(verificationScriptPath, verificationScript.ReplaceLineEndings("\n"));

        try
        {
            await auto.RunCommandAsync(
                "bash verify-blazor-gateway.sh",
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
