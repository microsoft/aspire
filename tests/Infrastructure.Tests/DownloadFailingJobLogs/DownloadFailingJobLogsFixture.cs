// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Xunit;

namespace Infrastructure.Tests;

/// <summary>
/// Locates the DownloadFailingJobLogs script and repository-local dotnet host.
/// </summary>
public sealed class DownloadFailingJobLogsFixture : IAsyncLifetime
{
    public string RepoRoot { get; private set; } = string.Empty;

    public string ToolPath { get; private set; } = string.Empty;

    public string DotNetPath { get; private set; } = string.Empty;

    public ValueTask InitializeAsync()
    {
        RepoRoot = FindRepoRoot();
        ToolPath = Path.Combine(RepoRoot, "tools", "scripts", "DownloadFailingJobLogs.cs");
        DotNetPath = Path.Combine(RepoRoot, "dotnet.sh");

        if (!File.Exists(ToolPath))
        {
            throw new InvalidOperationException($"DownloadFailingJobLogs script not found at {ToolPath}.");
        }

        if (!File.Exists(DotNetPath))
        {
            throw new InvalidOperationException($"dotnet.sh not found at {DotNetPath}.");
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

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
