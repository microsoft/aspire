// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json.Nodes;
using AnalyzeCiFailure;
using Xunit;

namespace Infrastructure.Tests;

/// <summary>Checks workflow completion, output and rerun adapters against explicit environment inputs.</summary>
public sealed class AnalyzeCiFailureWorkflowAdapterTests
{
    [Theory]
    [InlineData("Tests / No-package tests / Infrastructure (ubuntu-latest)", false, "logs-Infrastructure-ubuntu-latest", "Trx", "Required")]
    [InlineData("Custom / Other (windows-latest)", true, "logs-Other-windows-latest", "Trx", "Required")]
    [InlineData("Tests / VS Code extension E2E (Linux, debug)", false, "extension-e2e-diagnostics-linux-x64-debug-attempt2", "Mocha", "OptionalDiagnostics")]
    [InlineData("VS Code extension E2E (Windows, browser)", false, "extension-e2e-diagnostics-win-x64-browser-attempt2", "Mocha", "OptionalDiagnostics")]
    public void DefaultAdapterDescribesTestProducer(
        string name,
        bool uploadStep,
        string artifact,
        string format,
        string policy)
    {
        var job = new JsonObject
        {
            ["name"] = name,
            ["steps"] = new JsonArray(new JsonObject
            {
                ["name"] = uploadStep ? "Upload logs, and test results" : "Set up job",
                ["conclusion"] = "failure",
            }),
        };

        var plan = Assert.Single(new AspireCiWorkflowConventions().DescribeTestArtifacts(job, runAttempt: 2));

        Assert.NotNull(plan);
        Assert.Equal(name, plan.JobName);
        Assert.Equal(artifact, plan.ArtifactName);
        Assert.Equal(format, plan.Format.ToString());
        Assert.Equal(policy, plan.EvidencePolicy.ToString());
    }

    [Theory]
    [InlineData("cancelled", "completed", "failure", "RequiredForInterruptedExecution")]
    [InlineData("timed_out", "completed", "failure", "RequiredForInterruptedExecution")]
    [InlineData("success", "completed", "failure", "OptionalDiagnostics")]
    [InlineData("success", "in_progress", "failure", "RequiredForInterruptedExecution")]
    [InlineData("cancelled", "completed", "success", "RequiredForInterruptedExecution")]
    public void DefaultAdapterSeparatesInterruptedTestsFromFailedCleanup(
        string conclusion,
        string status,
        string cleanup,
        string expected)
    {
        var job = JsonNode.Parse($$"""
            {"name":"Tests / VS Code extension E2E (Linux, debug)","steps":[
              {"name":"Run extension E2E tests","conclusion":"{{conclusion}}","status":"{{status}}"},
              {"name":"Post cleanup","conclusion":"{{cleanup}}"}]}
            """)!.AsObject();

        var plan = Assert.Single(new AspireCiWorkflowConventions().DescribeTestArtifacts(job, runAttempt: 2));

        Assert.NotNull(plan);
        Assert.Equal(expected, plan.EvidencePolicy.ToString());
    }

    [Theory]
    [InlineData("Build")]
    [InlineData("Unrelated tests (ubuntu-latest)")]
    public void DefaultAdapterDoesNotInventTestArtifacts(string name)
        => Assert.Empty(new AspireCiWorkflowConventions().DescribeTestArtifacts(
            new JsonObject { ["name"] = name },
            runAttempt: 2));

    [Theory]
    [InlineData("Tests / No-package tests / malformed")]
    [InlineData("Tests / VS Code extension E2E (unknown, debug)")]
    public void DefaultAdapterRejectsMalformedRecognizedJobs(string name)
        => Assert.Throws<ValidationException>(() => new AspireCiWorkflowConventions().DescribeTestArtifacts(
            new JsonObject { ["name"] = name },
            runAttempt: 2));

    [Theory]
    [InlineData("Final Results", "", true)]
    [InlineData("Gate", "Check dependent jobs failed", true)]
    [InlineData("Gate", "Fail if any of the dependent jobs failed", true)]
    [InlineData("Build", "Build", false)]
    public void DefaultAdapterRecognizesAggregationWithoutDroppingRealFailures(string name, string step, bool expected)
    {
        var job = new JsonObject
        {
            ["name"] = name,
            ["steps"] = new JsonArray(new JsonObject { ["name"] = step, ["conclusion"] = "failure" }),
        };

        Assert.Equal(expected, new AspireCiWorkflowConventions().IsAggregationJob(job));
    }

