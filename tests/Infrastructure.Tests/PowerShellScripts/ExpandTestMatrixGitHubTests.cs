// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;
using Aspire.TestUtilities;
using Xunit;

namespace Infrastructure.Tests;

/// <summary>
/// Tests for eng/scripts/expand-test-matrix-github.ps1
/// </summary>
public class ExpandTestMatrixGitHubTests : IDisposable
{
    private readonly TestTempDirectory _tempDir = new();
    private readonly string _scriptPath;
    private readonly ITestOutputHelper _output;

    public ExpandTestMatrixGitHubTests(ITestOutputHelper output)
    {
        _output = output;
        _scriptPath = Path.Combine(FindRepoRoot(), "eng", "scripts", "expand-test-matrix-github.ps1");
    }

    public void Dispose() => _tempDir.Dispose();

    [Fact]
    [RequiresTools(["pwsh"])]
    public async Task MapsWindowsToRunner()
    {
        // Arrange
        var entry = TestDataBuilder.CreateMatrixEntry(
            name: "TestProject",
            projectName: "TestProject",
            testProjectPath: "tests/TestProject/TestProject.csproj",
            supportedOSes: ["windows"]);

        var canonicalMatrix = Path.Combine(_tempDir.Path, "canonical.json");
        TestDataBuilder.CreateCanonicalMatrixJson(canonicalMatrix, tests: [entry]);

        var outputFile = Path.Combine(_tempDir.Path, "expanded.json");

        // Act
        var result = await RunScript(canonicalMatrix, outputMatrixFile: outputFile);

        // Assert
        result.EnsureSuccessful("expand-test-matrix-github.ps1 failed");

        var expanded = ParseGitHubMatrix(outputFile);
        Assert.Single(expanded.Include);
        Assert.Equal("windows-latest", expanded.Include[0].RunsOn);
    }

    [Fact]
    [RequiresTools(["pwsh"])]
    public async Task MapsLinuxToRunner()
    {
        // Arrange
        var entry = TestDataBuilder.CreateMatrixEntry(
            name: "TestProject",
            projectName: "TestProject",
            testProjectPath: "tests/TestProject/TestProject.csproj",
            supportedOSes: ["linux"]);

        var canonicalMatrix = Path.Combine(_tempDir.Path, "canonical.json");
        TestDataBuilder.CreateCanonicalMatrixJson(canonicalMatrix, tests: [entry]);

        var outputFile = Path.Combine(_tempDir.Path, "expanded.json");

        // Act
        var result = await RunScript(canonicalMatrix, outputMatrixFile: outputFile);

        // Assert
        result.EnsureSuccessful();

        var expanded = ParseGitHubMatrix(outputFile);
        Assert.Single(expanded.Include);
        Assert.Equal("ubuntu-latest", expanded.Include[0].RunsOn);
    }

    [Fact]
    [RequiresTools(["pwsh"])]
    public async Task MapsMacOSToRunner()
    {
        var entry = TestDataBuilder.CreateMatrixEntry(
            name: "TestProject",
            projectName: "TestProject",
            testProjectPath: "tests/TestProject/TestProject.csproj",
            supportedOSes: ["macos"]);

        var canonicalMatrix = Path.Combine(_tempDir.Path, "canonical.json");
        TestDataBuilder.CreateCanonicalMatrixJson(canonicalMatrix, tests: [entry]);

        var outputFile = Path.Combine(_tempDir.Path, "expanded.json");

        var result = await RunScript(canonicalMatrix, outputMatrixFile: outputFile);

        result.EnsureSuccessful();

        var expanded = ParseGitHubMatrix(outputFile);
        Assert.Single(expanded.Include);
        Assert.Equal("macos-latest", expanded.Include[0].RunsOn);
    }

