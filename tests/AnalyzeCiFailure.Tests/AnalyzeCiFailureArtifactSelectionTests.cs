// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using System.Text.Json;
using VerifyXunit;
using Xunit;

namespace Infrastructure.Tests;

/// <summary>Exercises artifact matching, deterministic budgets and bounded archive extraction.</summary>
public sealed partial class AnalyzeCiFailureWorkflowTests
{
    private const string RequiredSelectionJob =
        """{"name":"Custom / Infrastructure (ubuntu-latest)","steps":[null,5,{"name":false},{"name":"Upload logs, and test results"}]}""";
    private const string OptionalSelectionJob =
        """{"name":"Run VS Code extension E2E tests / VS Code extension E2E (Linux, debug)","steps":"invalid"}""";

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task TestResultArtifactSelectorProjectsOnlyMatchingMetadata(bool optional, bool valid)
    {
        var name = optional ? "extension-e2e-diagnostics-linux-x64-debug-attempt1" : "logs-Infrastructure-ubuntu-latest";
        var artifacts = $$"""
            [
              null, 7, {"name":false,"expired":false},
              {"name":"irrelevant","expired":false,"created_at":"2026-09-04T12:01:00Z","id":{},"size_in_bytes":"invalid"},
              {"name":"{{name}}","expired":false,"created_at":"2026-09-04T12:01:00Z","id":{{(valid ? "10" : "{}")}},"size_in_bytes":{{(valid ? "4" : "\"invalid\"")}}}
            ]
            """;
        var jobs = $"[null,7,{{\"name\":false,\"steps\":[null]}},{{\"name\":\"Build\",\"steps\":false}},{(optional ? OptionalSelectionJob : RequiredSelectionJob)}]";

        var result = await RunArtifactSelectionFixtureAsync(artifacts, jobs);

        if (!valid)
        {
            Assert.Equal(optional ? 0 : 1, result.ExitCode);
            Assert.Equal(
                optional ? "[]\n" :
                "::error::test result artifact has invalid or excessive size metadata: job='Custom / Infrastructure (ubuntu-latest)' artifact='logs-Infrastructure-ubuntu-latest' id={} size=invalid limit=104857600\n",
                result.Output);
            return;
        }

        Assert.True(result.ExitCode == 0, result.Output);
        await Verifier.Verify(result.Output, "json").UseDirectory("Snapshots").UseParameters(optional, valid);
    }

    [Theory]
    [InlineData("10.0", "1e1", false)]
    [InlineData("1e1", "10.0", false)]
    [InlineData("10.00000000000000001", "1.00000000000000001", false)]
    [InlineData("10", "1e1", true)]
    [InlineData("10", "10.0", true)]
    [InlineData("10.5", "1", false)]
    [InlineData("10", "1.5", false)]
    [InlineData("10", "1e-400", false)]
    [InlineData("10", "10.00000000000000001", false)]
    public async Task TestResultArtifactSelectorRejectsNonIntegralIdsAndPreservesBoundedSizeArithmetic(string id, string size, bool accepted)
    {
        var result = await RunArtifactSelectionFixtureAsync(
            $$"""[{"id":{{id}},"name":"logs-Infrastructure-ubuntu-latest","expired":false,"created_at":"2026-09-04T12:01:00Z","size_in_bytes":{{size}}}]""",
            $"[{RequiredSelectionJob}]",
            maxArtifacts: 1,
            maxTotalBytes: 10,
            maxArtifactBytes: 10);

        Assert.Equal(accepted ? 0 : 1, result.ExitCode);
        if (!accepted)
        {
            Assert.Equal($"::error::test result artifact has invalid or excessive size metadata: job='Custom / Infrastructure (ubuntu-latest)' artifact='logs-Infrastructure-ubuntu-latest' id={id} size={size} limit=10\n", result.Output);
            return;
        }

        using var selected = JsonDocument.Parse(result.Output);
        var artifact = Assert.Single(selected.RootElement.EnumerateArray());
        Assert.Equal(id, artifact.GetProperty("id").GetRawText());
        Assert.Equal(size, artifact.GetProperty("size_in_bytes").GetRawText());
        await Verifier.Verify(result.Output, "json").UseDirectory("Snapshots").UseParameters(id, size);
    }

    [Theory]
    [InlineData("10", "10", false, true)]
    [InlineData("10", "10", true, true)]
    [InlineData("9007199254740992", "9007199254740993", false, false)]
    public async Task TestResultArtifactSelectorUsesExactNumericDuplicateIdentity(
        string firstId,
        string secondId,
        bool optional,
        bool duplicate)
    {
        var secondName = optional ? "extension-e2e-diagnostics-linux-x64-debug-attempt1" : "logs-Other-ubuntu-latest";
        var secondJob = optional ? OptionalSelectionJob :
            """{"name":"Custom / Other (ubuntu-latest)","steps":[{"name":"Upload logs, and test results"}]}""";
        var result = await RunArtifactSelectionFixtureAsync(
            $$"""
            [
              {"id":{{firstId}},"name":"logs-Infrastructure-ubuntu-latest","expired":false,"created_at":"2026-09-04T12:01:00Z","size_in_bytes":1},
              {"id":{{secondId}},"name":"{{secondName}}","expired":false,"created_at":"2026-09-04T12:01:00Z","size_in_bytes":1}
            ]
            """,
            $"[{RequiredSelectionJob},{secondJob}]");

        Assert.Equal(duplicate ? 1 : 0, result.ExitCode);
        if (duplicate)
        {
            Assert.Equal(
                optional
                ? "::error::test result artifact does not identify exactly one failed job: repeated artifact IDs among selected results\n"
                : "::error::test result artifact does not identify exactly one failed job: repeated artifact IDs among required results\n",
                result.Output);
            return;
        }

        await Verifier.Verify(result.Output, "json").UseDirectory("Snapshots").UseParameters(firstId, secondId);
    }

