// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Concurrent;
using System.Diagnostics;
using Xunit;

using IOPath = System.IO.Path;

namespace Aspire.Tests.Utils;

public sealed class TemporaryWorkspace(ITestOutputHelper outputHelper, DirectoryInfo workspaceDirectory) : IDisposable
{
    private static readonly ConcurrentDictionary<string, byte> s_preservedWorkspaces = new(StringComparer.Ordinal);

    // Lazily create the shared .aspire/settings.json in the parent Workspaces directory.
    // This sentinel stops directory-walking searches (ConfigurationHelper, ConfigurationService)
    // from finding the user's actual ~/.aspire/settings.json. Using Lazy<T> ensures thread
    // safety: two tests creating workspaces concurrently will never corrupt the file.
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

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "git",
                Arguments = "init",
                WorkingDirectory = workspaceDirectory.FullName,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };

        process.Start();
        await process.WaitForExitAsync(cancellationToken);

        if (process.ExitCode != 0)
        {
            var error = await process.StandardError.ReadToEndAsync(cancellationToken);
            throw new InvalidOperationException($"Failed to initialize git repository: {error}");
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

        // Remove stale aspire.config.json from previous test runs so it cannot
        // contaminate tests whose directory-walking searches escape their workspace.
        var staleConfig = IOPath.Combine(parentDir.FullName, "aspire.config.json");
        if (File.Exists(staleConfig))
        {
            File.Delete(staleConfig);
        }

        // Place a backstop .aspire/settings.json in the parent directory so that
        // directory-walking searches stop here rather than reaching the user's
        // home directory, even if a test deletes its own workspace sentinel.
        var backstopDir = IOPath.Combine(parentDir.FullName, ".aspire");
        var backstopSettings = IOPath.Combine(backstopDir, "settings.json");
        if (!File.Exists(backstopSettings))
        {
            Directory.CreateDirectory(backstopDir);
            File.WriteAllText(backstopSettings, "{}");
        }

        return parentDir;
    }

    public static TemporaryWorkspace Create(ITestOutputHelper outputHelper)
    {
        var parentDir = s_workspacesParent.Value;
        var workspaceDirectory = parentDir.CreateSubdirectory(IOPath.GetRandomFileName());
        outputHelper.WriteLine($"Temporary workspace created at: {workspaceDirectory.FullName}");

        // Register workspace path for CaptureWorkspaceOnFailure attribute
        TestContext.Current?.KeyValueStorage["WorkspacePath"] = workspaceDirectory.FullName;

        return new TemporaryWorkspace(outputHelper, workspaceDirectory);
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