    [Fact]
    [RequiresTools(["pwsh"])]
    public async Task ExpandsMultipleOSes()
    {
        var entry = TestDataBuilder.CreateMatrixEntry(
            name: "MultiOSProject",
            projectName: "MultiOSProject",
            testProjectPath: "tests/MultiOSProject/MultiOSProject.csproj",
            supportedOSes: ["windows", "linux", "macos"]);

        var canonicalMatrix = Path.Combine(_tempDir.Path, "canonical.json");
        TestDataBuilder.CreateCanonicalMatrixJson(canonicalMatrix, tests: [entry]);

        var outputFile = Path.Combine(_tempDir.Path, "expanded.json");

        var result = await RunScript(canonicalMatrix, outputMatrixFile: outputFile);

        result.EnsureSuccessful();

        var expanded = ParseGitHubMatrix(outputFile);
        Assert.Equal(3, expanded.Include.Length);
        Assert.Contains(expanded.Include, e => e.RunsOn == "windows-latest");
        Assert.Contains(expanded.Include, e => e.RunsOn == "ubuntu-latest");
        Assert.Contains(expanded.Include, e => e.RunsOn == "macos-latest");
    }

    [Fact]
    [RequiresTools(["pwsh"])]
    public async Task GeneratesIncludeFormat()
    {
        var entry = TestDataBuilder.CreateMatrixEntry(
            name: "TestProject",
            projectName: "TestProject",
            testProjectPath: "tests/TestProject/TestProject.csproj",
            supportedOSes: ["linux"]);

        var canonicalMatrix = Path.Combine(_tempDir.Path, "canonical.json");
        TestDataBuilder.CreateCanonicalMatrixJson(canonicalMatrix, tests: [entry]);

        var outputFile = Path.Combine(_tempDir.Path, "expanded.json");

        var result = await RunScript(canonicalMatrix, outputMatrixFile: outputFile);

        result.EnsureSuccessful();

        var json = File.ReadAllText(outputFile);
        var document = JsonDocument.Parse(json);
        Assert.True(document.RootElement.TryGetProperty("include", out var include));
        Assert.True(
            include.ValueKind == JsonValueKind.Array || include.ValueKind == JsonValueKind.Object,
            $"Expected include to be Array or Object, got {include.ValueKind}");
    }

    [Fact]
    [RequiresTools(["pwsh"])]
    public async Task RemovesSupportedOSes()
    {
        var entry = TestDataBuilder.CreateMatrixEntry(
            name: "TestProject",
            projectName: "TestProject",
            testProjectPath: "tests/TestProject/TestProject.csproj",
            supportedOSes: ["windows"]);

        var canonicalMatrix = Path.Combine(_tempDir.Path, "canonical.json");
        TestDataBuilder.CreateCanonicalMatrixJson(canonicalMatrix, tests: [entry]);

        var outputFile = Path.Combine(_tempDir.Path, "expanded.json");

        var result = await RunScript(canonicalMatrix, outputMatrixFile: outputFile);

        result.EnsureSuccessful();

        var json = File.ReadAllText(outputFile);
        var document = JsonDocument.Parse(json);
        var include = document.RootElement.GetProperty("include");

        var firstEntry = include.ValueKind == JsonValueKind.Array
            ? include.EnumerateArray().First()
            : include;

        Assert.False(firstEntry.TryGetProperty("supportedOSes", out _),
            "supportedOSes should be removed from expanded entries");
    }

    [Fact]
    [RequiresTools(["pwsh"])]
    public async Task AddsRunsOnProperty()
    {
        var entry = TestDataBuilder.CreateMatrixEntry(
            name: "TestProject",
            projectName: "TestProject",
            testProjectPath: "tests/TestProject/TestProject.csproj",
            supportedOSes: ["linux"]);

        var canonicalMatrix = Path.Combine(_tempDir.Path, "canonical.json");
        TestDataBuilder.CreateCanonicalMatrixJson(canonicalMatrix, tests: [entry]);

        var outputFile = Path.Combine(_tempDir.Path, "expanded.json");

        var result = await RunScript(canonicalMatrix, outputMatrixFile: outputFile);

        result.EnsureSuccessful();

        var json = File.ReadAllText(outputFile);
        var document = JsonDocument.Parse(json);
        var include = document.RootElement.GetProperty("include");

        var firstEntry = include.ValueKind == JsonValueKind.Array
            ? include.EnumerateArray().First()
            : include;

        Assert.True(firstEntry.TryGetProperty("runs-on", out var runsOn),
            "runs-on should be added to expanded entries");
        Assert.Equal("ubuntu-latest", runsOn.GetString());
    }