    [Theory]
    [InlineData("\"2026-09-04T12:00:00Z\"", "false", false)]
    [InlineData("\"2026-09-04T12:02:00Z\"", "false", true)]
    [InlineData("\"2026-09-04T12:02:01Z\"", "false", false)]
    [InlineData("\"2026-09-04T12:01:invalidZ\"", "false", true)]
    [InlineData("null", "false", false)]
    [InlineData("false", "false", false)]
    [InlineData("\"2026-09-04T12:01:00Z\"", "true", false)]
    [InlineData("\"2026-09-04T12:01:00Z\"", "", false)]
    [InlineData("\"2026-09-04T12:01:00Z\"", "null", false)]
    [InlineData("\"2026-09-04T12:01:00Z\"", "\"false\"", false)]
    [InlineData("\"2026-09-04T12:01:00Z\"", "0", false)]
    [InlineData("\"2026-09-04T12:01:00Z\"", "{}", false)]
    public async Task TestResultArtifactSelectorPreservesOrdinalWindowAndExplicitExpiration(
        string createdAt,
        string expired,
        bool accepted)
    {
        var expiration = expired.Length == 0 ? "" : $"\"expired\":{expired},";
        var result = await RunArtifactSelectionFixtureAsync(
            $$"""[{"id":10,"name":"logs-Infrastructure-ubuntu-latest",{{expiration}}"created_at":{{createdAt}},"size_in_bytes":1}]""",
            $"[{RequiredSelectionJob}]");

        Assert.Equal(accepted ? 0 : 1, result.ExitCode);
        if (!accepted)
        {
            Assert.Equal("::error::test result artifact is missing for a failed test job: job='Custom / Infrastructure (ubuntu-latest)' artifact='logs-Infrastructure-ubuntu-latest'\n", result.Output);
            return;
        }

        using var selected = JsonDocument.Parse(result.Output);
        Assert.Equal(10, Assert.Single(selected.RootElement.EnumerateArray()).GetProperty("id").GetInt32());
    }

    [Theory]
    [InlineData(1, 100)]
    [InlineData(20, 4)]
    [InlineData(3, 12)]
    public async Task TestResultArtifactSelectorBudgetsRequiredArtifactsBeforeDeterministicallyOrderedDiagnostics(
        int maxArtifacts,
        long maxTotalBytes)
    {
        var result = await RunArtifactSelectionFixtureAsync(
            """
            [
              {"id":20,"name":"extension-e2e-diagnostics-linux-x64-debug-attempt1","expired":false,"created_at":"2026-09-04T12:01:00Z","size_in_bytes":4},
              {"id":40,"name":"extension-e2e-diagnostics-win-x64-debug-attempt1","expired":false,"created_at":"2026-09-04T12:01:00Z","size_in_bytes":4},
              {"id":30,"name":"extension-e2e-diagnostics-linux-x64-command-palette-attempt1","expired":false,"created_at":"2026-09-04T12:01:00Z","size_in_bytes":4},
              {"id":10,"name":"logs-Infrastructure-ubuntu-latest","expired":false,"created_at":"2026-09-04T12:01:00Z","size_in_bytes":4}
            ]
            """,
            $$"""
            [
              {{OptionalSelectionJob}},
              {"name":"Run VS Code extension E2E tests / VS Code extension E2E (Windows, debug)"},
              {"name":"Run VS Code extension E2E tests / VS Code extension E2E (Linux, command-palette)"},
              {{RequiredSelectionJob}}
            ]
            """,
            maxArtifacts,
            maxTotalBytes,
            maxArtifactBytes: 4);

        Assert.True(result.ExitCode == 0, result.Output);
        await Verifier.Verify(result.Output, "json").UseDirectory("Snapshots").UseParameters(maxArtifacts, maxTotalBytes);
    }

    private async Task<CommandResult> RunArtifactSelectionFixtureAsync(
        string artifacts,
        string jobs,
        int maxArtifacts = 20,
        long maxTotalBytes = 1073741824,
        long maxArtifactBytes = 104857600)
    {
        var artifactsPath = Path.Combine(_workspace.Path, "artifacts.json");
        var jobsPath = Path.Combine(_workspace.Path, "jobs.json");
        await File.WriteAllTextAsync(artifactsPath, artifacts);
        await File.WriteAllTextAsync(jobsPath, jobs);

        return await RunSelectionAsync([artifactsPath, "2026-09-04T12:00:00Z", "2026-09-04T12:02:00Z", jobsPath,
            maxArtifacts.ToString(CultureInfo.InvariantCulture), maxTotalBytes.ToString(CultureInfo.InvariantCulture),
            maxArtifactBytes.ToString(CultureInfo.InvariantCulture)]);
    }
}
