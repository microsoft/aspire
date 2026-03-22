// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using Xunit;

namespace Aspire.Cli.Tests.Utils;

internal sealed class TemporaryWorkspace(ITestOutputHelper outputHelper, DirectoryInfo repoDirectory) : IDisposable
{
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
        try
        {
            repoDirectory.Delete(true);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error disposing TemporaryRepo: {ex.Message}");
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

        return new TemporaryWorkspace(outputHelper, repoDirectory);
    }
}
