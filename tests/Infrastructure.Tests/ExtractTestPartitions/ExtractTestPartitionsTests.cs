// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using Xunit;

namespace Infrastructure.Tests;

/// <summary>
/// Tests for the ExtractTestPartitions tool.
/// </summary>
public class ExtractTestPartitionsTests : IClassFixture<ExtractTestPartitionsFixture>, IDisposable
{
    private readonly TestTempDirectory _tempDir = new();
    private readonly ExtractTestPartitionsFixture _fixture;
    private readonly ITestOutputHelper _output;

    public ExtractTestPartitionsTests(ExtractTestPartitionsFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        _output = output;
    }

    public void Dispose() => _tempDir.Dispose();

    [Fact]
    public async Task ExtractsPartitionTraits()
    {
        // Arrange
        var assemblyPath = Path.Combine(_tempDir.Path, "TestAssembly.dll");
        MockAssemblyBuilder.CreateAssemblyWithPartitions(
            assemblyPath,
            ("TestClass1", "PartitionA"),
            ("TestClass2", "PartitionB"));

        var outputFile = Path.Combine(_tempDir.Path, "partitions.txt");

        // Act
        var result = await RunTool(assemblyPath, outputFile);

        // Assert
        Assert.Equal(0, result.ExitCode);
        Assert.True(File.Exists(outputFile), "Output file should be created");

        var partitions = File.ReadAllLines(outputFile);
        Assert.Equal(2, partitions.Length);
        Assert.Contains("PartitionA", partitions);
        Assert.Contains("PartitionB", partitions);
    }

    [Fact]
    public async Task IgnoresCollectionAttributes()
    {
        // Arrange - Collection attributes are for shared fixtures, not CI splitting
        var assemblyPath = Path.Combine(_tempDir.Path, "TestAssembly.dll");
        MockAssemblyBuilder.CreateAssemblyWithCollections(
            assemblyPath,
            ("TestClass1", "CollectionX"),
            ("TestClass2", "CollectionY"));

        var outputFile = Path.Combine(_tempDir.Path, "partitions.txt");

        // Act
        var result = await RunTool(assemblyPath, outputFile);

        // Assert
        Assert.Equal(0, result.ExitCode);
        Assert.False(File.Exists(outputFile), "Output file should NOT be created for Collection-only attributes");
        Assert.Contains("No partitions found", result.Output);
    }

    [Fact]
    public async Task ExtractsOnlyTraitPartitionsFromMixedAttributes()
    {
        // Arrange
        var assemblyPath = Path.Combine(_tempDir.Path, "TestAssembly.dll");
        MockAssemblyBuilder.CreateAssemblyWithMixedAttributes(
            assemblyPath,
            partitions: [("PartitionTest1", "PartA")],
            collections: [("CollectionTest1", "CollB")]);

        var outputFile = Path.Combine(_tempDir.Path, "partitions.txt");

        // Act
        var result = await RunTool(assemblyPath, outputFile);

        // Assert - Only Trait("Partition") entries are extracted, not Collection attributes
        Assert.Equal(0, result.ExitCode);
        Assert.True(File.Exists(outputFile), "Output file should be created");

        var partitions = File.ReadAllLines(outputFile);
        Assert.Single(partitions);
        Assert.Contains("PartA", partitions);
    }

    [Fact]
    public async Task ReturnsEmptyForNoAttributes()
    {
        // Arrange
        var assemblyPath = Path.Combine(_tempDir.Path, "TestAssembly.dll");
        MockAssemblyBuilder.CreateAssemblyWithNoAttributes(assemblyPath);

        var outputFile = Path.Combine(_tempDir.Path, "partitions.txt");

        // Act
        var result = await RunTool(assemblyPath, outputFile);

        // Assert
        Assert.Equal(0, result.ExitCode);
        Assert.False(File.Exists(outputFile), "Output file should NOT be created when no partitions found");
        Assert.Contains("No partitions found", result.Output);
    }

    [Fact]
    public async Task SortsPartitionsAlphabetically()
    {
        // Arrange
        var assemblyPath = Path.Combine(_tempDir.Path, "TestAssembly.dll");
        MockAssemblyBuilder.CreateAssemblyWithPartitions(
            assemblyPath,
            ("TestZ", "Zebra"),
            ("TestA", "Apple"),
            ("TestM", "Mango"));

        var outputFile = Path.Combine(_tempDir.Path, "partitions.txt");

        // Act
        var result = await RunTool(assemblyPath, outputFile);

        // Assert
        Assert.Equal(0, result.ExitCode);
        var partitions = File.ReadAllLines(outputFile);
        Assert.Equal(3, partitions.Length);
        Assert.Equal("Apple", partitions[0]);
        Assert.Equal("Mango", partitions[1]);
        Assert.Equal("Zebra", partitions[2]);
    }

    [Fact]
    public async Task DeduplicatesPartitions()
    {
        // Arrange - same partition name with different casing
        var assemblyPath = Path.Combine(_tempDir.Path, "TestAssembly.dll");
        MockAssemblyBuilder.CreateAssemblyWithPartitions(
            assemblyPath,
            ("TestClass1", "PartitionA"),
            ("TestClass2", "partitiona"), // lowercase variant
            ("TestClass3", "PARTITIONA")); // uppercase variant

        var outputFile = Path.Combine(_tempDir.Path, "partitions.txt");

        // Act
        var result = await RunTool(assemblyPath, outputFile);

        // Assert
        Assert.Equal(0, result.ExitCode);
        var partitions = File.ReadAllLines(outputFile);
        // Should be deduplicated case-insensitively - only one entry
        Assert.Single(partitions);
    }

