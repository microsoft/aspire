// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.EndToEnd.Tests.Helpers;
using Aspire.Cli.Tests.Utils;
using Hex1b.Automation;
using Hex1b.Input;
using Xunit;

namespace Aspire.Cli.EndToEnd.Tests;

/// <summary>
/// End-to-end tests for Aspire terminal commands.
/// </summary>
public sealed class TerminalCommandTests(ITestOutputHelper output)
{
    [Fact]
    [CaptureWorkspaceOnFailure]
    public async Task TerminalAttachFrontend_ShowsViteHelpAndDetaches()
    {
        var repoRoot = CliE2ETestHelpers.GetRepoRoot();
        var strategy = CliInstallStrategy.Detect(output.WriteLine);
        var workspace = TemporaryWorkspace.Create(output);

        using var terminal = CliE2ETestHelpers.CreateDockerTestTerminal(repoRoot, strategy, output, mountDockerSocket: true, workspace: workspace);
        var counter = new SequenceCounter();
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: TimeSpan.FromSeconds(500));
        await using var terminalRun = CliE2ETestHelpers.StartRun(terminal, workspace, auto, counter, output, TestContext.Current.CancellationToken);

        await auto.PrepareDockerEnvironmentAsync(counter, workspace);
        await auto.InstallAspireCliAsync(strategy, counter);

        await auto.RunCommandAsync("aspire config set features.terminalCommandsEnabled true -g", counter);
        await auto.AspireNewAsync("TerminalSupportApp", counter, template: AspireTemplate.ExpressReact);

        var appHostPath = Path.Combine(workspace.WorkspaceRoot.FullName, "TerminalSupportApp", "apphost.mts");
        var appHostContent = File.ReadAllText(appHostPath);
        var oldFrontendLine = ".waitFor(app);";
        var newFrontendLine = """
            .waitFor(app)
                .withTerminal();
            """;

        if (!appHostContent.Contains(oldFrontendLine, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Could not find '{oldFrontendLine}' in {appHostPath}.");
        }

        File.WriteAllText(appHostPath, appHostContent.Replace(oldFrontendLine, newFrontendLine, StringComparison.Ordinal));

        await auto.TypeAsync("cd TerminalSupportApp");
        await auto.EnterAsync();
        await auto.WaitForSuccessPromptAsync(counter);

        await auto.AspireStartAsync(counter);

        // Keep calling `aspire terminal ps` until the frontend row is reported alive.
        await auto.RunCommandAsync(
            "for i in $(seq 1 120); do aspire terminal ps | grep -Eq 'frontend.*alive' && break; sleep 1; done; aspire terminal ps | grep -Eq 'frontend.*alive'",
            counter,
            TimeSpan.FromMinutes(3));

        await auto.ClearScreenAsync(counter);

        await auto.TypeAsync("aspire terminal attach frontend");
        await auto.EnterAsync();
        await auto.WaitUntilTextAsync("Ctrl+B D", timeout: TimeSpan.FromSeconds(30));
        await auto.WaitAsync(3000);

        await auto.TypeAsync("h");
        await auto.EnterAsync();
        await auto.WaitUntilAsync(
            s => s.ContainsText("Shortcuts") || s.ContainsText("press r + enter to restart the server"),
            timeout: TimeSpan.FromSeconds(30),
            description: "waiting for Vite help shortcuts");

        // Keep an explicit pause between chord keys to mirror interactive input cadence.
        await auto.Ctrl().KeyAsync(Hex1bKey.B);
        await auto.WaitAsync(1000);
        await auto.KeyAsync(Hex1bKey.D);
        await auto.WaitForSuccessPromptAsync(counter, timeout: TimeSpan.FromSeconds(30));

        await auto.AspireStopAsync(counter);
    }
}
