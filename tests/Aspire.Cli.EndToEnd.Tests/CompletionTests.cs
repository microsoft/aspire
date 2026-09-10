// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text;
using Aspire.Cli.EndToEnd.Tests.Helpers;
using Hex1b.Automation;
using Hex1b.Input;
using Xunit;

namespace Aspire.Cli.EndToEnd.Tests;

public sealed class CompletionTests(ITestOutputHelper output)
{
    [Fact]
    [CaptureWorkspaceOnFailure]
    public async Task PackagedCliCompletionDoesNotRunNormalStartup()
    {
        var repoRoot = CliE2ETestHelpers.GetRepoRoot();
        var strategy = CliInstallStrategy.Detect(output.WriteLine);
        var workspace = TemporaryWorkspace.Create(output);
        using var terminal = CliE2ETestHelpers.CreateDockerTestTerminal(repoRoot, strategy, output, workspace: workspace);
        var counter = new SequenceCounter();
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: TimeSpan.FromSeconds(500));
        await using var terminalRun = CliE2ETestHelpers.StartRun(terminal, workspace, auto, counter, output, TestContext.Current.CancellationToken);

        await auto.PrepareDockerEnvironmentAsync(counter, workspace);
        await auto.InstallAspireCliAsync(strategy, counter);

        // Copy the installed native binary without its route sidecar so ASPIRE_HOME selects
        // genuinely fresh state. This still executes the packaged binary through Main, not a
        // command object, and does not depend on the installer's existing first-use sentinel.
        var home = workspace.CreateDirectory("completion-home");
        const string legacyConfig = "{}";
        await File.WriteAllTextAsync(Path.Combine(home.FullName, "globalsettings.json"), legacyConfig);
        await auto.RunCommandAsync("mkdir -p completion-bin && cp \"$(command -v aspire)\" completion-bin/aspire && test -x completion-bin/aspire", counter);

        const string environment =
            "timeout --kill-after=5s 30s env ASPIRE_HOME=\"$PWD/completion-home\" " +
            "ASPIRE_CLI_TELEMETRY_OPTOUT=false ASPIRE_EXTENSION_ENDPOINT=127.0.0.1:1 ";
        await auto.RunCommandAsync(
            environment + "./completion-bin/aspire --banner --log-level Debug --cli-wait-for-debugger completions script bash > completion.bash 2> completion.stderr",
            counter, TimeSpan.FromSeconds(40));
        await auto.RunCommandAsync(
            environment + "./completion-bin/aspire '[suggest]' 'aspire comp' > suggestions.txt 2> suggestions.stderr",
            counter, TimeSpan.FromSeconds(40));

        // The generated output must be sourceable shell code with a working registration.
        // Unit snapshots cover its full text; this test checks the executable/packaging boundary.
        await auto.RunCommandAsync("bash -n completion.bash && source completion.bash && complete -p aspire > registration.txt", counter);

        Assert.Equal(string.Empty, await File.ReadAllTextAsync(Path.Combine(workspace.WorkspaceRoot.FullName, "completion.stderr")));
        Assert.Equal(string.Empty, await File.ReadAllTextAsync(Path.Combine(workspace.WorkspaceRoot.FullName, "suggestions.stderr")));
        Assert.Equal("completions\n", await File.ReadAllTextAsync(Path.Combine(workspace.WorkspaceRoot.FullName, "suggestions.txt")));
        Assert.Equal("complete -o default -F _aspire_complete aspire\n", await File.ReadAllTextAsync(Path.Combine(workspace.WorkspaceRoot.FullName, "registration.txt")));
        Assert.Equal(["globalsettings.json"], Directory.GetFiles(home.FullName, "*", SearchOption.AllDirectories).Select(Path.GetFileName));
        Assert.Empty(Directory.GetDirectories(home.FullName));
        Assert.Equal(legacyConfig, await File.ReadAllTextAsync(Path.Combine(home.FullName, "globalsettings.json")));
    }

    [Fact]
    [CaptureWorkspaceOnFailure]
    public async Task BashTabInsertionPreservesLiteralArguments()
    {
        var repoRoot = CliE2ETestHelpers.GetRepoRoot();
        var strategy = CliInstallStrategy.Detect(output.WriteLine);
        var workspace = TemporaryWorkspace.Create(output);
        using var terminal = CliE2ETestHelpers.CreateDockerTestTerminal(repoRoot, strategy, output, workspace: workspace);
        var counter = new SequenceCounter();
        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: TimeSpan.FromSeconds(500));
        await using var terminalRun = CliE2ETestHelpers.StartRun(terminal, workspace, auto, counter, output, TestContext.Current.CancellationToken);

        await auto.PrepareDockerEnvironmentAsync(counter, workspace);
        await auto.InstallAspireCliAsync(strategy, counter);
        await auto.RunCommandAsync("aspire completions script bash > completion.bash && source completion.bash", counter);

        // Only the completion candidate source is replaced. Bash runs the generated hook and
        // Readline performs real Tab insertion before the capture shim records the parsed argv.
        var fakeBin = workspace.CreateDirectory("completion-source");
        await File.WriteAllTextAsync(Path.Combine(fakeBin.FullName, "aspire"), ("""
            #!/bin/bash
            if [[ "$1" == '[suggest]' ]]; then
                printf '%s\n' "$COMPLETION_TEST_VALUE"
            else
                printf '%s\0' "$@" > completion-arguments.bin
            fi
            """ + "\n").ReplaceLineEndings("\n"));
        await auto.RunCommandAsync("chmod +x completion-source/aspire && export PATH=\"$PWD/completion-source:$PATH\"", counter);

        foreach (var candidate in new[] { "name with space", "name'quote", "name$(touch completion-executed)" })
        {
            await auto.RunCommandAsync($"export COMPLETION_TEST_VALUE={AspireCliShellCommandHelpers.QuoteBashArg(candidate)}", counter);
            await auto.TypeAsync("aspire na");
            await auto.KeyAsync(Hex1bKey.Tab);
            await auto.EnterAsync();
            await auto.WaitForSuccessPromptAsync(counter, TimeSpan.FromSeconds(30));

            Assert.Equal(Encoding.UTF8.GetBytes(candidate + "\0"),
                await File.ReadAllBytesAsync(Path.Combine(workspace.WorkspaceRoot.FullName, "completion-arguments.bin")));
            Assert.False(File.Exists(Path.Combine(workspace.WorkspaceRoot.FullName, "completion-executed")));
        }

        await auto.RunCommandAsync("export PATH=\"${PATH#\"$PWD/completion-source:\"}\"", counter);
    }
}
