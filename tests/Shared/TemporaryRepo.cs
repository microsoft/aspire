// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Concurrent;
using System.Diagnostics;
using Xunit;

namespace Aspire.Cli.Tests.Utils;

internal sealed class TemporaryWorkspace(ITestOutputHelper outputHelper, DirectoryInfo repoDirectory) : IDisposable
{
    private static readonly ConcurrentDictionary<string, byte> s_preservedWorkspaces = new(StringComparer.Ordinal);

    public DirectoryInfo WorkspaceRoot => repoDirectory;

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
        catch (IOException ex)
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
            catch (IOException) when (i < maxRetries - 1)
            {
                Thread.Sleep(500 * (i + 1));
            }
            catch (IOException)
            {
                // Bulk delete failed after all retries. Delete files individually
                // to surface the exact file name that is still locked.
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
                file.Delete();
            }
            catch (IOException ex)
            {
                throw new IOException($"Cannot delete '{file.FullName}': {ex.Message}", ex);
            }
        }

        try
        {
            directory.Delete(false);
        }
        catch (IOException ex)
        {
            throw new IOException($"Cannot delete directory '{directory.FullName}': {ex.Message}", ex);
        }
    }

    internal void Preserve()
    {
        s_preservedWorkspaces[repoDirectory.FullName] = 0;
        outputHelper.WriteLine($"Marked temporary workspace for preservation: {repoDirectory.FullName}");
    }

    internal static void ReleasePreservation(string workspacePath, bool deleteDirectory = true)
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

    internal static TemporaryWorkspace Create(ITestOutputHelper outputHelper)
    {
        var tempPath = Path.GetTempPath();
        var path = Path.Combine(tempPath, "Aspire.Cli.Tests", "TemporaryWorkspaces", Guid.NewGuid().ToString());
        var repoDirectory = Directory.CreateDirectory(path);
        outputHelper.WriteLine($"Temporary workspace created at: {repoDirectory.FullName}");

        // Create an empty settings file so directory-walking searches
        // (ConfigurationHelper, ConfigurationService) stop here instead
        // of finding the user's actual ~/.aspire/settings.json.
        var aspireDir = Directory.CreateDirectory(Path.Combine(path, ".aspire"));
        File.WriteAllText(Path.Combine(aspireDir.FullName, "settings.json"), "{}");

        // Register workspace path for CaptureWorkspaceOnFailure attribute
        TestContext.Current?.KeyValueStorage["WorkspacePath"] = repoDirectory.FullName;

        return new TemporaryWorkspace(outputHelper, repoDirectory);
    }
}
