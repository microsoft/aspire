// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using Xunit;

namespace Infrastructure.Tests;

/// <summary>
/// Builds the CreateFailingTestIssue tool once before tests run.
/// </summary>
public sealed class CreateFailingTestIssueFixture : IAsyncLifetime
{
    public string RepoRoot { get; private set; } = string.Empty;

    public string ToolProjectPath { get; private set; } = string.Empty;

    public async ValueTask InitializeAsync()
    {
        RepoRoot = FindRepoRoot();
        ToolProjectPath = Path.Combine(RepoRoot, "tools", "CreateFailingTestIssue", "CreateFailingTestIssue.csproj");

        if (!File.Exists(ToolProjectPath))
        {
            throw new InvalidOperationException($"CreateFailingTestIssue project not found at {ToolProjectPath}.");
        }

        await BuildToolAsync(ToolProjectPath).ConfigureAwait(false);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static async Task BuildToolAsync(string toolProjectPath)
    {
        ProcessStartInfo processStartInfo = new()
        {
            FileName = "dotnet",
            Arguments = $"build \"{toolProjectPath}\" --restore",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = new Process { StartInfo = processStartInfo };
        process.Start();

        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();

        using var cancellationTokenSource = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        try
        {
            await process.WaitForExitAsync(cancellationTokenSource.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw;
        }

        var stdout = await stdoutTask.ConfigureAwait(false);
        var stderr = await stderrTask.ConfigureAwait(false);

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"Failed to build CreateFailingTestIssue tool. Exit code: {process.ExitCode}{Environment.NewLine}" +
                $"stdout:{Environment.NewLine}{stdout}{Environment.NewLine}" +
                $"stderr:{Environment.NewLine}{stderr}");
        }
    }

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Aspire.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("Could not find repository root (looking for Aspire.slnx).");
    }
}
