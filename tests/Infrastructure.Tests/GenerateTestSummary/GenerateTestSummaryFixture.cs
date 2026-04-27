// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using Aspire.Templates.Tests;
using Xunit;

namespace Infrastructure.Tests;

/// <summary>
/// Builds the GenerateTestSummary tool once before tests run.
/// </summary>
public sealed class GenerateTestSummaryFixture : IAsyncLifetime
{
    public string ToolProjectPath { get; private set; } = string.Empty;

    public async ValueTask InitializeAsync()
    {
        var repoRoot = TestUtils.RepoRoot;
        ToolProjectPath = Path.Combine(repoRoot, "tools", "GenerateTestSummary", "GenerateTestSummary.csproj");

        if (!File.Exists(ToolProjectPath))
        {
            throw new InvalidOperationException($"GenerateTestSummary project not found at {ToolProjectPath}.");
        }

        ProcessStartInfo processStartInfo = new()
        {
            FileName = "dotnet",
            Arguments = $"build \"{ToolProjectPath}\" --restore",
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
                $"Failed to build GenerateTestSummary tool. Exit code: {process.ExitCode}{Environment.NewLine}" +
                $"stdout:{Environment.NewLine}{stdout}{Environment.NewLine}" +
                $"stderr:{Environment.NewLine}{stderr}");
        }
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

}
