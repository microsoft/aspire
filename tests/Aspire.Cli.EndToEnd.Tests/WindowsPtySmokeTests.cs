// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.EndToEnd.Tests.Helpers;
using Hex1b;
using Hex1b.Automation;
using Xunit;

namespace Aspire.Cli.EndToEnd.Tests;

/// <summary>
/// Minimal smoke test to validate that Hex1b's PTY proxy works on Windows.
/// This test launches pwsh.exe via Hex1b, types a command, and verifies the output.
/// No Aspire CLI, no prompt counting — just raw Hex1b on Windows.
/// </summary>
public sealed class WindowsPtySmokeTests(ITestOutputHelper output)
{
    [Fact]
    public async Task PwshTerminal_CanEchoAndExit()
    {
        if (!OperatingSystem.IsWindows())
        {
            output.WriteLine("Skipping: this test only runs on Windows.");
            return;
        }

        var recordingPath = CliE2ETestHelpers.GetTestResultsRecordingPath("PwshTerminal_CanEchoAndExit");

        using var terminal = Hex1bTerminal.CreateBuilder()
            .WithHeadless()
            .WithDimensions(160, 48)
            .WithAsciinemaRecording(recordingPath)
            .WithPtyProcess("pwsh.exe", ["-NoProfile", "-NoLogo"])
            .Build();

        output.WriteLine($"Recording: {recordingPath}");

        var pendingRun = terminal.RunAsync(TestContext.Current.CancellationToken);

        var auto = new Hex1bTerminalAutomator(terminal, defaultTimeout: TimeSpan.FromSeconds(30));

        // Wait for the initial PowerShell prompt
        await auto.WaitUntilAsync(
            snapshot => snapshot.GetScreenText().Contains("PS "),
            timeout: TimeSpan.FromSeconds(15),
            description: "initial pwsh prompt (PS )");

        output.WriteLine("Got initial pwsh prompt.");

        // Type a simple echo command
        await auto.TypeAsync("Write-Output 'hex1b-windows-works'");
        await auto.EnterAsync();

        // Wait for the output to appear
        await auto.WaitUntilAsync(
            snapshot => snapshot.GetScreenText().Contains("hex1b-windows-works"),
            timeout: TimeSpan.FromSeconds(10),
            description: "echo output (hex1b-windows-works)");

        output.WriteLine("Saw echo output.");

        // Exit cleanly
        await auto.TypeAsync("exit");
        await auto.EnterAsync();

        await pendingRun;

        output.WriteLine("pwsh terminal exited cleanly.");
    }
}
