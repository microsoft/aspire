// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;
using Aspire.TestUtilities;
using Xunit;

namespace Infrastructure.Tests;

/// <summary>
/// Tests for eng/scripts/build-test-matrix.ps1
/// </summary>
public class BuildTestMatrixTests : IDisposable
{
    private readonly TestTempDirectory _tempDir = new();
    private readonly string _scriptPath;
    private readonly ITestOutputHelper _output;

    public BuildTestMatrixTests(ITestOutputHelper output)
    {
        _output = output;
        _scriptPath = Path.Combine(FindRepoRoot(), "eng", "scripts", "build-test-matrix.ps1");
    }

    public void Dispose() => _tempDir.Dispose();

    [Fact]
    [RequiresTools(["pwsh"])]
    public async Task GeneratesMatrixFromSingleProject()
    {
        // Arrange
        var artifactsDir = Path.Combine(_tempDir.Path, "artifacts");
        Directory.CreateDirectory(artifactsDir);

        TestDataBuilder.CreateTestsMetadataJson(
            Path.Combine(artifactsDir, "MyProject.tests-metadata.json"),
            projectName: "MyProject",
            testProjectPath: "tests/MyProject/MyProject.csproj",
            shortName: "MyProj");

        var outputFile = Path.Combine(_tempDir.Path, "matrix.json");

        // Act
        var result = await RunScript(artifactsDir, outputFile);

        // Assert
        result.EnsureSuccessful("build-test-matrix.ps1 failed");

        var matrix = ParseCanonicalMatrix(outputFile);
        var entry = Assert.Single(matrix.Tests);
        Assert.Equal("MyProject", entry.ProjectName);
        Assert.Equal("MyProj", entry.Name);
        Assert.Equal("regular", entry.Type);
        Assert.False(entry.Properties.GetValueOrDefault("requiresNugets"));
    }

    [Fact]
    [RequiresTools(["pwsh"])]
    public async Task GeneratesMatrixFromMultipleProjects()
    {
        // Arrange
        var artifactsDir = Path.Combine(_tempDir.Path, "artifacts");
        Directory.CreateDirectory(artifactsDir);

        TestDataBuilder.CreateTestsMetadataJson(
            Path.Combine(artifactsDir, "ProjectA.tests-metadata.json"),
            projectName: "ProjectA",
            testProjectPath: "tests/ProjectA/ProjectA.csproj");

        TestDataBuilder.CreateTestsMetadataJson(
            Path.Combine(artifactsDir, "ProjectB.tests-metadata.json"),
            projectName: "ProjectB",
            testProjectPath: "tests/ProjectB/ProjectB.csproj");

        var outputFile = Path.Combine(_tempDir.Path, "matrix.json");

        // Act
        var result = await RunScript(artifactsDir, outputFile);

        // Assert
        result.EnsureSuccessful();

        var matrix = ParseCanonicalMatrix(outputFile);
        Assert.Equal(2, matrix.Tests.Length);
        Assert.Contains(matrix.Tests, e => e.ProjectName == "ProjectA");
        Assert.Contains(matrix.Tests, e => e.ProjectName == "ProjectB");
    }

    [Fact]
    [RequiresTools(["pwsh"])]
    public async Task GeneratesPartitionEntries()
    {
        // Arrange
        var artifactsDir = Path.Combine(_tempDir.Path, "artifacts");
        Directory.CreateDirectory(artifactsDir);

        TestDataBuilder.CreateSplitTestsMetadataJson(
            Path.Combine(artifactsDir, "SplitProject.tests-metadata.json"),
            projectName: "SplitProject",
            testProjectPath: "tests/SplitProject/SplitProject.csproj",
            shortName: "Split");

        TestDataBuilder.CreateTestsPartitionsJson(
            Path.Combine(artifactsDir, "SplitProject.tests-partitions.json"),
            "PartitionA", "PartitionB");

        var outputFile = Path.Combine(_tempDir.Path, "matrix.json");

        // Act
        var result = await RunScript(artifactsDir, outputFile);

        // Assert
        result.EnsureSuccessful();

        var matrix = ParseCanonicalMatrix(outputFile);
        // Should have 3 entries: PartitionA, PartitionB, and uncollected
        Assert.Equal(3, matrix.Tests.Length);

        var partitionA = matrix.Tests.FirstOrDefault(e => e.Name == "Split-PartitionA");
        Assert.NotNull(partitionA);
        Assert.Equal("collection", partitionA.Type);
        Assert.Contains("--filter-trait", partitionA.ExtraTestArgs);
        Assert.Contains("Partition=PartitionA", partitionA.ExtraTestArgs);

        var uncollected = matrix.Tests.FirstOrDefault(e => e.Name == "Split");
        Assert.NotNull(uncollected);
        Assert.Contains("--filter-not-trait", uncollected.ExtraTestArgs);
    }

