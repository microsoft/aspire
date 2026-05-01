// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.TestUtilities;
using Xunit;

namespace Infrastructure.Tests;

public class SanitizeArtifactPathsTests : IDisposable
{
    private static readonly char[] s_invalidArtifactFileNameCharacters = ['"', ':', '<', '>', '|', '*', '?', '\r', '\n'];

    private readonly TestTempDirectory _tempDir = new();
    private readonly string _scriptPath;
    private readonly ITestOutputHelper _output;

    public SanitizeArtifactPathsTests(ITestOutputHelper output)
    {
        _output = output;
        _scriptPath = Path.Combine(FindRepoRoot(), "eng", "scripts", "sanitize-artifact-paths.ps1");
    }

    public void Dispose() => _tempDir.Dispose();

    [Fact]
    [RequiresTools(["pwsh"])]
    public async Task RenamesArtifactPathsWithCharactersRejectedByUploadArtifact()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var testResultsPath = Directory.CreateDirectory(Path.Combine(_tempDir.Path, "testresults"));
        var nestedPath = Directory.CreateDirectory(Path.Combine(testResultsPath.FullName, "Verify snapshot mismatch: repo_path"));
        var invalidFilePath = Path.Combine(nestedPath.FullName, "received:snapshot?.bin");
        await File.WriteAllTextAsync(invalidFilePath, "snapshot contents");

        using var cmd = new PowerShellCommand(_scriptPath, _output)
            .WithTimeout(TimeSpan.FromSeconds(30));

        var result = await cmd.ExecuteAsync($"\"{testResultsPath.FullName}\"");

        result.EnsureSuccessful();
        Assert.DoesNotContain(
            Directory.EnumerateFileSystemEntries(testResultsPath.FullName, "*", SearchOption.AllDirectories).Select(Path.GetFileName),
            name => name is not null && name.IndexOfAny(s_invalidArtifactFileNameCharacters) >= 0);
        var renamedFile = Assert.Single(Directory.EnumerateFiles(testResultsPath.FullName, "*.bin", SearchOption.AllDirectories));
        Assert.Equal("snapshot contents", await File.ReadAllTextAsync(renamedFile));
    }

    [Fact]
    [RequiresTools(["pwsh"])]
    public async Task AddsSuffixWhenSanitizedArtifactPathAlreadyExists()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var testResultsPath = Directory.CreateDirectory(Path.Combine(_tempDir.Path, "testresults"));
        await File.WriteAllTextAsync(Path.Combine(testResultsPath.FullName, "received_snapshot.bin"), "existing contents");
        await File.WriteAllTextAsync(Path.Combine(testResultsPath.FullName, "received:snapshot.bin"), "renamed contents");

        using var cmd = new PowerShellCommand(_scriptPath, _output)
            .WithTimeout(TimeSpan.FromSeconds(30));

        var result = await cmd.ExecuteAsync($"\"{testResultsPath.FullName}\"");

        result.EnsureSuccessful();
        Assert.Equal("existing contents", await File.ReadAllTextAsync(Path.Combine(testResultsPath.FullName, "received_snapshot.bin")));
        Assert.Equal("renamed contents", await File.ReadAllTextAsync(Path.Combine(testResultsPath.FullName, "received_snapshot-2.bin")));
    }

    [Fact]
    [RequiresTools(["pwsh"])]
    public async Task SanitizesMultipleArtifactRoots()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var testResultsPath = Directory.CreateDirectory(Path.Combine(_tempDir.Path, "testresults"));
        var testLogsPath = Directory.CreateDirectory(Path.Combine(_tempDir.Path, "test-logs"));
        await File.WriteAllTextAsync(Path.Combine(testResultsPath.FullName, "test:result.txt"), "test result");
        await File.WriteAllTextAsync(Path.Combine(testLogsPath.FullName, "test:log.txt"), "test log");

        using var cmd = new PowerShellCommand(_scriptPath, _output)
            .WithTimeout(TimeSpan.FromSeconds(30));

        var result = await cmd.ExecuteAsync($"\"{testResultsPath.FullName}\"", $"\"{testLogsPath.FullName}\"");

        result.EnsureSuccessful();
        Assert.Equal("test result", await File.ReadAllTextAsync(Path.Combine(testResultsPath.FullName, "test_result.txt")));
        Assert.Equal("test log", await File.ReadAllTextAsync(Path.Combine(testLogsPath.FullName, "test_log.txt")));
    }

    [Fact]
    [RequiresTools(["pwsh"])]
    public async Task IgnoresMissingArtifactPaths()
    {
        using var cmd = new PowerShellCommand(_scriptPath, _output)
            .WithTimeout(TimeSpan.FromSeconds(30));

        var result = await cmd.ExecuteAsync($"\"{Path.Combine(_tempDir.Path, "missing")}\"");

        result.EnsureSuccessful();
        Assert.Contains("does not exist; skipping", result.Output);
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Aspire.slnx")))
            {
                return dir.FullName;
            }
            dir = dir.Parent;
        }
        throw new InvalidOperationException("Could not find repository root");
    }
}
