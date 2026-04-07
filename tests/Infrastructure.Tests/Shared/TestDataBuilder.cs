// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Infrastructure.Tests;

/// <summary>
/// Helper to create test input JSON files for PowerShell scripts.
/// </summary>
public static class TestDataBuilder
{
    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>
    /// Creates a .tests-metadata.json file for a regular (non-split) test project.
    /// </summary>
    public static string CreateTestsMetadataJson(
        string outputPath,
        string projectName,
        string testProjectPath,
        string? shortName = null,
        string? testSessionTimeout = null,
        string? testHangTimeout = null,
        bool requiresNugets = false,
        bool requiresTestSdk = false,
        bool requiresCliArchive = false,
        string? extraTestArgs = null,
        string[]? supportedOSes = null,
        Dictionary<string, string>? runners = null)
    {
        var metadata = new TestMetadata
        {
            ProjectName = projectName,
            TestProjectPath = testProjectPath,
            ShortName = shortName ?? projectName,
            SplitTests = "false",
            TestSessionTimeout = testSessionTimeout,
            TestHangTimeout = testHangTimeout,
            RequiresNugets = requiresNugets ? "true" : null,
            RequiresTestSdk = requiresTestSdk ? "true" : null,
            RequiresCliArchive = requiresCliArchive ? "true" : null,
            ExtraTestArgs = extraTestArgs,
            SupportedOSes = supportedOSes ?? ["windows", "linux", "macos"],
            Runners = runners
        };

        var json = JsonSerializer.Serialize(metadata, s_jsonOptions);
        var outputDir = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(outputDir) && !Directory.Exists(outputDir))
        {
            Directory.CreateDirectory(outputDir);
        }
        File.WriteAllText(outputPath, json);
        return outputPath;
    }

    /// <summary>
    /// Creates a .tests-metadata.json file for a split test project.
    /// </summary>
    public static string CreateSplitTestsMetadataJson(
        string outputPath,
        string projectName,
        string testProjectPath,
        string? shortName = null,
        string? testSessionTimeout = null,
        string? testHangTimeout = null,
        string? uncollectedTestsSessionTimeout = null,
        string? uncollectedTestsHangTimeout = null,
        bool requiresNugets = false,
        bool requiresTestSdk = false,
        string[]? supportedOSes = null,
        Dictionary<string, string>? runners = null)
    {
        var metadata = new TestMetadata
        {
            ProjectName = projectName,
            TestProjectPath = testProjectPath,
            ShortName = shortName ?? projectName,
            SplitTests = "true",
            TestSessionTimeout = testSessionTimeout,
            TestHangTimeout = testHangTimeout,
            UncollectedTestsSessionTimeout = uncollectedTestsSessionTimeout,
            UncollectedTestsHangTimeout = uncollectedTestsHangTimeout,
            RequiresNugets = requiresNugets ? "true" : null,
            RequiresTestSdk = requiresTestSdk ? "true" : null,
            SupportedOSes = supportedOSes ?? ["windows", "linux", "macos"],
            Runners = runners
        };

        var json = JsonSerializer.Serialize(metadata, s_jsonOptions);
        var outputDir = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(outputDir) && !Directory.Exists(outputDir))
        {
            Directory.CreateDirectory(outputDir);
        }
        File.WriteAllText(outputPath, json);
        return outputPath;
    }

    /// <summary>
    /// Creates a .tests-partitions.json file with collection-based partitions.
    /// </summary>
    public static string CreateTestsPartitionsJson(
        string outputPath,
        params string[] partitionNames)
    {
        var partitions = new List<string>();
        foreach (var name in partitionNames)
        {
            partitions.Add($"collection:{name}");
        }
        partitions.Add("uncollected:*");

        var data = new TestPartitionsJson { TestPartitions = partitions.ToArray() };
        var json = JsonSerializer.Serialize(data, s_jsonOptions);
        var outputDir = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(outputDir) && !Directory.Exists(outputDir))
        {
            Directory.CreateDirectory(outputDir);
        }
        File.WriteAllText(outputPath, json);
        return outputPath;
    }

    /// <summary>
    /// Creates a .tests-partitions.json file with class-based entries.
    /// </summary>
    public static string CreateClassBasedPartitionsJson(
        string outputPath,
        params string[] classNames)
    {
        var partitions = classNames.Select(c => $"class:{c}").ToArray();
        var data = new TestPartitionsJson { TestPartitions = partitions };
        var json = JsonSerializer.Serialize(data, s_jsonOptions);
        var outputDir = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(outputDir) && !Directory.Exists(outputDir))
        {
            Directory.CreateDirectory(outputDir);
        }
        File.WriteAllText(outputPath, json);
        return outputPath;
    }

    /// <summary>
    /// Creates a canonical test matrix JSON file (output of build-test-matrix.ps1).
    /// </summary>
    public static string CreateCanonicalMatrixJson(
        string outputPath,
        CanonicalMatrixEntry[]? tests = null)
    {
        var matrix = new CanonicalMatrix
        {
            Tests = tests ?? []
        };

        var json = JsonSerializer.Serialize(matrix, s_jsonOptions);
        var outputDir = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(outputDir) && !Directory.Exists(outputDir))
        {
            Directory.CreateDirectory(outputDir);
        }
        File.WriteAllText(outputPath, json);
        return outputPath;
    }

    /// <summary>
    /// Creates a single canonical matrix entry.
    /// </summary>
    public static CanonicalMatrixEntry CreateMatrixEntry(
        string name,
        string projectName,
        string testProjectPath,
        string type = "regular",
        string? shortname = null,
        string? workitemprefix = null,
        string? collection = null,
        string? classname = null,
        string? extraTestArgs = null,
        string testSessionTimeout = "20m",
        string testHangTimeout = "10m",
        bool requiresNugets = false,
        bool requiresTestSdk = false,
        bool requiresCliArchive = false,
        string[]? supportedOSes = null,
        Dictionary<string, string>? runners = null)
    {
        return new CanonicalMatrixEntry
        {
            Type = type,
            Name = name,
            ProjectName = projectName,
            TestProjectPath = testProjectPath,
            Shortname = shortname ?? name,
            Workitemprefix = workitemprefix ?? projectName,
            Collection = collection,
            Classname = classname,
            ExtraTestArgs = extraTestArgs ?? "",
            TestSessionTimeout = testSessionTimeout,
            TestHangTimeout = testHangTimeout,
            RequiresNugets = requiresNugets,
            RequiresTestSdk = requiresTestSdk,
            RequiresCliArchive = requiresCliArchive,
            SupportedOSes = supportedOSes ?? ["windows", "linux", "macos"],
            Runners = runners
        };
    }

    private sealed class TestMetadata
    {
        [JsonPropertyName("projectName")]
        public string ProjectName { get; set; } = "";

        [JsonPropertyName("testProjectPath")]
        public string TestProjectPath { get; set; } = "";

        [JsonPropertyName("shortName")]
        public string ShortName { get; set; } = "";

        [JsonPropertyName("splitTests")]
        public string SplitTests { get; set; } = "false";

        [JsonPropertyName("testSessionTimeout")]
        public string? TestSessionTimeout { get; set; }

        [JsonPropertyName("testHangTimeout")]
        public string? TestHangTimeout { get; set; }

        [JsonPropertyName("uncollectedTestsSessionTimeout")]
        public string? UncollectedTestsSessionTimeout { get; set; }

        [JsonPropertyName("uncollectedTestsHangTimeout")]
        public string? UncollectedTestsHangTimeout { get; set; }

        [JsonPropertyName("requiresNugets")]
        public string? RequiresNugets { get; set; }

        [JsonPropertyName("requiresTestSdk")]
        public string? RequiresTestSdk { get; set; }

        [JsonPropertyName("requiresCliArchive")]
        public string? RequiresCliArchive { get; set; }

        [JsonPropertyName("extraTestArgs")]
        public string? ExtraTestArgs { get; set; }

        [JsonPropertyName("supportedOSes")]
        public string[] SupportedOSes { get; set; } = ["windows", "linux", "macos"];

        [JsonPropertyName("runners")]
        public Dictionary<string, string>? Runners { get; set; }
    }

    private sealed class TestPartitionsJson
    {
        [JsonPropertyName("testPartitions")]
        public string[] TestPartitions { get; set; } = [];
    }
}