    [Fact]
    [RequiresTools(["pwsh"])]
    public async Task GeneratesClassEntries()
    {
        // Arrange
        var artifactsDir = Path.Combine(_tempDir.Path, "artifacts");
        Directory.CreateDirectory(artifactsDir);

        TestDataBuilder.CreateSplitTestsMetadataJson(
            Path.Combine(artifactsDir, "ClassSplitProject.tests-metadata.json"),
            projectName: "ClassSplitProject",
            testProjectPath: "tests/ClassSplitProject/ClassSplitProject.csproj",
            shortName: "ClassSplit");

        TestDataBuilder.CreateClassBasedPartitionsJson(
            Path.Combine(artifactsDir, "ClassSplitProject.tests-partitions.json"),
            "MyNamespace.TestClassA", "MyNamespace.TestClassB");

        var outputFile = Path.Combine(_tempDir.Path, "matrix.json");

        // Act
        var result = await RunScript(artifactsDir, outputFile);

        // Assert
        result.EnsureSuccessful();

        var matrix = ParseCanonicalMatrix(outputFile);
        Assert.Equal(2, matrix.Tests.Length);

        var classA = matrix.Tests.FirstOrDefault(e => e.Name == "ClassSplit-TestClassA");
        Assert.NotNull(classA);
        Assert.Equal("class", classA.Type);
        Assert.Contains("--filter-class", classA.ExtraTestArgs);
        Assert.Contains("MyNamespace.TestClassA", classA.ExtraTestArgs);
    }

    [Fact]
    [RequiresTools(["pwsh"])]
    public async Task DefaultsMtpBaseArgsToEmptyWhenNotSpecified()
    {
        // Arrange
        var artifactsDir = Path.Combine(_tempDir.Path, "artifacts");
        Directory.CreateDirectory(artifactsDir);

        // Create metadata without explicit timeouts
        TestDataBuilder.CreateTestsMetadataJson(
            Path.Combine(artifactsDir, "NoTimeouts.tests-metadata.json"),
            projectName: "NoTimeouts",
            testProjectPath: "tests/NoTimeouts/NoTimeouts.csproj");

        var outputFile = Path.Combine(_tempDir.Path, "matrix.json");

        // Act
        var result = await RunScript(artifactsDir, outputFile);

        // Assert
        result.EnsureSuccessful();

        var matrix = ParseCanonicalMatrix(outputFile);
        var entry = Assert.Single(matrix.Tests);
        Assert.Equal("", entry.MtpBaseArgs);
    }

    [Fact]
    [RequiresTools(["pwsh"])]
    public async Task PreservesCustomMtpBaseArgs()
    {
        // Arrange
        var artifactsDir = Path.Combine(_tempDir.Path, "artifacts");
        Directory.CreateDirectory(artifactsDir);

        TestDataBuilder.CreateTestsMetadataJson(
            Path.Combine(artifactsDir, "CustomTimeouts.tests-metadata.json"),
            projectName: "CustomTimeouts",
            testProjectPath: "tests/CustomTimeouts/CustomTimeouts.csproj",
            mtpBaseArgs: "--hangdump-timeout 15m --timeout 45m");

        var outputFile = Path.Combine(_tempDir.Path, "matrix.json");

        // Act
        var result = await RunScript(artifactsDir, outputFile);

        // Assert
        result.EnsureSuccessful();

        var matrix = ParseCanonicalMatrix(outputFile);
        var entry = Assert.Single(matrix.Tests);
        Assert.Equal("--hangdump-timeout 15m --timeout 45m", entry.MtpBaseArgs);
    }

