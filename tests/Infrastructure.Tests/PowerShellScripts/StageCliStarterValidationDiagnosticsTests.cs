// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Formats.Tar;
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
    [RequiresTools(["pwsh", "tar"])]
    public async Task ArchivesDiagnosticsWithoutNodeModulesOrHiddenFiles()
    {
        var sourceRoot = Path.Combine(_workspace.Path, "validation with spaces");
        var destinationRoot = Path.Combine(_workspace.Path, "upload with spaces");
        WriteFile(sourceRoot, "cli-logs/cli.log");
        WriteFile(sourceRoot, ".aspire/settings.json");
        WriteFile(sourceRoot, ".env");
        WriteFile(sourceRoot, "..settings");
        WriteFile(sourceRoot, "aspire-go-starter/AspireCliGoStarterSmoke/.aspire/settings.json");
        WriteFile(sourceRoot, "aspire-go-starter/AspireCliGoStarterSmoke/frontend/.env");
        WriteFile(sourceRoot, "aspire-go-starter/diagnostics/aspire-stop.log");
        WriteFile(sourceRoot, "aspire-go-starter/AspireCliGoStarterSmoke/apphost.go");
        WriteFile(sourceRoot, "aspire-go-starter/AspireCliGoStarterSmoke/frontend/package.json");
        WriteFile(sourceRoot, "aspire-go-starter/AspireCliGoStarterSmoke/frontend/node_modules/@babel/core/package.json");
        WriteFile(sourceRoot, "aspire-ts-starter/AspireCliTsStarterSmoke/node_modules/vite/package.json");
        WriteFile(destinationRoot, "starter-validation.tar");

        var result = await RunScript(sourceRoot, destinationRoot);

        result.EnsureSuccessful();
        Assert.Equal(
            [
                "aspire-go-starter/AspireCliGoStarterSmoke/apphost.go",
                "aspire-go-starter/AspireCliGoStarterSmoke/frontend/package.json",
                "aspire-go-starter/diagnostics/aspire-stop.log",
                "cli-logs/cli.log"
            ],
            ReadArchiveFiles(destinationRoot, sourceRoot));
        Assert.Equal(["starter-validation.tar"], GetRelativeEntries(destinationRoot));
    }

    [Fact]
    [RequiresTools(["pwsh", "tar"])]
    public async Task ArchivesLinksWithoutFollowingTargets()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Creating symbolic links requires extra privileges on Windows.");

        var sourceRoot = Path.Combine(_workspace.Path, "validation");
        var destinationRoot = Path.Combine(_workspace.Path, "upload");
        var outsideDirectory = Path.Combine(_workspace.Path, "outside");
        WriteFile(sourceRoot, "aspire-ts-starter/diagnostics/aspire-stop.log");
        WriteFile(outsideDirectory, "secret.txt");

        File.CreateSymbolicLink(
            Path.Combine(sourceRoot, "aspire-ts-starter", "removed.log"),
            Path.Combine(_workspace.Path, "missing.log"));
        Directory.CreateSymbolicLink(Path.Combine(sourceRoot, "aspire-ts-starter", "linked"), outsideDirectory);

        var result = await RunScript(sourceRoot, destinationRoot);

        result.EnsureSuccessful();
        Assert.Equal(["aspire-ts-starter/diagnostics/aspire-stop.log"], ReadArchiveFiles(destinationRoot, sourceRoot));
        using var archive = File.OpenRead(Path.Combine(destinationRoot, "starter-validation.tar"));
        using var reader = new TarReader(archive);
        List<string> links = [];
        while (reader.GetNextEntry() is { } entry)
        {
            if (entry.EntryType is TarEntryType.SymbolicLink)
            {
                links.Add(entry.Name);
            }
        }
        Assert.Equal(["validation/aspire-ts-starter/linked", "validation/aspire-ts-starter/removed.log"], links.Order(StringComparer.Ordinal));
    }

    [Fact]
    [RequiresTools(["pwsh"])]
    public async Task MissingSourceLeavesNothingToUpload()
    {
        var destinationRoot = Path.Combine(_workspace.Path, "upload");
        WriteFile(destinationRoot, "starter-validation.tar");
        WriteFile(destinationRoot, "starter-validation.tar.partial");

        var result = await RunScript(Path.Combine(_workspace.Path, "missing"), destinationRoot);

        result.EnsureSuccessful();
        Assert.Empty(Directory.EnumerateFileSystemEntries(destinationRoot));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [RequiresTools(["pwsh"])]
    public async Task ArchiveFailureLeavesNothingToUpload(int tarExitCode)
    {
        var sourceRoot = Path.Combine(_workspace.Path, "validation");
        var destinationRoot = Path.Combine(_workspace.Path, "upload");
        WriteFile(sourceRoot, "diagnostics/aspire-stop.log");
        WriteFile(destinationRoot, "starter-validation.tar");

        // A native command may leave partial output before failing. Exercise the production script
        // with that failure without relying on permissions, disk exhaustion, or a timed filesystem race.
        var wrapper = Path.Combine(_workspace.Path, "fail-tar.ps1");
        await File.WriteAllTextAsync(wrapper, """
            param($Script, $SourceRoot, $DestinationRoot, $TarExitCode)
            function tar
            {
                [System.IO.File]::WriteAllText((Join-Path $DestinationRoot 'starter-validation.tar.partial'), 'incomplete')
                $global:LASTEXITCODE = $TarExitCode
            }
            & $Script -SourceRoot $SourceRoot -DestinationRoot $DestinationRoot
            """);
        using var cmd = new PowerShellCommand(wrapper, _output).WithTimeout(TimeSpan.FromMinutes(2));
        var result = await cmd.ExecuteAsync(
            "-Script", $"\"{_scriptPath}\"",
            "-SourceRoot", $"\"{sourceRoot}\"",
            "-DestinationRoot", $"\"{destinationRoot}\"",
            "-TarExitCode", tarExitCode.ToString());

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains($"tar exit code {tarExitCode}", result.Output);
        Assert.Empty(Directory.EnumerateFileSystemEntries(destinationRoot));
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

    private static string[] ReadArchiveFiles(string root, string sourceRoot)
    {
        using var archive = File.OpenRead(Path.Combine(root, "starter-validation.tar"));
        using var reader = new TarReader(archive);
        List<string> files = [];
        while (reader.GetNextEntry() is { } entry)
        {
            if (entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile))
            {
                continue;
            }

            // Entries include the validation directory, for example "validation/cli-logs/cli.log".
            var prefix = Path.GetFileName(sourceRoot) + "/";
            Assert.StartsWith(prefix, entry.Name, StringComparison.Ordinal);
            var name = entry.Name[prefix.Length..];
            using var content = new StreamReader(entry.DataStream!);
            Assert.Equal(name, content.ReadToEnd());
            files.Add(name);
        }

        return files.Order(StringComparer.Ordinal).ToArray();
    }
}
