// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using Aspire.Templates.Tests;
using Xunit;

namespace Infrastructure.Tests;

/// <summary>
/// Class fixture that builds the ExtractTestPartitions tool once before all tests run.
/// Individual tests can then use <c>dotnet run --no-build</c> for faster execution.
/// </summary>
public sealed class ExtractTestPartitionsFixture : IAsyncLifetime
{
    public string ToolProjectPath { get; private set; } = string.Empty;

    public async ValueTask InitializeAsync()
    {
        ToolProjectPath = GetToolProjectPath();

        // Build the tool once before all tests
        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = $"build \"{ToolProjectPath}\" --restore",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = new Process { StartInfo = psi };
        process.Start();

        var stdout = await process.StandardOutput.ReadToEndAsync();
        var stderr = await process.StandardError.ReadToEndAsync();

        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        await process.WaitForExitAsync(cts.Token);

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"Failed to build ExtractTestPartitions tool. Exit code: {process.ExitCode}\n" +
                $"stdout:\n{stdout}\n" +
                $"stderr:\n{stderr}");
        }
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static string GetToolProjectPath()
    {
        var repoRoot = TestUtils.RepoRoot;
        var projectPath = Path.Combine(repoRoot, "tools", "ExtractTestPartitions", "ExtractTestPartitions.csproj");

        if (!File.Exists(projectPath))
        {
            throw new InvalidOperationException(
                $"ExtractTestPartitions project not found at {projectPath}.");
        }

        return projectPath;
    }

}