    [Fact]
    [RequiresTools(["pwsh"])]
    public async Task PreservesRequiresNugetsProperty()
    {
        // Arrange
        var artifactsDir = Path.Combine(_tempDir.Path, "artifacts");
        Directory.CreateDirectory(artifactsDir);

        TestDataBuilder.CreateTestsMetadataJson(
            Path.Combine(artifactsDir, "NeedsNugets.tests-metadata.json"),
            projectName: "NeedsNugets",
            testProjectPath: "tests/NeedsNugets/NeedsNugets.csproj",
            requiresNugets: true);

        TestDataBuilder.CreateTestsMetadataJson(
            Path.Combine(artifactsDir, "NoNugets.tests-metadata.json"),
            projectName: "NoNugets",
            testProjectPath: "tests/NoNugets/NoNugets.csproj",
            requiresNugets: false);

        var outputFile = Path.Combine(_tempDir.Path, "matrix.json");

        // Act
        var result = await RunScript(artifactsDir, outputFile);

        // Assert
        result.EnsureSuccessful();

        var matrix = ParseCanonicalMatrix(outputFile);
        Assert.Equal(2, matrix.Tests.Length);
        Assert.Contains(matrix.Tests, e => e.ProjectName == "NeedsNugets" && e.Properties.GetValueOrDefault("requiresNugets") == true);
        Assert.Contains(matrix.Tests, e => e.ProjectName == "NoNugets" && e.Properties.GetValueOrDefault("requiresNugets") == false);
    }

    [Fact]
    [RequiresTools(["pwsh"])]
    public async Task GeneratesCorrectFilterArgs()
    {
        // Arrange
        var artifactsDir = Path.Combine(_tempDir.Path, "artifacts");
        Directory.CreateDirectory(artifactsDir);

        TestDataBuilder.CreateSplitTestsMetadataJson(
            Path.Combine(artifactsDir, "FilterTest.tests-metadata.json"),
            projectName: "FilterTest",
            testProjectPath: "tests/FilterTest/FilterTest.csproj");

        TestDataBuilder.CreateTestsPartitionsJson(
            Path.Combine(artifactsDir, "FilterTest.tests-partitions.json"),
            "MyPartition");

        var outputFile = Path.Combine(_tempDir.Path, "matrix.json");

        // Act
        var result = await RunScript(artifactsDir, outputFile);

        // Assert
        result.EnsureSuccessful();

        var matrix = ParseCanonicalMatrix(outputFile);
        var partitionEntry = matrix.Tests.First(e => e.Collection == "MyPartition");
        Assert.Equal("--filter-trait \"Partition=MyPartition\"", partitionEntry.ExtraTestArgs);
    }

    [Fact]
    [RequiresTools(["pwsh"])]
    public async Task CreatesEmptyMatrixWhenNoMetadataFiles()
    {
        // Arrange
        var emptyArtifactsDir = Path.Combine(_tempDir.Path, "empty-artifacts");
        Directory.CreateDirectory(emptyArtifactsDir);

        var outputFile = Path.Combine(_tempDir.Path, "matrix.json");

        // Act
        var result = await RunScript(emptyArtifactsDir, outputFile);

        // Assert
        result.EnsureSuccessful();

        var matrix = ParseCanonicalMatrix(outputFile);
        Assert.Empty(matrix.Tests);
    }

    [Fact]
    [RequiresTools(["pwsh"])]
    public async Task UsesUncollectedMtpBaseArgsForUncollectedEntry()
    {
        // Arrange
        var artifactsDir = Path.Combine(_tempDir.Path, "artifacts");
        Directory.CreateDirectory(artifactsDir);

        TestDataBuilder.CreateSplitTestsMetadataJson(
            Path.Combine(artifactsDir, "SplitProject.tests-metadata.json"),
            projectName: "SplitProject",
            testProjectPath: "tests/SplitProject/SplitProject.csproj",
            shortName: "Split",
            mtpBaseArgs: "--hangdump-timeout 15m --timeout 30m",
            uncollectedMtpBaseArgs: "--hangdump-timeout 20m --timeout 45m");

        TestDataBuilder.CreateTestsPartitionsJson(
            Path.Combine(artifactsDir, "SplitProject.tests-partitions.json"),
            "PartitionA");

        var outputFile = Path.Combine(_tempDir.Path, "matrix.json");

        // Act
        var result = await RunScript(artifactsDir, outputFile);

        // Assert
        result.EnsureSuccessful();

        var matrix = ParseCanonicalMatrix(outputFile);

        // The partitioned entry should have regular mtpBaseArgs
        var partitionEntry = matrix.Tests.FirstOrDefault(e => e.Name == "Split-PartitionA");
        Assert.NotNull(partitionEntry);
        Assert.Equal("--hangdump-timeout 15m --timeout 30m", partitionEntry.MtpBaseArgs);

        // The uncollected entry should have uncollected-specific mtpBaseArgs
        var uncollectedEntry = matrix.Tests.FirstOrDefault(e => e.Name == "Split");
        Assert.NotNull(uncollectedEntry);
        Assert.Equal("--hangdump-timeout 20m --timeout 45m", uncollectedEntry.MtpBaseArgs);
    }