    [Theory]
    [InlineData("Trx", "OptionalDiagnostics", false)]
    [InlineData("Mocha", "Required", true)]
    public void MissingArtifactPolicyDoesNotComeFromResultFormat(string format, string policy, bool rejects)
    {
        var plan = new TestArtifactExpectation(
            "Alternate tests",
            "alternate-results",
            Enum.Parse<TestArtifactFormat>(format),
            Enum.Parse<TestEvidencePolicy>(policy));
        if (rejects)
        {
            Assert.Throws<ValidationException>(() => TestResultArtifacts.Select(
                [],
                [plan],
                "2026-09-04T12:00:00Z",
                "2026-09-04T12:02:00Z"));
        }
        else
        {
            Assert.Empty(TestResultArtifacts.Select(
                [],
                [plan],
                "2026-09-04T12:00:00Z",
                "2026-09-04T12:02:00Z"));
        }
    }

    [Fact]
    public void RequiredMochaReservesBudgetBeforeOptionalTrx()
    {
        var artifacts = TestResultArtifacts.ReadArtifacts(JsonNode.Parse("""
            [{"id":10,"name":"optional","size_in_bytes":10,"expired":false,"created_at":"2026-09-04T12:01:00Z"},
             {"id":11,"name":"required","size_in_bytes":10,"expired":false,"created_at":"2026-09-04T12:01:00Z"}]
            """)!.AsArray());
        var plans = new[]
        {
            new TestArtifactExpectation("Optional", "optional", TestArtifactFormat.Trx, TestEvidencePolicy.OptionalDiagnostics),
            new TestArtifactExpectation("Required", "required", TestArtifactFormat.Mocha, TestEvidencePolicy.Required),
        };

        var selected = TestResultArtifacts.Select(
            artifacts,
            plans,
            "2026-09-04T12:00:00Z",
            "2026-09-04T12:02:00Z",
            maxArtifacts: 1,
            maxTotalBytes: 10);

        Assert.Equal("Required", Assert.Single(selected).JobName);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SpareCapacityPrioritizesNeededResultsOverOptionalDiagnostics(bool reverse)
    {
        var artifacts = TestResultArtifacts.ReadArtifacts(JsonNode.Parse("""
            [{"id":10,"name":"optional","size_in_bytes":10,"expired":false,"created_at":"2026-09-04T12:01:00Z"},
             {"id":11,"name":"interrupted","size_in_bytes":10,"expired":false,"created_at":"2026-09-04T12:01:00Z"}]
            """)!.AsArray());
        var plans = new[]
        {
            new TestArtifactExpectation("A optional", "optional", TestArtifactFormat.Trx, TestEvidencePolicy.OptionalDiagnostics),
            new TestArtifactExpectation("Z interrupted", "interrupted", TestArtifactFormat.Mocha, TestEvidencePolicy.RequiredForInterruptedExecution),
        };
        if (reverse)
        {
            Array.Reverse(plans);
        }

        var selected = TestResultArtifacts.Select(
            artifacts,
            plans,
            "2026-09-04T12:00:00Z",
            "2026-09-04T12:02:00Z",
            maxArtifacts: 1,
            maxTotalBytes: 10);

        Assert.Equal("Z interrupted", Assert.Single(selected).JobName);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void UndefinedConventionValuesCannotRelaxArtifactRequirements(bool invalidFormat)
    {
        var plan = new TestArtifactExpectation(
            "Tests",
            "results",
            invalidFormat ? (TestArtifactFormat)99 : TestArtifactFormat.Trx,
            invalidFormat ? TestEvidencePolicy.Required : (TestEvidencePolicy)99);

        var error = Assert.Throws<ValidationException>(() => TestResultArtifacts.Select(
            [],
            [plan],
            "2026-09-04T12:00:00Z",
            "2026-09-04T12:02:00Z"));

        Assert.Equal("Workflow conventions returned an invalid test artifact plan: job='Tests' artifact='results'", error.Message);
    }

    [Fact]
    public void MissingArtifactDiagnosticsKeepContextBoundedAndRedacted()
    {
        var plan = new TestArtifactExpectation(
            "Tests token=secret\n::error::injected",
            "results-" + new string('x', count: 1000),
            TestArtifactFormat.Trx,
            TestEvidencePolicy.Required);

        var error = Assert.Throws<ValidationException>(() => TestResultArtifacts.Select(
            [],
            [plan],
            "2026-09-04T12:00:00Z",
            "2026-09-04T12:02:00Z"));

        Assert.Contains("token=[REDACTED]", error.Message, StringComparison.Ordinal);
        Assert.Contains("artifact=", error.Message, StringComparison.Ordinal);
        Assert.Single(error.Message.Split('\n'));
        Assert.InRange(error.Message.Length, 1, 500);
    }
}