    [Fact]
    public async Task HandlesInvalidAssemblyPath()
    {
        // Arrange
        var nonExistentPath = Path.Combine(_tempDir.Path, "DoesNotExist.dll");
        var outputFile = Path.Combine(_tempDir.Path, "partitions.txt");

        // Act
        var result = await RunTool(nonExistentPath, outputFile);

        // Assert
        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("not found", result.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task HandlesInvalidArguments()
    {
        // Act - run with no arguments
        var result = await RunToolRaw();

        // Assert
        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("Usage:", result.Output);
    }

    [Fact]
    public async Task CreatesOutputDirectory()
    {
        // Arrange
        var assemblyPath = Path.Combine(_tempDir.Path, "TestAssembly.dll");
        MockAssemblyBuilder.CreateAssemblyWithPartitions(
            assemblyPath,
            ("TestClass1", "PartitionA"));

        // Output in nested directory that doesn't exist
        var outputFile = Path.Combine(_tempDir.Path, "nested", "dir", "partitions.txt");

        // Act
        var result = await RunTool(assemblyPath, outputFile);

        // Assert
        Assert.Equal(0, result.ExitCode);
        Assert.True(File.Exists(outputFile), "Output file should be created in nested directory");
    }

    [Fact]
    public async Task ExtractsPartitionsFromNestedTypes()
    {
        // Arrange - Test classes can be nested (Outer+Inner pattern)
        var assemblyPath = Path.Combine(_tempDir.Path, "TestAssembly.dll");
        MockAssemblyBuilder.CreateAssemblyWithNestedTypePartitions(
            assemblyPath,
            ("OuterClass", "InnerClass", "NestedPartition"));

        var outputFile = Path.Combine(_tempDir.Path, "partitions.txt");

        // Act
        var result = await RunTool(assemblyPath, outputFile);

        // Assert
        Assert.Equal(0, result.ExitCode);
        Assert.True(File.Exists(outputFile), "Output file should be created");

        var partitions = File.ReadAllLines(outputFile);
        Assert.Contains("NestedPartition", partitions);
    }

    [Fact]
    public async Task IgnoresEmptyPartitionNames()
    {
        // Arrange - Empty/whitespace partition names should be ignored
        var assemblyPath = Path.Combine(_tempDir.Path, "TestAssembly.dll");
        MockAssemblyBuilder.CreateAssemblyWithPartitions(
            assemblyPath,
            ("TestClass1", "ValidPartition"),
            ("TestClass2", ""), // Empty name
            ("TestClass3", "   ")); // Whitespace-only name

        var outputFile = Path.Combine(_tempDir.Path, "partitions.txt");

        // Act
        var result = await RunTool(assemblyPath, outputFile);

        // Assert
        Assert.Equal(0, result.ExitCode);
        Assert.True(File.Exists(outputFile), "Output file should be created");

        var partitions = File.ReadAllLines(outputFile);
        Assert.Single(partitions);
        Assert.Equal("ValidPartition", partitions[0]);
    }

    [Fact]
    public async Task IgnoresNonPartitionTraits()
    {
        // Arrange - Only Traits with key "Partition" should be extracted
        var assemblyPath = Path.Combine(_tempDir.Path, "TestAssembly.dll");
        MockAssemblyBuilder.CreateAssemblyWithNonPartitionTraits(
            assemblyPath,
            ("TestClass1", "Partition", "ShouldInclude"),
            ("TestClass2", "Category", "ShouldIgnore"),
            ("TestClass3", "OtherKey", "AlsoIgnore"));

        var outputFile = Path.Combine(_tempDir.Path, "partitions.txt");

        // Act
        var result = await RunTool(assemblyPath, outputFile);

        // Assert
        Assert.Equal(0, result.ExitCode);
        Assert.True(File.Exists(outputFile), "Output file should be created");

        var partitions = File.ReadAllLines(outputFile);
        Assert.Single(partitions);
        Assert.Equal("ShouldInclude", partitions[0]);
    }

    private async Task<ToolResult> RunTool(string assemblyPath, string outputFile)
    {
        return await RunToolRaw("--assembly-path", assemblyPath, "--output-file", outputFile);
    }

    private async Task<ToolResult> RunToolRaw(params string[] args)
    {
        // Use 'dotnet run --no-build' since the fixture already built the tool
        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        psi.ArgumentList.Add("run");
        psi.ArgumentList.Add("--no-build");
        psi.ArgumentList.Add("--project");
        psi.ArgumentList.Add(_fixture.ToolProjectPath);

        if (args.Length > 0)
        {
            psi.ArgumentList.Add("--");
            foreach (var arg in args)
            {
                psi.ArgumentList.Add(arg);
            }
        }

        _output.WriteLine($"Running: {psi.FileName} {string.Join(" ", psi.ArgumentList)}");

        using var process = new Process { StartInfo = psi };
        var outputLines = new List<string>();

        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                outputLines.Add(e.Data);
                _output.WriteLine($"[stdout] {e.Data}");
            }
        };

        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                outputLines.Add(e.Data);
                _output.WriteLine($"[stderr] {e.Data}");
            }
        };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        await process.WaitForExitAsync(cts.Token);

        return new ToolResult(process.ExitCode, string.Join(Environment.NewLine, outputLines));
    }

    private sealed record ToolResult(int ExitCode, string Output);
}