    [Fact]
    [RequiresTools(["pwsh"])]
    public async Task PassesRequiresTestSdkProperty()
    {
        // Arrange
        var artifactsDir = Path.Combine(_tempDir.Path, "artifacts");
        Directory.CreateDirectory(artifactsDir);

        TestDataBuilder.CreateTestsMetadataJson(
            Path.Combine(artifactsDir, "SdkProject.tests-metadata.json"),
            projectName: "SdkProject",
            testProjectPath: "tests/SdkProject/SdkProject.csproj",
            requiresTestSdk: true);

        var outputFile = Path.Combine(_tempDir.Path, "matrix.json");

        // Act
        var result = await RunScript(artifactsDir, outputFile);

        // Assert
        result.EnsureSuccessful();

        var matrix = ParseCanonicalMatrix(outputFile);
        var entry = Assert.Single(matrix.Tests);
        Assert.True(entry.Properties.GetValueOrDefault("requiresTestSdk"));
    }

    [Fact]
    [RequiresTools(["pwsh"])]
    public async Task PreservesSupportedOSes()
    {
        // Arrange
        var artifactsDir = Path.Combine(_tempDir.Path, "artifacts");
        Directory.CreateDirectory(artifactsDir);

        TestDataBuilder.CreateTestsMetadataJson(
            Path.Combine(artifactsDir, "LinuxOnly.tests-metadata.json"),
            projectName: "LinuxOnly",
            testProjectPath: "tests/LinuxOnly/LinuxOnly.csproj",
            supportedOSes: ["linux"]);

        var outputFile = Path.Combine(_tempDir.Path, "matrix.json");

        // Act
        var result = await RunScript(artifactsDir, outputFile);

        // Assert
        result.EnsureSuccessful();

        var matrix = ParseCanonicalMatrix(outputFile);
        var entry = Assert.Single(matrix.Tests);
        Assert.Single(entry.SupportedOSes);
        Assert.Contains("linux", entry.SupportedOSes);
    }

    [Fact]
    [RequiresTools(["pwsh"])]
    public async Task InheritsSupportedOSesToPartitionEntries()
    {
        // Arrange
        var artifactsDir = Path.Combine(_tempDir.Path, "artifacts");
        Directory.CreateDirectory(artifactsDir);

        TestDataBuilder.CreateSplitTestsMetadataJson(
            Path.Combine(artifactsDir, "OsRestrictedSplit.tests-metadata.json"),
            projectName: "OsRestrictedSplit",
            testProjectPath: "tests/OsRestrictedSplit/OsRestrictedSplit.csproj",
            shortName: "OsRestrict",
            supportedOSes: ["windows", "linux"]);

        TestDataBuilder.CreateTestsPartitionsJson(
            Path.Combine(artifactsDir, "OsRestrictedSplit.tests-partitions.json"),
            "PartA");

        var outputFile = Path.Combine(_tempDir.Path, "matrix.json");

        // Act
        var result = await RunScript(artifactsDir, outputFile);

        // Assert
        result.EnsureSuccessful();

        var matrix = ParseCanonicalMatrix(outputFile);
        // Both the partition entry and uncollected entry should have the same supportedOSes
        foreach (var entry in matrix.Tests)
        {
            Assert.Equal(2, entry.SupportedOSes.Length);
            Assert.Contains("windows", entry.SupportedOSes);
            Assert.Contains("linux", entry.SupportedOSes);
        }
    }

