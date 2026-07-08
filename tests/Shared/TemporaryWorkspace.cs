// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Concurrent;
using System.Diagnostics;
using Xunit;

using IOPath = System.IO.Path;

namespace Aspire.Tests.Utils;

public sealed class TemporaryWorkspace(ITestOutputHelper outputHelper, DirectoryInfo repoDirectory) : IDisposable
{
    private static readonly ConcurrentDictionary<string, byte> s_preservedWorkspaces = new(StringComparer.Ordinal);

    public DirectoryInfo WorkspaceRoot => repoDirectory;

    public string Path => repoDirectory.FullName;

    public DirectoryInfo CreateDirectory(string name)
    {
        return repoDirectory.CreateSubdirectory(name);
    }

    public async Task InitializeGitAsync(CancellationToken cancellationToken = default)
    {
        outputHelper.WriteLine($"Initializing git repository at: {repoDirectory.FullName}");

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "git",
                Arguments = "init",
                WorkingDirectory = repoDirectory.FullName,
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
        if (s_preservedWorkspaces.ContainsKey(repoDirectory.FullName))
        {
            outputHelper.WriteLine($"Preserved temporary workspace at: {repoDirectory.FullName}");
            return;
        }

        outputHelper.WriteLine($"Disposing temporary workspace at: {repoDirectory.FullName}");

        try
        {
            DeleteDirectoryWithRetries(repoDirectory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            outputHelper.WriteLine($"Failed to delete temporary workspace '{repoDirectory.FullName}': {ex.Message}");
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
        s_preservedWorkspaces[repoDirectory.FullName] = 0;
        outputHelper.WriteLine($"Marked temporary workspace for preservation: {repoDirectory.FullName}");
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

    public static TemporaryWorkspace Create(ITestOutputHelper outputHelper)
    {
        var tempPath = IOPath.GetTempPath();
        var parentDir = Directory.CreateDirectory(IOPath.Combine(tempPath, typeof(TemporaryWorkspace).Assembly.GetName().Name!, "Workspace"));
        var repoDirectory = parentDir.CreateSubdirectory(IOPath.GetRandomFileName());
        outputHelper.WriteLine($"Temporary workspace created at: {repoDirectory.FullName}");

        // Create an empty settings file so directory-walking searches
        // (ConfigurationHelper, ConfigurationService) stop here instead
        // of finding the user's actual ~/.aspire/settings.json.
        var aspireDir = Directory.CreateDirectory(IOPath.Combine(repoDirectory.FullName, ".aspire"));
        File.WriteAllText(IOPath.Combine(aspireDir.FullName, "settings.json"), "{}");

        // Register workspace path for CaptureWorkspaceOnFailure attribute
        TestContext.Current?.KeyValueStorage["WorkspacePath"] = repoDirectory.FullName;

        return new TemporaryWorkspace(outputHelper, repoDirectory);
    }
}
