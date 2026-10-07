// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using Xunit;

using IOPath = System.IO.Path;

namespace Aspire.Tests.Utils;

public sealed class TemporaryWorkspace(ITestOutputHelper outputHelper, DirectoryInfo workspaceDirectory) : IDisposable
{
    private static readonly ConcurrentDictionary<string, byte> s_preservedWorkspaces = new(StringComparer.Ordinal);

    private static readonly Lazy<DirectoryInfo> s_workspacesParent = new(InitializeWorkspacesParent);

    public DirectoryInfo WorkspaceRoot => workspaceDirectory;

    public string Path => workspaceDirectory.FullName;

    public DirectoryInfo CreateDirectory(string name)
    {
        return workspaceDirectory.CreateSubdirectory(name);
    }

    public async Task InitializeGitAsync(CancellationToken cancellationToken = default)
    {
        outputHelper.WriteLine($"Initializing git repository at: {workspaceDirectory.FullName}");

        await RunGitAsync(workspaceDirectory.FullName, outputHelper, ["init"], cancellationToken);
    }

    internal static async Task RunGitAsync(string workingDirectory, ITestOutputHelper outputHelper, string[] arguments, CancellationToken cancellationToken)
    {
        var command = $"git {string.Join(' ', arguments)}";
        var stopwatch = Stopwatch.StartNew();
        outputHelper.WriteLine($"[{DateTimeOffset.UtcNow:O}] Starting '{command}' in '{workingDirectory}'");

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, TestContext.Current.CancellationToken, timeout.Token);
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo("git", arguments)
            {
                WorkingDirectory = workingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };

        try
        {
            process.Start();
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 2 && Directory.Exists(workingDirectory))
        {
            outputHelper.WriteLine($"Failed to start git: {ex}");
            Assert.Skip("git is required for this test but was not found on PATH.");
        }

        outputHelper.WriteLine($"[{DateTimeOffset.UtcNow:O}] '{command}' started with PID {process.Id} after {stopwatch.Elapsed}");

        var stdout = new StringBuilder();
        var stderr = new StringBuilder();

        // Drain both pipes while Git runs: waiting for exit first can deadlock if either
        // pipe fills. Log each line immediately so output survives a timeout.
        var stdoutTask = ReadOutputAsync(process.StandardOutput, stdout, "stdout");
        var stderrTask = ReadOutputAsync(process.StandardError, stderr, "stderr");

        try
        {
            await Task.WhenAll(process.WaitForExitAsync(cancellation.Token), stdoutTask, stderrTask);
        }
        catch (OperationCanceledException)
        {
            outputHelper.WriteLine($"[{DateTimeOffset.UtcNow:O}] '{command}' (PID {process.Id}) {(timeout.IsCancellationRequested ? "timed out" : "was canceled")} after {stopwatch.Elapsed}");

            // Disposing Process does not stop it. Request tree termination and reap the root
            // before workspace disposal. HasExited can change between the check and Kill.
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch (Exception ex) when ((ex is InvalidOperationException or Win32Exception) && process.HasExited)
            {
                outputHelper.WriteLine($"[{DateTimeOffset.UtcNow:O}] '{command}' (PID {process.Id}) exited during termination: {ex.Message}");
            }

            // WaitForExitAsync observes only the root, not every descendant. Tree cleanup is
            // best-effort; strict containment would require process groups or Windows jobs.
            // https://learn.microsoft.com/dotnet/api/system.diagnostics.process.kill#remarks
            await process.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None);
            if (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested && !TestContext.Current.CancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException($"'{command}' in '{workingDirectory}' (PID {process.Id}) timed out after 30 seconds.");
            }