    [Fact]
    [RequiresTools(["pwsh"])]
    public async Task PassesThroughRunnersForRegularTests()
    {
        // Arrange
        var artifactsDir = Path.Combine(_tempDir.Path, "artifacts");
        Directory.CreateDirectory(artifactsDir);

        TestDataBuilder.CreateTestsMetadataJson(
            Path.Combine(artifactsDir, "CustomRunner.tests-metadata.json"),
            projectName: "CustomRunner",
            testProjectPath: "tests/CustomRunner/CustomRunner.csproj",
            runners: new Dictionary<string, string> { ["macos"] = "macos-latest-xlarge" });

        var outputFile = Path.Combine(_tempDir.Path, "matrix.json");

        // Act
        var result = await RunScript(artifactsDir, outputFile);

        // Assert
        result.EnsureSuccessful();

        var matrix = ParseCanonicalMatrix(outputFile);
        var entry = Assert.Single(matrix.Tests);
        Assert.NotNull(entry.Runners);
        Assert.Single(entry.Runners);
        Assert.Equal("macos-latest-xlarge", entry.Runners["macos"]);
    }

    [Fact]
    [RequiresTools(["pwsh"])]
    public async Task OmitsRunnersWhenNotSet()
    {
        // Arrange
        var artifactsDir = Path.Combine(_tempDir.Path, "artifacts");
        Directory.CreateDirectory(artifactsDir);

        TestDataBuilder.CreateTestsMetadataJson(
            Path.Combine(artifactsDir, "NoRunner.tests-metadata.json"),
            projectName: "NoRunner",
            testProjectPath: "tests/NoRunner/NoRunner.csproj");

        var outputFile = Path.Combine(_tempDir.Path, "matrix.json");

        // Act
        var result = await RunScript(artifactsDir, outputFile);

        // Assert
        result.EnsureSuccessful();

        var matrix = ParseCanonicalMatrix(outputFile);
        var entry = Assert.Single(matrix.Tests);
        Assert.Null(entry.Runners);
    }

    [Fact]
    [RequiresTools(["pwsh"])]
    public async Task PassesThroughRunnersForPartitionEntries()
    {
        // Arrange
        var artifactsDir = Path.Combine(_tempDir.Path, "artifacts");
        Directory.CreateDirectory(artifactsDir);

        TestDataBuilder.CreateSplitTestsMetadataJson(
            Path.Combine(artifactsDir, "SplitRunner.tests-metadata.json"),
            projectName: "SplitRunner",
            testProjectPath: "tests/SplitRunner/SplitRunner.csproj",
            shortName: "SplitR",
            runners: new Dictionary<string, string> { ["linux"] = "ubuntu-24.04" });

        TestDataBuilder.CreateTestsPartitionsJson(
            Path.Combine(artifactsDir, "SplitRunner.tests-partitions.json"),
            "PartA");

        var outputFile = Path.Combine(_tempDir.Path, "matrix.json");

        // Act
        var result = await RunScript(artifactsDir, outputFile);

        // Assert
        result.EnsureSuccessful();

        var matrix = ParseCanonicalMatrix(outputFile);
        // All entries (partition + uncollected) should inherit the runners
        foreach (var entry in matrix.Tests)
        {
            Assert.NotNull(entry.Runners);
            Assert.Equal("ubuntu-24.04", entry.Runners["linux"]);
        }
    }

    [Fact]
    [RequiresTools(["pwsh"])]
    public async Task PassesThroughRunnersForClassEntries()
    {
        // Arrange
        var artifactsDir = Path.Combine(_tempDir.Path, "artifacts");
        Directory.CreateDirectory(artifactsDir);

        TestDataBuilder.CreateSplitTestsMetadataJson(
            Path.Combine(artifactsDir, "ClassRunner.tests-metadata.json"),
            projectName: "ClassRunner",
            testProjectPath: "tests/ClassRunner/ClassRunner.csproj",
            shortName: "ClassR",
            runners: new Dictionary<string, string>
            {
                ["windows"] = "windows-2022",
                ["macos"] = "macos-latest-xlarge"
            });

        TestDataBuilder.CreateClassBasedPartitionsJson(
            Path.Combine(artifactsDir, "ClassRunner.tests-partitions.json"),
            "Ns.ClassA");

        var outputFile = Path.Combine(_tempDir.Path, "matrix.json");

        // Act
        var result = await RunScript(artifactsDir, outputFile);

        // Assert
        result.EnsureSuccessful();

        var matrix = ParseCanonicalMatrix(outputFile);
        var entry = Assert.Single(matrix.Tests);
        Assert.NotNull(entry.Runners);
        Assert.Equal(2, entry.Runners.Count);
        Assert.Equal("windows-2022", entry.Runners["windows"]);
        Assert.Equal("macos-latest-xlarge", entry.Runners["macos"]);
    }

