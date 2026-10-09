// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Hex1b.Automation;
using Xunit;

namespace Aspire.Cli.EndToEnd.Tests.Helpers;

/// <summary>
/// Wraps a terminal run session and ensures diagnostics are captured and the terminal is properly
/// exited on disposal. Use via <see cref="CliE2ETestHelpers.StartRun"/> to consistently capture
/// diagnostics at the end of every CLI E2E test.
/// </summary>
internal sealed class TerminalRun : IAsyncDisposable
{
    private readonly Task _pendingRun;
    private readonly CancellationTokenSource _runCancellation;
    private readonly Hex1bTerminalAutomator _automator;
    private readonly SequenceCounter _counter;
    private readonly TemporaryWorkspace _workspace;
    private readonly ITestOutputHelper _output;

    internal TerminalRun(Task pendingRun, CancellationTokenSource runCancellation, Hex1bTerminalAutomator automator, SequenceCounter counter, TemporaryWorkspace workspace, ITestOutputHelper output)
    {
        _pendingRun = pendingRun;
        _runCancellation = runCancellation;
        _automator = automator;
        _counter = counter;
        _workspace = workspace;
        _output = output;
    }

    public async ValueTask DisposeAsync()
    {
        using var runCancellation = _runCancellation;

        // Capture diagnostics (best effort). The helper bounds its prompt wait; awaiting it directly
        // avoids abandoning an automator operation that could overlap with the exit input below.
        try
        {
            await _automator.CaptureAspireDiagnosticsAsync(_counter, _workspace);
        }
        catch (Exception ex)
        {
            WriteTestOutput($"[TerminalRun] Could not capture diagnostics before shutdown: {ex.Message}");
        }

        // Exit the terminal (best effort)
        try
        {
            await _automator.TypeAsync("exit");
            await _automator.EnterAsync();
        }
        catch
        {
            // Best effort exit — the terminal may already be closed.
        }

        // A timed-out command can leave the shell busy, so the queued exit may never execute.
        // Cancel the terminal run after a grace period rather than hiding the original test failure.
        try
        {
            await WaitForExitAsync(_pendingRun, runCancellation, TimeSpan.FromSeconds(15));
        }
        catch (Exception ex)
        {
            WriteTestOutput($"[TerminalRun] Terminal shutdown did not complete normally: {ex.Message}");
        }

        // Copy workspace diagnostics to the host-side testresults directory so they appear
        // in CI artifacts. The in-Docker capture (CaptureAspireDiagnosticsAsync / EXIT trap)
        // writes files to the workspace volume mount, but that temp directory is not in the
        // CI-uploaded testresults/ path. This step bridges that gap.
        try
        {
            CaptureWorkspaceDiagnosticsToTestResults();
        }
        catch
        {
            // Best effort — don't mask the original test failure.
        }
    }

    internal static async Task WaitForExitAsync(Task pendingRun, CancellationTokenSource runCancellation, TimeSpan timeout)
    {
        try
        {
            await pendingRun.WaitAsync(timeout);
        }
        catch (TimeoutException)
        {
            runCancellation.Cancel();

            try
            {
                await pendingRun.WaitAsync(timeout);
            }
            catch (OperationCanceledException) when (runCancellation.IsCancellationRequested)
            {
            }
            catch (TimeoutException ex)
            {
                throw new TimeoutException(
                    "The terminal did not exit after cancellation. Stopped waiting so the original test failure can be reported. " +
                    "Check the terminal recording for the command that stopped making progress.",
                    ex);
            }
        }
    }

    /// <summary>
    /// Copies the diagnostics directory from the workspace temp directory to the testresults path
    /// that CI uploads as artifacts. The in-Docker capture writes everything under a single
    /// <see cref="CliE2EAutomatorHelpers.DiagnosticsDirectoryName"/> subdirectory, so the host
    /// side only needs to copy that one directory.
    /// </summary>
    private void CaptureWorkspaceDiagnosticsToTestResults()
    {
        var diagnosticsSource = Path.Combine(_workspace.WorkspaceRoot.FullName, CliE2EAutomatorHelpers.DiagnosticsDirectoryName);
        if (!Directory.Exists(diagnosticsSource))
        {
            WriteTestOutput($"[TerminalRun] No diagnostics directory found at: {diagnosticsSource}");
            return;
        }

        var testName = TestContext.Current?.TestCase is { TestMethodName: { } methodName }
            ? methodName
            : "unknown";

        var destDir = CliE2ETestHelpers.GetCaptureRootDirectory(testName);
        CopyDirectoryIfExists(diagnosticsSource, destDir);

        WriteTestOutput($"[TerminalRun] Captured diagnostics to: {destDir}");
        WriteTestOutput($"[TerminalRun]   Source workspace: {_workspace.WorkspaceRoot.FullName}");

        // Report file counts per subdirectory so CI logs show what was actually captured.
        foreach (var subDir in Directory.GetDirectories(destDir))
        {
            var fileCount = Directory.GetFiles(subDir, "*", SearchOption.AllDirectories).Length;
            WriteTestOutput($"[TerminalRun]   {Path.GetFileName(subDir)}/: {fileCount} file(s)");
        }

        // Count top-level files (e.g. aspire-start.json)
        var topLevelFiles = Directory.GetFiles(destDir);
        if (topLevelFiles.Length > 0)
        {
            WriteTestOutput($"[TerminalRun]   (root): {topLevelFiles.Length} file(s)");
        }
    }

    private static void CopyDirectoryIfExists(string source, string destination)
    {
        if (!Directory.Exists(source))
        {
            return;
        }

        Directory.CreateDirectory(destination);

        foreach (var file in Directory.GetFiles(source))
        {
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), overwrite: true);
        }

        foreach (var dir in Directory.GetDirectories(source))
        {
            CopyDirectoryIfExists(dir, Path.Combine(destination, Path.GetFileName(dir)));
        }
    }

    private void WriteTestOutput(string message)
    {
        _output.WriteLine(message);
    }
}