            throw;
        }
        finally
        {
            outputHelper.WriteLine($"[{DateTimeOffset.UtcNow:O}] '{command}' (PID {process.Id}) finished after {stopwatch.Elapsed}; exit code: {(process.HasExited ? process.ExitCode.ToString() : "still running")}");
        }

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"'{command}' in '{workingDirectory}' failed with exit code {process.ExitCode}. stdout: {stdout}, stderr: {stderr}");
        }

        async Task ReadOutputAsync(StreamReader reader, StringBuilder capturedOutput, string streamName)
        {
            while (await reader.ReadLineAsync(cancellation.Token) is { } line)
            {
                capturedOutput.AppendLine(line);
                outputHelper.WriteLine($"[{DateTimeOffset.UtcNow:O}] '{command}' (PID {process.Id}) {streamName}: {line}");
            }

            outputHelper.WriteLine($"[{DateTimeOffset.UtcNow:O}] '{command}' (PID {process.Id}) {streamName} closed");
        }
    }

    public void Dispose()
    {
        if (s_preservedWorkspaces.ContainsKey(workspaceDirectory.FullName))
        {
            outputHelper.WriteLine($"Preserved temporary workspace at: {workspaceDirectory.FullName}");
            return;
        }

        outputHelper.WriteLine($"Disposing temporary workspace at: {workspaceDirectory.FullName}");

        try
        {
            DeleteDirectoryWithRetries(workspaceDirectory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            outputHelper.WriteLine($"Failed to delete temporary workspace '{workspaceDirectory.FullName}': {ex.Message}");
        }
    }

    private static void DeleteDirectoryWithRetries(DirectoryInfo directory)
    {
        // On Windows, file handles held by disposed StreamWriters may not be
        // released instantly. Retry with backoff to handle transient locks.
        // On Linux/macOS, Delete(true) can partially succeed (remove the directory)
        // yet still throw IOException, so subsequent retries see DirectoryNotFoundException.
        const int maxRetries = 5;
        for (var i = 0; i < maxRetries; i++)
        {
            try
            {
                directory.Delete(true);
                return;
            }
            catch (DirectoryNotFoundException)
            {
                // Directory was already deleted (possibly by a previous attempt
                // that removed the directory but still threw). Nothing to clean up.
                return;
            }
            catch (Exception ex) when ((ex is IOException or UnauthorizedAccessException) && i < maxRetries - 1)
            {
                ResetReadOnlyAttributes(directory);
                Thread.Sleep(500 * (i + 1));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Bulk delete failed after all retries. Delete files individually
                // to surface the exact file name that is still locked.
                ResetReadOnlyAttributes(directory);
                DeleteContentsIndividually(directory);
                return;
            }
        }
    }

    private static void DeleteContentsIndividually(DirectoryInfo directory)
    {
        if (!directory.Exists)
        {
            return;
        }

        foreach (var child in directory.EnumerateDirectories())
        {
            DeleteContentsIndividually(child);
        }

        foreach (var file in directory.EnumerateFiles())
        {
            try
            {
                file.Attributes &= ~FileAttributes.ReadOnly;
                file.Delete();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new IOException($"Cannot delete '{file.FullName}': {ex.Message}", ex);
            }
        }

        try
        {
            directory.Attributes &= ~FileAttributes.ReadOnly;
            directory.Delete(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new IOException($"Cannot delete directory '{directory.FullName}': {ex.Message}", ex);
        }
    }

    private static void ResetReadOnlyAttributes(DirectoryInfo directory)
    {
        if (!OperatingSystem.IsWindows() || !directory.Exists)
        {
            return;
        }

        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = 0
        };

        foreach (var entry in directory.EnumerateFileSystemInfos("*", options))
        {
            TryResetReadOnlyAttribute(entry);
        }

        TryResetReadOnlyAttribute(directory);
    }

    private static void TryResetReadOnlyAttribute(FileSystemInfo entry)
    {
        try
        {
            entry.Attributes &= ~FileAttributes.ReadOnly;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort: deletion below will surface persistent locks or permission issues.
        }
    }

    public void Preserve()
    {
        s_preservedWorkspaces[workspaceDirectory.FullName] = 0;
        outputHelper.WriteLine($"Marked temporary workspace for preservation: {workspaceDirectory.FullName}");
    }

    public static void ReleasePreservation(string workspacePath, bool deleteDirectory = true)
    {
        if (!s_preservedWorkspaces.TryRemove(workspacePath, out _))
        {
            return;
        }

        if (!deleteDirectory || !Directory.Exists(workspacePath))
        {
            return;
        }

        try
        {
            DeleteDirectoryWithRetries(new DirectoryInfo(workspacePath));
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error deleting preserved temporary workspace '{workspacePath}': {ex.Message}");
        }
    }

    private static DirectoryInfo InitializeWorkspacesParent()
    {
        var tempPath = IOPath.GetTempPath();
        var parentDir = Directory.CreateDirectory(IOPath.Combine(tempPath, typeof(TemporaryWorkspace).Assembly.GetName().Name!, "Workspace"));

        return parentDir;
    }

    public static TemporaryWorkspace Create(ITestOutputHelper outputHelper)
    {
        var parentDir = s_workspacesParent.Value;
        var workspaceDirectory = parentDir.CreateSubdirectory(IOPath.GetRandomFileName());
        outputHelper.WriteLine($"Temporary workspace created at: {workspaceDirectory.FullName}");

        // CaptureWorkspaceOnFailure runs after method-local disposal. Preserve the workspace
        // immediately so disposal defers deletion until the attribute has copied or released it.
        TestContext.Current?.KeyValueStorage["WorkspacePath"] = workspaceDirectory.FullName;
        var workspace = new TemporaryWorkspace(outputHelper, workspaceDirectory);
        if (TestContext.Current?.KeyValueStorage.TryGetValue("PreserveWorkspaceOnFailure", out var preserveValue) == true &&
            preserveValue is true)
        {
            workspace.Preserve();
        }

        return workspace;
    }

    /// <summary>
    /// Creates a workspace with <c>.aspire/settings.json</c> so that directory-walking
    /// searches (ConfigurationHelper.GetConfigRootDirectory) resolve to this workspace
    /// rather than walking up to the user's actual ~/.aspire/settings.json.
    /// </summary>
    public static TemporaryWorkspace CreateForCli(ITestOutputHelper outputHelper)
    {
        var workspace = Create(outputHelper);

        var aspireDir = IOPath.Combine(workspace.Path, ".aspire");
        var settingsPath = IOPath.Combine(aspireDir, "settings.json");
        Directory.CreateDirectory(aspireDir);
        File.WriteAllText(settingsPath, "{}");

        return workspace;
    }
}