    [Fact]
    [RequiresTools(["pwsh"])]
    public async Task AllCITestsPropertiesAppearInOutputWithDefaults()
    {
        // Verifies that every property defined in CITestsProperties.props
        // appears in the canonical matrix output with its default value
        // when the input metadata doesn't set any properties to true.
        var expectedProperties = ReadCITestsPropertyNames();

        var artifactsDir = Path.Combine(_tempDir.Path, "artifacts");
        Directory.CreateDirectory(artifactsDir);

        TestDataBuilder.CreateTestsMetadataJson(
            Path.Combine(artifactsDir, "DefaultProps.tests-metadata.json"),
            projectName: "DefaultProps",
            testProjectPath: "tests/DefaultProps/DefaultProps.csproj");

        var outputFile = Path.Combine(_tempDir.Path, "matrix.json");

        var result = await RunScript(artifactsDir, outputFile);

        result.EnsureSuccessful();

        var matrix = ParseCanonicalMatrix(outputFile);
        var entry = Assert.Single(matrix.Tests);

        foreach (var propName in expectedProperties)
        {
            Assert.True(entry.Properties.ContainsKey(propName),
                $"Expected property '{propName}' from CITestsProperties.props to be present in matrix output, but it was missing.");
            Assert.False(entry.Properties[propName],
                $"Expected property '{propName}' to have default value 'false', but it was 'true'.");
        }
    }

    [Fact]
    [RequiresTools(["pwsh"])]
    public async Task DefaultsAreAppliedWhenPropertiesAreMissingFromMetadata()
    {
        // Create metadata JSON manually with a partial properties object
        // (only requiresNugets=true, everything else omitted) to verify
        // that the script fills in defaults from CITestsProperties.props.
        var expectedProperties = ReadCITestsPropertyNames();

        var artifactsDir = Path.Combine(_tempDir.Path, "artifacts");
        Directory.CreateDirectory(artifactsDir);

        var partialMetadata = """
            {
              "projectName": "PartialProps",
              "testProjectPath": "tests/PartialProps/PartialProps.csproj",
              "shortName": "PartialProps",
              "splitTests": "false",
              "properties": {
                "requiresNugets": true
              },
              "supportedOSes": ["windows", "linux"]
            }
            """;
        File.WriteAllText(
            Path.Combine(artifactsDir, "PartialProps.tests-metadata.json"),
            partialMetadata);

        var outputFile = Path.Combine(_tempDir.Path, "matrix.json");

        var result = await RunScript(artifactsDir, outputFile);

        result.EnsureSuccessful();

        var matrix = ParseCanonicalMatrix(outputFile);
        var entry = Assert.Single(matrix.Tests);

        // requiresNugets should be true (from input)
        Assert.True(entry.Properties["requiresNugets"]);

        // All other properties should be present with their default value (false)
        foreach (var propName in expectedProperties.Where(p => p != "requiresNugets"))
        {
            Assert.True(entry.Properties.ContainsKey(propName),
                $"Expected property '{propName}' to be filled in by defaults, but it was missing.");
            Assert.False(entry.Properties[propName],
                $"Expected property '{propName}' default to be 'false', but it was 'true'.");
        }
    }