    [Fact]
    [RequiresTools(["pwsh"])]
    public async Task PreservesAllEntryProperties()
    {
        var entry = TestDataBuilder.CreateMatrixEntry(
            name: "FullProject",
            projectName: "FullProject",
            testProjectPath: "tests/FullProject/FullProject.csproj",
            type: "collection",
            shortname: "Full",
            workitemprefix: "FullProject_Part",
            collection: "MyPartition",
            extraTestArgs: "--filter-trait \"Partition=MyPartition\"",
            testSessionTimeout: "30m",
            testHangTimeout: "15m",
            supportedOSes: ["linux"]);

        var canonicalMatrix = Path.Combine(_tempDir.Path, "canonical.json");
        TestDataBuilder.CreateCanonicalMatrixJson(canonicalMatrix, tests: [entry]);

        var outputFile = Path.Combine(_tempDir.Path, "expanded.json");

        var result = await RunScript(canonicalMatrix, outputMatrixFile: outputFile);

        result.EnsureSuccessful();

        var expanded = ParseGitHubMatrix(outputFile);
        var expandedEntry = Assert.Single(expanded.Include);

        Assert.Equal("FullProject", expandedEntry.ProjectName);
        Assert.Equal("FullProject", expandedEntry.Name);
        Assert.Equal("collection", expandedEntry.Type);
        Assert.Equal("Full", expandedEntry.Shortname);
        Assert.Equal("FullProject_Part", expandedEntry.Workitemprefix);
        Assert.Equal("MyPartition", expandedEntry.Collection);
        Assert.Equal("--filter-trait \"Partition=MyPartition\"", expandedEntry.ExtraTestArgs);
        Assert.Equal("30m", expandedEntry.TestSessionTimeout);
        Assert.Equal("15m", expandedEntry.TestHangTimeout);
        Assert.Equal("ubuntu-latest", expandedEntry.RunsOn);
    }

    [Fact]
    [RequiresTools(["pwsh"])]
    public async Task HandlesEmptyMatrix()
    {
        var canonicalMatrix = Path.Combine(_tempDir.Path, "canonical.json");
        TestDataBuilder.CreateCanonicalMatrixJson(canonicalMatrix);

        var outputFile = Path.Combine(_tempDir.Path, "expanded.json");

        var result = await RunScript(canonicalMatrix, outputMatrixFile: outputFile);

        result.EnsureSuccessful();

        var expanded = ParseGitHubMatrix(outputFile);
        Assert.Empty(expanded.Include);
    }

