// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace TestSelector.Models;

/// <summary>
/// Result of MSBuild-based test selection evaluation.
/// </summary>
public sealed class TestSelectionResult
{
    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>
    /// Whether to run all tests (critical path triggered or conservative fallback).
    /// </summary>
    [JsonPropertyName("runAllTests")]
    public bool RunAllTests { get; set; }

    /// <summary>
    /// Reason for the test selection decision.
    /// </summary>
    [JsonPropertyName("reason")]
    public string Reason { get; set; } = "";

    /// <summary>
    /// File that triggered the decision (if applicable).
    /// </summary>
    [JsonPropertyName("triggerFile")]
    public string? TriggerFile { get; set; }

    /// <summary>
    /// Pattern that triggered the decision (if applicable).
    /// </summary>
    [JsonPropertyName("triggerPattern")]
    public string? TriggerPattern { get; set; }

    /// <summary>
    /// Category flags for CI consumption.
    /// </summary>
    [JsonPropertyName("categories")]
    public Dictionary<string, bool> Categories { get; set; } = [];

    /// <summary>
    /// All affected test projects (from dotnet-affected + category projects).
    /// </summary>
    [JsonPropertyName("affectedTestProjects")]
    public List<string> AffectedTestProjects { get; set; } = [];

    /// <summary>
    /// Integration test projects specifically (for matrix builds).
    /// </summary>
    [JsonPropertyName("integrationsProjects")]
    public List<string> IntegrationsProjects { get; set; } = [];

    /// <summary>
    /// Information about NuGet-dependent tests.
    /// </summary>
    [JsonPropertyName("nugetDependentTests")]
    public NuGetDependentTestsInfo? NuGetDependentTests { get; set; }

    /// <summary>
    /// Files that were changed but not ignored.
    /// </summary>
    [JsonPropertyName("changedFiles")]
    public List<string> ChangedFiles { get; set; } = [];

    /// <summary>
    /// Files that were ignored based on ignorePaths.
    /// </summary>
    [JsonPropertyName("ignoredFiles")]
    public List<string> IgnoredFiles { get; set; } = [];

    /// <summary>
    /// Projects affected by dotnet-affected (source + test projects).
    /// </summary>
    [JsonPropertyName("dotnetAffectedProjects")]
    public List<string> DotnetAffectedProjects { get; set; } = [];

    /// <summary>
    /// Error message if evaluation failed.
    /// </summary>
    [JsonPropertyName("error")]
    public string? Error { get; set; }

    /// <summary>
    /// Serializes the result to JSON.
    /// </summary>
    public string ToJson()
    {
        return JsonSerializer.Serialize(this, s_jsonOptions);
    }

    /// <summary>
    /// Creates a result indicating all tests should run due to a critical path match.
    /// </summary>
    public static TestSelectionResult CriticalPath(string file, string pattern)
    {
        return new TestSelectionResult
        {
            RunAllTests = true,
            Reason = "critical_path",
            TriggerFile = file,
            TriggerPattern = pattern
        };
    }

    /// <summary>
    /// Creates a result indicating no tests need to run.
    /// </summary>
    public static TestSelectionResult NoChanges()
    {
        return new TestSelectionResult
        {
            RunAllTests = false,
            Reason = "no_changes"
        };
    }

    /// <summary>
    /// Creates a result indicating all files were ignored.
    /// </summary>
    public static TestSelectionResult AllIgnored(List<string> ignoredFiles)
    {
        return new TestSelectionResult
        {
            RunAllTests = false,
            Reason = "all_ignored",
            IgnoredFiles = ignoredFiles
        };
    }

    /// <summary>
    /// Creates a result indicating an error occurred.
    /// </summary>
    public static TestSelectionResult WithError(string error)
    {
        return new TestSelectionResult
        {
            RunAllTests = true, // Conservative: run all on error
            Reason = "error",
            Error = error
        };
    }

