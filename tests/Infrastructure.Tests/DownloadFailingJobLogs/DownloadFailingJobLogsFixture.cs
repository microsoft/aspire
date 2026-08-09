// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Xunit;

namespace Infrastructure.Tests;

/// <summary>
/// Locates the DownloadFailingJobLogs script and repository-local dotnet host.
/// </summary>
public sealed class DownloadFailingJobLogsFixture : IAsyncLifetime
{
    public string ToolPath { get; private set; } = string.Empty;

    public string DotNetPath { get; private set; } = string.Empty;

    public ValueTask InitializeAsync()
    {
        ToolPath = Path.Combine(RepoRoot.Path, "tools", "scripts", "DownloadFailingJobLogs.cs");
        DotNetPath = Path.Combine(RepoRoot.Path, "dotnet.sh");

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
}
