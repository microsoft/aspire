// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.TestUtilities;
using Xunit;

namespace Infrastructure.Tests;

public sealed class StageCliStarterDiagnosticsTests(ITestOutputHelper output)
{
    [Fact]
    [RequiresTools(["pwsh"])]
    public async Task StagesProjectsAndLogsWithoutDependencies()
    {
        using var workspace = TemporaryWorkspace.Create(output);
        var source = Path.Combine(workspace.Path, "validation");
        var destination = Path.Combine(workspace.Path, "staged");
        var files = new Dictionary<string, string>
        {
            [Path.Combine("go", "diagnostics", "aspire-start.log")] = "start log",
            [Path.Combine("go", "project", "frontend", "package.json")] = "{}",
            [Path.Combine("go", "project", "node_modules", "package.js")] = "dependency",
            [Path.Combine("go", "project", "frontend", "node_modules", "nested", "package.js")] = "dependency",
            [Path.Combine("cli-logs", "cli.log")] = "cli log"
        };

        foreach (var (relativePath, content) in files)
        {
            var path = Path.Combine(source, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, content);
        }

        using var command = CreateCommand();
        (await command.ExecuteAsync("-Source", $"\"{source}\"", "-Destination", $"\"{destination}\"")).EnsureSuccessful();

        Assert.Equal(
            new[]
            {
                Path.Combine("cli-logs", "cli.log"),
                Path.Combine("go", "diagnostics", "aspire-start.log"),
                Path.Combine("go", "project", "frontend", "package.json")
            }.Order(StringComparer.Ordinal),
            Directory.GetFiles(destination, "*", SearchOption.AllDirectories)
                .Select(path => Path.GetRelativePath(destination, path))
                .Order(StringComparer.Ordinal));
        Assert.Equal("start log", await File.ReadAllTextAsync(Path.Combine(destination, "go", "diagnostics", "aspire-start.log")));
        Assert.Equal("dependency", await File.ReadAllTextAsync(Path.Combine(source, "go", "project", "node_modules", "package.js")));
    }

    [Fact]
    [RequiresTools(["pwsh"])]
    public async Task SkipsDependencyDirectoryWhoseTargetHasDisappeared()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var workspace = TemporaryWorkspace.Create(output);
        var source = Path.Combine(workspace.Path, "validation");
        var destination = Path.Combine(workspace.Path, "staged");
        Directory.CreateDirectory(source);
        Directory.CreateSymbolicLink(Path.Combine(source, "node_modules"), Path.Combine(workspace.Path, "removed"));
        await File.WriteAllTextAsync(Path.Combine(source, "project.json"), "{}");

        using var command = CreateCommand();
        (await command.ExecuteAsync("-Source", $"\"{source}\"", "-Destination", $"\"{destination}\"")).EnsureSuccessful();

        Assert.Equal(["project.json"], Directory.GetFiles(destination).Select(Path.GetFileName));
    }

    private PowerShellCommand CreateCommand() =>
        new PowerShellCommand(Path.Combine(RepoRoot.Path, "eng", "scripts", "stage-cli-starter-diagnostics.ps1"), output)
            .WithTimeout(TimeSpan.FromMinutes(1));
}
