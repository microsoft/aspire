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

    internal TerminalRun(Task pendingRun, Hex1bTerminalAutomator automator, SequenceCounter counter, TemporaryWorkspace workspace)
    {
        _pendingRun = pendingRun;
        _automator = automator;
        _counter = counter;
        _workspace = workspace;
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
    /// Copies diagnostic files from the workspace temp directory to the testresults path
    /// that CI uploads as artifacts. This makes CLI logs, DCP logs, and other diagnostics
    /// available in the CI artifact download regardless of test pass/fail status.
    /// </summary>
    private void CaptureWorkspaceDiagnosticsToTestResults()
    {
        var workspacePath = _workspace.WorkspaceRoot.FullName;
        if (!Directory.Exists(workspacePath))
        {
            return;
        }

        var testName = TestContext.Current?.TestCase is { } testCase
            ? $"{testCase.TestClassName}.{testCase.TestMethodName}"
            : "unknown";

        var destDir = GetDiagnosticsCapturePath(testName);
        Directory.CreateDirectory(destDir);

        // Copy diagnostic directories produced by BuildAspireDiagnosticsCaptureCommand
        CopyDirectoryIfExists(Path.Combine(workspacePath, ".aspire-logs"), Path.Combine(destDir, "_aspire-logs"));
        CopyDirectoryIfExists(Path.Combine(workspacePath, ".aspire-dcp-logs"), Path.Combine(destDir, "_aspire-dcp-logs"));
        CopyDirectoryIfExists(Path.Combine(workspacePath, ".aspire-packages"), Path.Combine(destDir, "_aspire-packages"));

        // Copy diagnostic files
        CopyFileIfExists(Path.Combine(workspacePath, "_aspire-start.json"), Path.Combine(destDir, "_aspire-start.json"));
        CopyFileIfExists(Path.Combine(workspacePath, "_aspire-detach.log"), Path.Combine(destDir, "_aspire-detach.log"));
        CopyFileIfExists(Path.Combine(workspacePath, "_aspire-cli.log"), Path.Combine(destDir, "_aspire-cli.log"));
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

    private static void CopyFileIfExists(string source, string destination)
    {
        if (File.Exists(source))
        {
            File.Copy(source, destination, overwrite: true);
        }
    }
}