/// <summary>
/// Represents the canonical test matrix output.
/// </summary>
public class CanonicalMatrix
{
    [JsonPropertyName("tests")]
    public CanonicalMatrixEntry[] Tests { get; set; } = [];
}

/// <summary>
/// Represents a single entry in the canonical test matrix.
/// </summary>
public class CanonicalMatrixEntry
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "regular";

    [JsonPropertyName("projectName")]
    public string ProjectName { get; set; } = "";

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("shortname")]
    public string Shortname { get; set; } = "";

    [JsonPropertyName("testProjectPath")]
    public string TestProjectPath { get; set; } = "";

    [JsonPropertyName("workitemprefix")]
    public string Workitemprefix { get; set; } = "";

    [JsonPropertyName("collection")]
    public string? Collection { get; set; }

    [JsonPropertyName("classname")]
    public string? Classname { get; set; }

    [JsonPropertyName("extraTestArgs")]
    public string ExtraTestArgs { get; set; } = "";

    [JsonPropertyName("testSessionTimeout")]
    public string TestSessionTimeout { get; set; } = "20m";

    [JsonPropertyName("testHangTimeout")]
    public string TestHangTimeout { get; set; } = "10m";

    [JsonPropertyName("requiresNugets")]
    public bool RequiresNugets { get; set; }

    [JsonPropertyName("requiresTestSdk")]
    public bool RequiresTestSdk { get; set; }

    [JsonPropertyName("requiresCliArchive")]
    public bool RequiresCliArchive { get; set; }

    [JsonPropertyName("splitTests")]
    public bool SplitTests { get; set; }

    [JsonPropertyName("supportedOSes")]
    public string[] SupportedOSes { get; set; } = ["windows", "linux", "macos"];

    [JsonPropertyName("runners")]
    public Dictionary<string, string>? Runners { get; set; }
}

/// <summary>
/// Represents an expanded GitHub Actions matrix entry.
/// </summary>
public class ExpandedMatrixEntry : CanonicalMatrixEntry
{
    [JsonPropertyName("runs-on")]
    public string RunsOn { get; set; } = "";
}

/// <summary>
/// Represents the GitHub Actions matrix format.
/// </summary>
public class GitHubActionsMatrix
{
    [JsonPropertyName("include")]
    public ExpandedMatrixEntry[] Include { get; set; } = [];
}
