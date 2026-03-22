// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;
using Aspire.TestUtilities;
using Xunit;

namespace Infrastructure.Tests;

/// <summary>
/// Tests for eng/scripts/split-test-projects-for-ci.ps1
///
/// Note: These tests are more limited because the script requires:
/// 1. A built test assembly with partition attributes, OR
/// 2. An executable test assembly to run --list-tests
///
/// We test the partition extraction path using mock assemblies, but we can't easily
/// test the class-based fallback without a real test project.
/// </summary>
public class SplitTestProjectsTests : IDisposable
{
    private readonly TestTempDirectory _tempDir = new();
    private readonly string _scriptPath;
    private readonly string _repoRoot;
    private readonly ITestOutputHelper _output;

    public SplitTestProjectsTests(ITestOutputHelper output)
    {
        _output = output;
        _repoRoot = FindRepoRoot();
        _scriptPath = Path.Combine(_repoRoot, "eng", "scripts", "split-test-projects-for-ci.ps1");
    }

    public void Dispose() => _tempDir.Dispose();

    [Fact]
    [RequiresTools(["pwsh"])]
    public async Task UsesCollectionModeWithPartitions()
    {
        // Arrange - Create a mock assembly with partition attributes
        var assemblyPath = Path.Combine(_tempDir.Path, "TestAssembly.dll");
        MockAssemblyBuilder.CreateAssemblyWithPartitions(
            assemblyPath,
            ("TestClass1", "PartitionA"),
            ("TestClass2", "PartitionB"));

        var outputFile = Path.Combine(_tempDir.Path, "partitions.json");

        // Act (the script builds ExtractTestPartitions tool internally if needed)
        var result = await RunScript(
            assemblyPath,
            runCommand: "echo", // Won't be used since we have partitions
            testClassPrefix: "TestNamespace",
            outputFile: outputFile);

        // Assert
        result.EnsureSuccessful("split-test-projects-for-ci.ps1 failed");
        Assert.Contains("Mode: collection", result.Output);

        var partitions = ParsePartitionsJson(outputFile);
        Assert.Contains("collection:PartitionA", partitions.TestPartitions);
        Assert.Contains("collection:PartitionB", partitions.TestPartitions);
        Assert.Contains("uncollected:*", partitions.TestPartitions);
    }

    [Fact]
    [RequiresTools(["pwsh"])]
    public async Task IncludesUncollectedEntry()
    {
        // Arrange
        var assemblyPath = Path.Combine(_tempDir.Path, "TestAssembly.dll");
        MockAssemblyBuilder.CreateAssemblyWithPartitions(
            assemblyPath,
            ("TestClass1", "OnlyPartition"));

        var outputFile = Path.Combine(_tempDir.Path, "partitions.json");

        // Act
        var result = await RunScript(
            assemblyPath,
            runCommand: "echo",
            testClassPrefix: "TestNamespace",
            outputFile: outputFile);

        // Assert
        result.EnsureSuccessful();

        var partitions = ParsePartitionsJson(outputFile);
        // Should always have uncollected:* at the end in collection mode
        Assert.Contains("uncollected:*", partitions.TestPartitions);
        Assert.Equal("uncollected:*", partitions.TestPartitions.Last());
    }

    [Fact]
    [RequiresTools(["pwsh"])]
    public async Task OutputsValidJson()
    {
        // Arrange
        var assemblyPath = Path.Combine(_tempDir.Path, "TestAssembly.dll");
        MockAssemblyBuilder.CreateAssemblyWithPartitions(
            assemblyPath,
            ("TestClass1", "Part1"),
            ("TestClass2", "Part2"));

        var outputFile = Path.Combine(_tempDir.Path, "partitions.json");

        // Act
        var result = await RunScript(
            assemblyPath,
            runCommand: "echo",
            testClassPrefix: "TestNamespace",
            outputFile: outputFile);

        // Assert
        result.EnsureSuccessful();
        Assert.True(File.Exists(outputFile), "Output file should exist");

        // Verify it's valid JSON
        var json = File.ReadAllText(outputFile);
        var document = JsonDocument.Parse(json); // Throws if invalid
        Assert.True(document.RootElement.TryGetProperty("testPartitions", out var partitions));
        Assert.Equal(JsonValueKind.Array, partitions.ValueKind);
    }

    [Fact]
    [RequiresTools(["pwsh"])]
    public async Task SortsPartitionsAlphabetically()
    {
        // Arrange
        var assemblyPath = Path.Combine(_tempDir.Path, "TestAssembly.dll");
        MockAssemblyBuilder.CreateAssemblyWithPartitions(
            assemblyPath,
            ("TestZ", "Zebra"),
            ("TestA", "Apple"),
            ("TestM", "Mango"));

        var outputFile = Path.Combine(_tempDir.Path, "partitions.json");

        // Act
        var result = await RunScript(
            assemblyPath,
            runCommand: "echo",
            testClassPrefix: "TestNamespace",
            outputFile: outputFile);

        // Assert
        result.EnsureSuccessful();

        var partitions = ParsePartitionsJson(outputFile);
        // Remove uncollected:* for sorting check
        var collectionEntries = partitions.TestPartitions
            .Where(p => p.StartsWith("collection:"))
            .ToArray();

        Assert.Equal("collection:Apple", collectionEntries[0]);
        Assert.Equal("collection:Mango", collectionEntries[1]);
        Assert.Equal("collection:Zebra", collectionEntries[2]);
    }

    [Fact]
    [RequiresTools(["pwsh"])]
    public async Task FailsWhenAssemblyNotFound()
    {
        // Arrange
        var nonExistentPath = Path.Combine(_tempDir.Path, "DoesNotExist.dll");
        var outputFile = Path.Combine(_tempDir.Path, "partitions.json");

        // Act
        var result = await RunScript(
            nonExistentPath,
            runCommand: "echo",
            testClassPrefix: "TestNamespace",
            outputFile: outputFile);

        // Assert
        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("not found", result.Output, StringComparison.OrdinalIgnoreCase);
    }

    private async Task<CommandResult> RunScript(
        string assemblyPath,
        string runCommand,
        string testClassPrefix,
        string outputFile)
    {
        using var cmd = new PowerShellCommand(_scriptPath, _output)
            .WithTimeout(TimeSpan.FromMinutes(3));

        var args = new List<string>
        {
            "-TestAssemblyPath", $"\"{assemblyPath}\"",
            "-RunCommand", $"\"{runCommand}\"",
            "-TestClassNamePrefixForCI", $"\"{testClassPrefix}\"",
            "-TestPartitionsJsonFile", $"\"{outputFile}\"",
            "-RepoRoot", $"\"{_repoRoot}\""
        };

        return await cmd.ExecuteAsync(args.ToArray());
    }

    private static TestPartitionsJson ParsePartitionsJson(string path)
    {
        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<TestPartitionsJson>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        }) ?? throw new InvalidOperationException("Failed to parse partitions JSON");
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

    private sealed class TestPartitionsJson
    {
        public string[] TestPartitions { get; set; } = [];
    }
}
