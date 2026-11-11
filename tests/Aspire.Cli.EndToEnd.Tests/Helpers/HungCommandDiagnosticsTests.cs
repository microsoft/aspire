// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Hex1b;
using Hex1b.Automation;
using Xunit;

namespace Aspire.Cli.EndToEnd.Tests.Helpers;

public class HungCommandDiagnosticsTests(ITestOutputHelper output)
{
    [Fact]
    public async Task SuspendHungCommandAndCaptureDiagnostics_CapturesHungProcessAndReturnsShell()
    {
        var workspace = Directory.CreateTempSubdirectory("aspire-hung-command-");
        try
        {
            using var terminal = Hex1bTerminal.CreateBuilder()
                .WithHeadless()
                .WithDimensions(160, 48)
                .WithPtyProcess("/bin/bash", ["--norc"])
                .Build();
            using var runCancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            var pendingRun = terminal.RunAsync(runCancellation.Token);
            var counter = new SequenceCounter();
            var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: TimeSpan.FromSeconds(30));

            // Typed input is buffered by the pty until bash reads it, so there is no need to wait for bash's default
            // prompt (which differs between root and non-root users).
            await auto.RunCommandAsync(AspireCliShellCommandHelpers.NumberedPromptSetupCommand, counter);
            await auto.RunCommandAsync($"cd {AspireCliShellCommandHelpers.QuoteBashArg(workspace.FullName)}", counter);

            // Stand-in for a CLI command that never returns. A fake ~/.aspire/logs entry proves the CLI logs are
            // copied even though the foreground command never gave the shell back.
            var home = Path.Combine(workspace.FullName, "home");
            Directory.CreateDirectory(Path.Combine(home, ".aspire", "logs"));
            await File.WriteAllTextAsync(Path.Combine(home, ".aspire", "logs", "cli_hung.log"), "msbuild -getProperty started", TestContext.Current.CancellationToken);
            await auto.RunCommandAsync($"export HOME={AspireCliShellCommandHelpers.QuoteBashArg(home)}", counter);

            // A pipeline (like `aspire start ... | tee ...`) stands in for a hung CLI command; every member must be
            // stopped. Wait until it owns the terminal so Ctrl+Z reaches it rather than bash's line editor.
            await auto.TypeAsync("sleep 3602 | sh -c 'echo hung-command-\"\"started; exec sleep 3601'");
            await auto.EnterAsync();
            await auto.WaitUntilTextAsync("hung-command-started", timeout: TimeSpan.FromSeconds(15));

            await auto.SuspendHungCommandAndCaptureDiagnosticsAsync(workspace.FullName);

            var diagnostics = Path.Combine(workspace.FullName, CliE2EAutomatorHelpers.DiagnosticsDirectoryName);
            var snapshot = await File.ReadAllTextAsync(Path.Combine(diagnostics, CliE2EAutomatorHelpers.HungCommandProcessesFileName), TestContext.Current.CancellationToken);
            output.WriteLine(snapshot);
            Assert.Contains("sleep 3601", snapshot);
            Assert.Contains("sleep 3602", snapshot);
            Assert.Equal("msbuild -getProperty started", await File.ReadAllTextAsync(Path.Combine(diagnostics, "logs", "cli_hung.log"), TestContext.Current.CancellationToken));

            // The shell must be usable again and the hung command must be gone so the terminal can exit.
            await auto.TypeAsync("echo \"sleep-processes:$(ps -eo args | grep -c '^sleep 360[12]$')\"");
            await auto.EnterAsync();
            await auto.WaitUntilTextAsync("sleep-processes:0", timeout: TimeSpan.FromSeconds(15));

            await auto.TypeAsync("exit");
            await auto.EnterAsync();
            await TerminalRun.WaitForExitAsync(pendingRun, runCancellation, TimeSpan.FromSeconds(15));
        }
        finally
        {
            workspace.Delete(recursive: true);
        }
    }
}
