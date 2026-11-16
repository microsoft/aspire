// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.TestUtilities;
using Xunit;

namespace Infrastructure.Tests;

/// <summary>
/// Tests for eng/scripts/stage-cli-starter-validation-diagnostics.ps1
/// </summary>
public sealed class StageCliStarterValidationDiagnosticsTests : IDisposable
{
    private readonly TemporaryWorkspace _workspace;
    private readonly string _scriptPath;
    private readonly ITestOutputHelper _output;

    public StageCliStarterValidationDiagnosticsTests(ITestOutputHelper output)
    {
        _output = output;
        _workspace = TemporaryWorkspace.Create(output);
        _scriptPath = Path.Combine(RepoRoot.Path, "eng", "scripts", "stage-cli-starter-validation-diagnostics.ps1");
    }

    public void Dispose() => _workspace.Dispose();

    [Fact]
    [RequiresTools(["pwsh"])]
    public async Task CopiesDiagnosticsWithoutNodeModules()
    {
        var sourceRoot = Path.Combine(_workspace.Path, "validation");
        var destinationRoot = Path.Combine(_workspace.Path, "upload");
        WriteFile(sourceRoot, "cli-logs/cli.log");
        WriteFile(sourceRoot, "aspire-go-starter/diagnostics/aspire-stop.log");
        WriteFile(sourceRoot, "aspire-go-starter/AspireCliGoStarterSmoke/apphost.go");
        WriteFile(sourceRoot, "aspire-go-starter/AspireCliGoStarterSmoke/frontend/package.json");
        WriteFile(sourceRoot, "aspire-go-starter/AspireCliGoStarterSmoke/frontend/node_modules/@babel/core/package.json");
        WriteFile(sourceRoot, "aspire-ts-starter/AspireCliTsStarterSmoke/node_modules/vite/package.json");
        WriteFile(destinationRoot, "stale.txt");

        var result = await RunScript(sourceRoot, destinationRoot);

        result.EnsureSuccessful();
        Assert.Equal(
            [
                "aspire-go-starter/AspireCliGoStarterSmoke/apphost.go",
                "aspire-go-starter/AspireCliGoStarterSmoke/frontend/package.json",
                "aspire-go-starter/diagnostics/aspire-stop.log",
                "cli-logs/cli.log"
            ],
            GetRelativeEntries(destinationRoot));
        Assert.Equal("cli-logs/cli.log", File.ReadAllText(Path.Combine(destinationRoot, "cli-logs", "cli.log")));
    }

    [Fact]
    [RequiresTools(["pwsh"])]
    public async Task SkipsEntriesThatNoLongerExistAndDirectoryLinks()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Creating symbolic links requires extra privileges on Windows.");

        var sourceRoot = Path.Combine(_workspace.Path, "validation");
        var destinationRoot = Path.Combine(_workspace.Path, "upload");
        var outsideDirectory = Path.Combine(_workspace.Path, "outside");
        WriteFile(sourceRoot, "aspire-ts-starter/diagnostics/aspire-stop.log");
        WriteFile(outsideDirectory, "secret.txt");

        // A dangling link reproduces an entry that is listed and then removed before it is copied.
        File.CreateSymbolicLink(
            Path.Combine(sourceRoot, "aspire-ts-starter", "removed.log"),
            Path.Combine(_workspace.Path, "missing.log"));
        Directory.CreateSymbolicLink(Path.Combine(sourceRoot, "aspire-ts-starter", "linked"), outsideDirectory);

        var result = await RunScript(sourceRoot, destinationRoot);

        result.EnsureSuccessful();
        Assert.Equal(["aspire-ts-starter/diagnostics/aspire-stop.log"], GetRelativeEntries(destinationRoot));
    }

    [Fact]
    [RequiresTools(["pwsh"])]
    public async Task MissingSourceLeavesNothingToUpload()
    {
        var destinationRoot = Path.Combine(_workspace.Path, "upload");
        WriteFile(destinationRoot, "stale.txt");

        var result = await RunScript(Path.Combine(_workspace.Path, "missing"), destinationRoot);

        result.EnsureSuccessful();
        Assert.False(Directory.Exists(destinationRoot));
    }

    private async Task<CommandResult> RunScript(string sourceRoot, string destinationRoot)
    {
        using var cmd = new PowerShellCommand(_scriptPath, _output)
            .WithTimeout(TimeSpan.FromMinutes(2));

        return await cmd.ExecuteAsync(
            "-SourceRoot", $"\"{sourceRoot}\"",
            "-DestinationRoot", $"\"{destinationRoot}\"");
    }

    private static void WriteFile(string root, string relativePath)
    {
        var path = Path.Combine(root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, relativePath);
    }

    private static string[] GetRelativeEntries(string root) =>
        Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/'))
            .Order(StringComparer.Ordinal)
            .ToArray();
}