    /// <summary>
    /// Creates a result indicating all tests should run (conservative fallback).
    /// </summary>
    public static TestSelectionResult RunAll(string reason)
    {
        return new TestSelectionResult
        {
            RunAllTests = true,
            Reason = reason
        };
    }

    /// <summary>
    /// Creates a result indicating the change was observed but should not apply execution changes.
    /// </summary>
    public static TestSelectionResult NonApplying(string reason)
    {
        return new TestSelectionResult
        {
            RunAllTests = false,
            Reason = reason
        };
    }

    /// <summary>
    /// Writes the result in GitHub Actions output format.
    /// </summary>
    public void WriteGitHubOutput()
    {
        var outputPath = Environment.GetEnvironmentVariable("GITHUB_OUTPUT");
        var outputs = new List<KeyValuePair<string, string>>
        {
            new("run_all", RunAllTests.ToString().ToLowerInvariant()),
            new("selection_reason", Reason)
        };

        // Output run_integrations based on both the category trigger status AND whether
        // there are integration test projects discovered via dotnet-affected/sourceToTestMappings.
        var runIntegrations = RunAllTests || AffectedTestProjects.Count > 0;

        foreach (var (category, enabled) in Categories)
        {
            if (category == "integrations")
            {
                // Merge: integrations runs if triggered by paths OR if test projects were discovered
                var integrationsEnabled = enabled || runIntegrations;
                outputs.Add(new("run_integrations", integrationsEnabled.ToString().ToLowerInvariant()));
            }
            else
            {
                outputs.Add(new($"run_{category}", enabled.ToString().ToLowerInvariant()));
            }
        }

        if (!Categories.ContainsKey("integrations"))
        {
            outputs.Add(new("run_integrations", runIntegrations.ToString().ToLowerInvariant()));
        }

        // Output affected test projects as JSON array of .csproj paths for matrix filtering
        outputs.Add(new("affected_test_projects", JsonSerializer.Serialize(AffectedTestProjects)));

        // NuGet-dependent test outputs
        var nugetTriggered = NuGetDependentTests?.Triggered == true;
        outputs.Add(new("run_nuget_tests", nugetTriggered.ToString().ToLowerInvariant()));
        outputs.Add(new("nuget_test_projects", JsonSerializer.Serialize(NuGetDependentTests?.Projects ?? [])));

        if (!string.IsNullOrEmpty(outputPath))
        {
            using var writer = new StreamWriter(outputPath, append: true);
            foreach (var (name, value) in outputs)
            {
                WriteGitHubOutputValue(writer, name, value);
            }
        }
        else
        {
            foreach (var (name, value) in outputs)
            {
                Console.WriteLine($"{name}={value}");
            }
        }
    }

    private static void WriteGitHubOutputValue(TextWriter writer, string name, string value)
    {
        if (value.Contains('\n') || value.Contains('\r'))
        {
            var delimiter = $"EOF_{Guid.NewGuid():N}";
            writer.WriteLine($"{name}<<{delimiter}");
            writer.WriteLine(value);
            writer.WriteLine(delimiter);
            return;
        }

        writer.WriteLine($"{name}={value}");
    }
}

/// <summary>
/// Information about NuGet-dependent test projects.
/// </summary>
public sealed class NuGetDependentTestsInfo
{
    /// <summary>
    /// Whether NuGet-dependent tests should be triggered.
    /// </summary>
    [JsonPropertyName("triggered")]
    public bool Triggered { get; set; }

    /// <summary>
    /// Reason for triggering (which packable projects were affected).
    /// </summary>
    [JsonPropertyName("reason")]
    public string Reason { get; set; } = "";

    /// <summary>
    /// List of affected packable projects that caused the trigger.
    /// </summary>
    [JsonPropertyName("affectedPackableProjects")]
    public List<string> AffectedPackableProjects { get; set; } = [];

    /// <summary>
    /// NuGet-dependent test projects that should run.
    /// </summary>
    [JsonPropertyName("projects")]
    public List<string> Projects { get; set; } = [];
}