    [Fact]
    [RequiresTools(["pwsh"])]
    public async Task FailsWhenCanonicalMatrixNotFound()
    {
        var nonExistentFile = Path.Combine(_tempDir.Path, "does-not-exist.json");
        var outputFile = Path.Combine(_tempDir.Path, "expanded.json");

        var result = await RunScript(nonExistentFile, outputMatrixFile: outputFile);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("not found", result.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    [RequiresTools(["pwsh"])]
    public async Task DefaultsToAllOSesWhenSupportedOSesEmpty()
    {
        var entry = TestDataBuilder.CreateMatrixEntry(
            name: "NoOsSpecified",
            projectName: "NoOsSpecified",
            testProjectPath: "tests/NoOsSpecified/NoOsSpecified.csproj",
            supportedOSes: []);

        var canonicalMatrix = Path.Combine(_tempDir.Path, "canonical.json");
        TestDataBuilder.CreateCanonicalMatrixJson(canonicalMatrix, tests: [entry]);

        var outputFile = Path.Combine(_tempDir.Path, "expanded.json");

        var result = await RunScript(canonicalMatrix, outputMatrixFile: outputFile);

        result.EnsureSuccessful();

        var expanded = ParseGitHubMatrix(outputFile);
        Assert.Equal(3, expanded.Include.Length);
        Assert.Contains(expanded.Include, e => e.RunsOn == "windows-latest");
        Assert.Contains(expanded.Include, e => e.RunsOn == "ubuntu-latest");
        Assert.Contains(expanded.Include, e => e.RunsOn == "macos-latest");
    }

    [Fact]
    [RequiresTools(["pwsh"])]
    public async Task WarnsOnInvalidOS()
    {
        var entry = TestDataBuilder.CreateMatrixEntry(
            name: "BadOs",
            projectName: "BadOs",
            testProjectPath: "tests/BadOs/BadOs.csproj",
            supportedOSes: ["linux", "invalid-os"]);

        var canonicalMatrix = Path.Combine(_tempDir.Path, "canonical.json");
        TestDataBuilder.CreateCanonicalMatrixJson(canonicalMatrix, tests: [entry]);

        var outputFile = Path.Combine(_tempDir.Path, "expanded.json");

        var result = await RunScript(canonicalMatrix, outputMatrixFile: outputFile);

        result.EnsureSuccessful();

        Assert.Contains("invalid", result.Output.ToLowerInvariant());

        var expanded = ParseGitHubMatrix(outputFile);
        Assert.Single(expanded.Include);
        Assert.Equal("ubuntu-latest", expanded.Include[0].RunsOn);
    }

    [Fact]
    [RequiresTools(["pwsh"])]
    public async Task HandlesOSNamesWithDifferentCasing()
    {
        var entry = TestDataBuilder.CreateMatrixEntry(
            name: "CasedOs",
            projectName: "CasedOs",
            testProjectPath: "tests/CasedOs/CasedOs.csproj",
            supportedOSes: ["WINDOWS", "Linux", "MacOS"]);

        var canonicalMatrix = Path.Combine(_tempDir.Path, "canonical.json");
        TestDataBuilder.CreateCanonicalMatrixJson(canonicalMatrix, tests: [entry]);

        var outputFile = Path.Combine(_tempDir.Path, "expanded.json");

        var result = await RunScript(canonicalMatrix, outputMatrixFile: outputFile);

        result.EnsureSuccessful();

        var expanded = ParseGitHubMatrix(outputFile);
        Assert.Equal(3, expanded.Include.Length);
        Assert.Contains(expanded.Include, e => e.RunsOn == "windows-latest");
        Assert.Contains(expanded.Include, e => e.RunsOn == "ubuntu-latest");
        Assert.Contains(expanded.Include, e => e.RunsOn == "macos-latest");
    }

    [Fact]
    [RequiresTools(["pwsh"])]
    public async Task SplitTestsGoToNoNugetsCategory()
    {
        var splitEntry = TestDataBuilder.CreateMatrixEntry(
            name: "SplitProject",
            projectName: "SplitProject",
            testProjectPath: "tests/SplitProject/SplitProject.csproj",
            supportedOSes: ["linux"]);
        splitEntry.SplitTests = true;

        var regularEntry = TestDataBuilder.CreateMatrixEntry(
            name: "RegularProject",
            projectName: "RegularProject",
            testProjectPath: "tests/RegularProject/RegularProject.csproj",
            supportedOSes: ["linux"]);

        var canonicalMatrix = Path.Combine(_tempDir.Path, "canonical.json");
        TestDataBuilder.CreateCanonicalMatrixJson(canonicalMatrix, tests: [splitEntry, regularEntry]);

        var outputFile = Path.Combine(_tempDir.Path, "expanded.json");

        var result = await RunScript(canonicalMatrix, outputMatrixFile: outputFile);

        result.EnsureSuccessful();

        var expanded = ParseGitHubMatrix(outputFile);
        Assert.Equal(2, expanded.Include.Length);
        Assert.Contains(expanded.Include, e => e.ProjectName == "SplitProject");
        Assert.Contains(expanded.Include, e => e.ProjectName == "RegularProject");
    }

    [Fact]
    [RequiresTools(["pwsh"])]
    public async Task UsesCustomRunnerForSpecificOS()
    {
        // Arrange: custom runner for macos only
        var entry = TestDataBuilder.CreateMatrixEntry(
            name: "CustomRunnerProject",
            projectName: "CustomRunnerProject",
            testProjectPath: "tests/CustomRunnerProject/CustomRunnerProject.csproj",
            supportedOSes: ["windows", "linux", "macos"],
            runners: new Dictionary<string, string> { ["macos"] = "macos-latest-xlarge" });

        var canonicalMatrix = Path.Combine(_tempDir.Path, "canonical.json");
        TestDataBuilder.CreateCanonicalMatrixJson(canonicalMatrix, tests: [entry]);

        var outputFile = Path.Combine(_tempDir.Path, "expanded.json");

        // Act
        var result = await RunScript(canonicalMatrix, outputMatrixFile: outputFile);

        // Assert
        result.EnsureSuccessful();

        var expanded = ParseGitHubMatrix(outputFile);
        Assert.Equal(3, expanded.Include.Length);

        // macos should use custom runner
        var macEntry = expanded.Include.First(e => e.RunsOn == "macos-latest-xlarge");

        // windows and linux should use defaults
        Assert.Contains(expanded.Include, e => e.RunsOn == "windows-latest");
        Assert.Contains(expanded.Include, e => e.RunsOn == "ubuntu-latest");
    }

    [Fact]
    [RequiresTools(["pwsh"])]
    public async Task UsesCustomRunnerForMultipleOSes()
    {
        // Arrange: custom runners for all three OSes
        var entry = TestDataBuilder.CreateMatrixEntry(
            name: "AllCustomRunners",
            projectName: "AllCustomRunners",
            testProjectPath: "tests/AllCustomRunners/AllCustomRunners.csproj",
            supportedOSes: ["windows", "linux", "macos"],
            runners: new Dictionary<string, string>
            {
                ["windows"] = "windows-2022",
                ["linux"] = "ubuntu-24.04",
                ["macos"] = "macos-latest-xlarge"
            });

        var canonicalMatrix = Path.Combine(_tempDir.Path, "canonical.json");
        TestDataBuilder.CreateCanonicalMatrixJson(canonicalMatrix, tests: [entry]);

        var outputFile = Path.Combine(_tempDir.Path, "expanded.json");

        // Act
        var result = await RunScript(canonicalMatrix, outputMatrixFile: outputFile);

        // Assert
        result.EnsureSuccessful();

        var expanded = ParseGitHubMatrix(outputFile);
        Assert.Equal(3, expanded.Include.Length);
        Assert.Contains(expanded.Include, e => e.RunsOn == "windows-2022");
        Assert.Contains(expanded.Include, e => e.RunsOn == "ubuntu-24.04");
        Assert.Contains(expanded.Include, e => e.RunsOn == "macos-latest-xlarge");

        // Verify no default runners are used
        Assert.DoesNotContain(expanded.Include, e => e.RunsOn == "windows-latest");
        Assert.DoesNotContain(expanded.Include, e => e.RunsOn == "ubuntu-latest");
        Assert.DoesNotContain(expanded.Include, e => e.RunsOn == "macos-latest");
    }

    [Fact]
    [RequiresTools(["pwsh"])]
    public async Task UsesDefaultRunnersWhenNoCustomRunnersSet()
    {
        // Arrange: no runners property
        var entry = TestDataBuilder.CreateMatrixEntry(
            name: "DefaultRunners",
            projectName: "DefaultRunners",
            testProjectPath: "tests/DefaultRunners/DefaultRunners.csproj",
            supportedOSes: ["windows", "linux", "macos"]);

        var canonicalMatrix = Path.Combine(_tempDir.Path, "canonical.json");
        TestDataBuilder.CreateCanonicalMatrixJson(canonicalMatrix, tests: [entry]);

        var outputFile = Path.Combine(_tempDir.Path, "expanded.json");

        // Act
        var result = await RunScript(canonicalMatrix, outputMatrixFile: outputFile);

        // Assert
        result.EnsureSuccessful();

        var expanded = ParseGitHubMatrix(outputFile);
        Assert.Equal(3, expanded.Include.Length);
        Assert.Contains(expanded.Include, e => e.RunsOn == "windows-latest");
        Assert.Contains(expanded.Include, e => e.RunsOn == "ubuntu-latest");
        Assert.Contains(expanded.Include, e => e.RunsOn == "macos-latest");
    }

    [Fact]
    [RequiresTools(["pwsh"])]
    public async Task FullPipeline_SplitTestsExpandPerOS()
    {
        // Validates the full pipeline: build-test-matrix → expand-test-matrix-github → split-test-matrix-by-deps
        var artifactsDir = Path.Combine(_tempDir.Path, "artifacts");
        Directory.CreateDirectory(artifactsDir);

        TestDataBuilder.CreateTestsMetadataJson(
            Path.Combine(artifactsDir, "RegularProject.tests-metadata.json"),
            projectName: "RegularProject",
            testProjectPath: "tests/RegularProject/RegularProject.csproj",
            shortName: "Regular");

        TestDataBuilder.CreateSplitTestsMetadataJson(
            Path.Combine(artifactsDir, "SplitMultiOS.tests-metadata.json"),
            projectName: "SplitMultiOS",
            testProjectPath: "tests/SplitMultiOS/SplitMultiOS.csproj",
            shortName: "SplitMultiOS",
            supportedOSes: ["windows", "linux", "macos"]);

        TestDataBuilder.CreateClassBasedPartitionsJson(
            Path.Combine(artifactsDir, "SplitMultiOS.tests-partitions.json"),
            "Namespace.ClassA", "Namespace.ClassB");

        TestDataBuilder.CreateTestsMetadataJson(
            Path.Combine(artifactsDir, "LinuxE2E.tests-metadata.json"),
            projectName: "LinuxE2E",
            testProjectPath: "tests/LinuxE2E/LinuxE2E.csproj",
            shortName: "LinuxE2E",
            requiresNugets: true,
            supportedOSes: ["linux"]);

        TestDataBuilder.CreateTestsMetadataJson(
            Path.Combine(artifactsDir, "CliE2E.tests-metadata.json"),
            projectName: "CliE2E",
            testProjectPath: "tests/CliE2E/CliE2E.csproj",
            shortName: "CliE2E",
            requiresNugets: true,
            requiresCliArchive: true,
            supportedOSes: ["linux"]);

        // Run build-test-matrix.ps1
        var buildMatrixScript = Path.Combine(FindRepoRoot(), "eng", "scripts", "build-test-matrix.ps1");
        var canonicalFile = Path.Combine(_tempDir.Path, "canonical.json");

        using var buildCmd = new PowerShellCommand(buildMatrixScript, _output)
            .WithTimeout(TimeSpan.FromMinutes(2));
        var buildResult = await buildCmd.ExecuteAsync(
            "-ArtifactsDir", $"\"{artifactsDir}\"",
            "-OutputMatrixFile", $"\"{canonicalFile}\"");
        buildResult.EnsureSuccessful("build-test-matrix.ps1 failed");

        // Run expand-test-matrix-github.ps1 → single output file
        var expandedFile = Path.Combine(_tempDir.Path, "expanded.json");
        var expandResult = await RunScript(canonicalFile, outputMatrixFile: expandedFile);
        expandResult.EnsureSuccessful("expand-test-matrix-github.ps1 failed");

        // Run split-test-matrix-by-deps.ps1
        var splitScriptPath = Path.Combine(FindRepoRoot(), "eng", "scripts", "split-test-matrix-by-deps.ps1");
        var githubOutputFile = Path.Combine(_tempDir.Path, "github_output.txt");
        File.WriteAllText(githubOutputFile, "");

        using var splitCmd = new PowerShellCommand(splitScriptPath, _output)
            .WithTimeout(TimeSpan.FromMinutes(2))
            .WithEnvironmentVariable("GITHUB_OUTPUT", githubOutputFile);
        var splitResult = await splitCmd.ExecuteAsync(
            "-AllTestsMatrixFile", $"\"{expandedFile}\"",
            "-OutputToGitHubEnv");
        splitResult.EnsureSuccessful("split-test-matrix-by-deps.ps1 failed");

        // Read split results from GITHUB_OUTPUT file
        var splitOutputs = ParseGitHubOutputFile(githubOutputFile);
        var noNugets = splitOutputs["tests_matrix_no_nugets"];
        var noNugetsOverflow = splitOutputs["tests_matrix_no_nugets_overflow"];
        var nugetsLinux = splitOutputs["tests_matrix_requires_nugets_linux"];
        var nugetsWindows = splitOutputs["tests_matrix_requires_nugets_windows"];
        var nugetsMacos = splitOutputs["tests_matrix_requires_nugets_macos"];
        var cliArchiveMatrix = splitOutputs["tests_matrix_requires_cli_archive"];

        var allNoNugets = noNugets.Include.Concat(noNugetsOverflow.Include).ToArray();

        // Regular project: 1 project × 3 OSes = 3
        var regularEntries = allNoNugets.Where(e => e.ProjectName == "RegularProject").ToArray();
        Assert.Equal(3, regularEntries.Length);

        // Split project: 2 classes × 3 OSes = 6
        var splitEntries = allNoNugets.Where(e => e.ProjectName == "SplitMultiOS").ToArray();
        Assert.Equal(6, splitEntries.Length);
        Assert.Equal(2, splitEntries.Count(e => e.RunsOn == "ubuntu-latest"));
        Assert.Equal(2, splitEntries.Count(e => e.RunsOn == "windows-latest"));
        Assert.Equal(2, splitEntries.Count(e => e.RunsOn == "macos-latest"));

        // Linux-only E2E: 1 project × 1 OS = 1, in requires-nugets-linux matrix
        var e2eEntries = nugetsLinux.Include.Where(e => e.ProjectName == "LinuxE2E").ToArray();
        Assert.Single(e2eEntries);
        Assert.Equal("ubuntu-latest", e2eEntries[0].RunsOn);
        Assert.True(e2eEntries[0].RequiresNugets);

        // CLI E2E: 1 project × 1 OS = 1, in requires-cli-archive matrix
        var cliE2eEntries = cliArchiveMatrix.Include.Where(e => e.ProjectName == "CliE2E").ToArray();
        Assert.Single(cliE2eEntries);
        Assert.Equal("ubuntu-latest", cliE2eEntries[0].RunsOn);
        Assert.True(cliE2eEntries[0].RequiresCliArchive);

        // Total no-nugets: 3 + 6 = 9, Total nugets: 1 (linux only), Total cli-archive: 1
        Assert.Equal(9, allNoNugets.Length);
        Assert.Single(nugetsLinux.Include);
        Assert.Empty(nugetsWindows.Include);
        Assert.Empty(nugetsMacos.Include);
        Assert.Single(cliArchiveMatrix.Include);
    }

    private async Task<CommandResult> RunScript(
        string canonicalMatrixFile,
        string? outputMatrixFile = null)
    {
        using var cmd = new PowerShellCommand(_scriptPath, _output)
            .WithTimeout(TimeSpan.FromMinutes(2));

        var args = new List<string>
        {
            "-CanonicalMatrixFile", $"\"{canonicalMatrixFile}\""
        };

        if (!string.IsNullOrEmpty(outputMatrixFile))
        {
            args.Add("-OutputMatrixFile");
            args.Add($"\"{outputMatrixFile}\"");
        }

        return await cmd.ExecuteAsync(args.ToArray());
    }

    private static GitHubActionsMatrix ParseGitHubMatrix(string path)
    {
        var json = File.ReadAllText(path);
        return ParseGitHubMatrixJson(json);
    }

    private static Dictionary<string, GitHubActionsMatrix> ParseGitHubOutputFile(string path)
    {
        var results = new Dictionary<string, GitHubActionsMatrix>();
        foreach (var line in File.ReadAllLines(path))
        {
            var eqIndex = line.IndexOf('=');
            if (eqIndex < 0)
            {
                continue;
            }

            var key = line[..eqIndex];
            var value = line[(eqIndex + 1)..];
            results[key] = ParseGitHubMatrixJson(value);
        }
        return results;
    }

    private static GitHubActionsMatrix ParseGitHubMatrixJson(string json)
    {
        var document = JsonDocument.Parse(json);
        var include = document.RootElement.GetProperty("include");

        // PowerShell's ConvertTo-Json serializes single-element arrays as objects
        // Handle both cases
        ExpandedMatrixEntry[] entries;
        if (include.ValueKind == JsonValueKind.Array)
        {
            entries = JsonSerializer.Deserialize<ExpandedMatrixEntry[]>(include.GetRawText(), new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            }) ?? [];
        }
        else if (include.ValueKind == JsonValueKind.Object)
        {
            var singleEntry = JsonSerializer.Deserialize<ExpandedMatrixEntry>(include.GetRawText(), new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });
            entries = singleEntry is not null ? [singleEntry] : [];
        }
        else
        {
            entries = [];
        }

        return new GitHubActionsMatrix { Include = entries };
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