    [Fact]
    public void CITestsPropertiesPropsFileIsValidAndComplete()
    {
        // Validates that CITestsProperties.props is well-formed XML
        // and contains the expected property definitions.
        var propsPath = Path.Combine(FindRepoRoot(), "eng", "testing", "CITestsProperties.props");
        Assert.True(File.Exists(propsPath), $"CITestsProperties.props not found at {propsPath}");

        var doc = new System.Xml.XmlDocument();
        doc.Load(propsPath);

        var items = doc.SelectNodes("/Project/ItemGroup/CITestsProperty");
        Assert.NotNull(items);
        Assert.True(items.Count > 0, "CITestsProperties.props should define at least one CITestsProperty item.");

        foreach (System.Xml.XmlElement item in items)
        {
            var include = item.GetAttribute("Include");
            var msbuildProp = item.GetAttribute("MSBuildProp");
            var defaultVal = item.GetAttribute("Default");

            Assert.False(string.IsNullOrWhiteSpace(include),
                "Each CITestsProperty must have an Include attribute (JSON key name).");
            Assert.False(string.IsNullOrWhiteSpace(msbuildProp),
                $"CITestsProperty '{include}' must have an MSBuildProp attribute.");
            Assert.False(string.IsNullOrWhiteSpace(defaultVal),
                $"CITestsProperty '{include}' must have a Default attribute.");

            // JSON keys should be camelCase (start with lowercase)
            Assert.True(char.IsLower(include[0]),
                $"CITestsProperty Include '{include}' should be camelCase (start with lowercase).");

            // MSBuild properties should be PascalCase (start with uppercase)
            Assert.True(char.IsUpper(msbuildProp[0]),
                $"CITestsProperty MSBuildProp '{msbuildProp}' should be PascalCase (start with uppercase).");
        }
    }

    [Fact]
    [RequiresTools(["pwsh"])]
    public async Task SplitTestEntriesInheritAllCITestsProperties()
    {
        // Verifies that partition-based split test entries also get
        // all properties from CITestsProperties.props.
        var expectedProperties = ReadCITestsPropertyNames();

        var artifactsDir = Path.Combine(_tempDir.Path, "artifacts");
        Directory.CreateDirectory(artifactsDir);

        TestDataBuilder.CreateSplitTestsMetadataJson(
            Path.Combine(artifactsDir, "SplitProps.tests-metadata.json"),
            projectName: "SplitProps",
            testProjectPath: "tests/SplitProps/SplitProps.csproj",
            shortName: "SplitP",
            requiresNugets: true);

        TestDataBuilder.CreateTestsPartitionsJson(
            Path.Combine(artifactsDir, "SplitProps.tests-partitions.json"),
            "PartA");

        var outputFile = Path.Combine(_tempDir.Path, "matrix.json");

        var result = await RunScript(artifactsDir, outputFile);

        result.EnsureSuccessful();

        var matrix = ParseCanonicalMatrix(outputFile);
        Assert.True(matrix.Tests.Length >= 2, "Expected at least 2 entries (partition + uncollected).");

        foreach (var entry in matrix.Tests)
        {
            foreach (var propName in expectedProperties)
            {
                Assert.True(entry.Properties.ContainsKey(propName),
                    $"Split entry '{entry.Name}' is missing property '{propName}'.");
            }
            Assert.True(entry.Properties["requiresNugets"],
                $"Split entry '{entry.Name}' should have requiresNugets=true.");
        }
    }

    private async Task<CommandResult> RunScript(string artifactsDir, string outputFile)
    {
        using var cmd = new PowerShellCommand(_scriptPath, _output)
            .WithTimeout(TimeSpan.FromMinutes(2));

        return await cmd.ExecuteAsync(
            "-ArtifactsDir", $"\"{artifactsDir}\"",
            "-OutputMatrixFile", $"\"{outputFile}\"");
    }

    private static CanonicalMatrix ParseCanonicalMatrix(string path)
    {
        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<CanonicalMatrix>(json)
            ?? throw new InvalidOperationException("Failed to parse matrix JSON");
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

    /// <summary>
    /// Reads the CITestsProperties.props XML file and returns
    /// the list of property names (Include attributes).
    /// </summary>
    private static HashSet<string> ReadCITestsPropertyNames()
    {
        var propsPath = Path.Combine(FindRepoRoot(), "eng", "testing", "CITestsProperties.props");
        var doc = new System.Xml.XmlDocument();
        doc.Load(propsPath);

        var items = doc.SelectNodes("/Project/ItemGroup/CITestsProperty")
            ?? throw new InvalidOperationException("No CITestsProperty items found in CITestsProperties.props");

        var names = new HashSet<string>();
        foreach (System.Xml.XmlElement item in items)
        {
            names.Add(item.GetAttribute("Include"));
        }
        return names;
    }
}
