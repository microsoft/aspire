// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Tests.Utils;
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
    private readonly Hex1bTerminalAutomator _automator;
    private readonly SequenceCounter _counter;
    private readonly TemporaryWorkspace _workspace;
    private readonly ITestOutputHelper _output;

    internal TerminalRun(Task pendingRun, Hex1bTerminalAutomator automator, SequenceCounter counter, TemporaryWorkspace workspace, ITestOutputHelper output)
    {
        _pendingRun = pendingRun;
        _automator = automator;
        _counter = counter;
        _workspace = workspace;
        _output = output;
    }

    public async ValueTask DisposeAsync()
    {
        // Capture diagnostics (best effort)
        try
        {
            await _automator.CaptureAspireDiagnosticsAsync(_counter, _workspace);
        }
        catch
        {
            // Best effort diagnostics capture — don't mask the original test failure.
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

        // Wait for the terminal process to finish
        try
        {
            await _pendingRun;
        }
        catch
        {
            // Best effort — if the test body threw, we don't want to mask it.
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

        var destDir = GetDiagnosticsCapturePath(testName);
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

    private static string GetDiagnosticsCapturePath(string testName)
    {
        var githubWorkspace = Environment.GetEnvironmentVariable("GITHUB_WORKSPACE");

        if (!string.IsNullOrEmpty(githubWorkspace))
        {
            // CI environment — write to testresults/ so upload-artifact includes these files.
            return Path.Combine(githubWorkspace, "testresults", "workspaces", testName);
        }

        // Local development — keep diagnostics with other test output.
        return Path.Combine(AppContext.BaseDirectory, "TestResults", "workspaces", testName);
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
