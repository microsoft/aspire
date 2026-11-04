// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using AnalyzeCiFailure;
using Xunit;

namespace Infrastructure.Tests;

/// <summary>Exercises the in-memory mapping of combined TRX reports to known jobs.</summary>
public sealed class CombinedTrxReportsTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LongestShortNameSeparatesBaseAndPartitionReports(bool reverseJobs)
    {
        var baseJob = CreateJob("Hosting", "ubuntu-latest");
        var partition = CreateJob("Hosting-2", "ubuntu-latest");
        var reports = new CombinedTrxReports(
            [
                ("ubuntu-latest/testresults/Hosting_net10.0_20261103065005.trx", "00001.trx"),
                ("ubuntu-latest/testresults/Hosting-2_net10.0_20261103065037.trx", "00002.trx"),
                ("ubuntu-latest/testresults/Hosting-2_net11.0_20261103065037.trx", "00003.trx"),
            ],
            reverseJobs ? [partition, baseJob] : [baseJob, partition]);

        Assert.Equal(["00001.trx"], reports.GetFileNamesForJob(baseJob));
        Assert.Equal(["00002.trx", "00003.trx"], reports.GetFileNamesForJob(partition));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EqualBestMatchesRemainAmbiguous(bool identicalExpectation)
    {
        var first = CreateJob("Hosting", "ubuntu-latest");
        var second = identicalExpectation ? first with { } : first with
        {
            JobName = "Tests / Package tests - Linux / Hosting (ubuntu-latest)",
        };
        var reports = new CombinedTrxReports(
            [("ubuntu-latest/testresults/Hosting.trx", "00001.trx")],
            [first, second]);

        Assert.Empty(reports.GetFileNamesForJob(first));
        Assert.Empty(reports.GetFileNamesForJob(second));
    }

    [Fact]
    public void ReportsStayWithinTheirProducingRunner()
    {
        var linux = CreateJob("Hosting", "ubuntu-latest");
        var windows = CreateJob("Hosting", "windows-latest");
        var reports = new CombinedTrxReports(
            [
                ("ubuntu-latest/testresults/Hosting.trx", "00001.trx"),
                ("windows-latest/testresults/Hosting.trx", "00002.trx"),
                ("macos-latest/testresults/Hosting.trx", "00003.trx"),
            ],
            [linux, windows]);

        Assert.Equal(["00001.trx"], reports.GetFileNamesForJob(linux));
        Assert.Equal(["00002.trx"], reports.GetFileNamesForJob(windows));
    }

    [Fact]
    public void CallerSuppliedHistoricalPartitionCannotSupplyTheCurrentBaseJob()
    {
        var current = CreateJob("Hosting", "ubuntu-latest");
        var historical = CreateJob("Hosting-Collection", "ubuntu-latest");
        var reports = new CombinedTrxReports(
            [("ubuntu-latest/testresults/Hosting-Collection_net10.0_20261103065037.trx", "00001.trx")],
            [current, historical]);

        Assert.Empty(reports.GetFileNamesForJob(current));
        Assert.Equal(["00001.trx"], reports.GetFileNamesForJob(historical));
    }

    [Fact]
    public void NonTrxJobsCannotClaimMatchingReports()
    {
        var trx = CreateJob("Hosting", "ubuntu-latest");
        var mocha = CreateJob("Hosting-2", "ubuntu-latest") with { Format = TestArtifactFormat.Mocha };
        var reports = new CombinedTrxReports(
            [("ubuntu-latest/testresults/Hosting-2_net10.0_20261103065037.trx", "00001.trx")],
            [trx, mocha]);

        Assert.Equal(["00001.trx"], reports.GetFileNamesForJob(trx));
        Assert.Empty(reports.GetFileNamesForJob(mocha));
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void EmptyInputsReturnNoFileNames(bool emptyReports, bool emptyJobs)
    {
        var job = CreateJob("Hosting", "ubuntu-latest");
        var reports = new CombinedTrxReports(
            emptyReports ? [] : [("ubuntu-latest/testresults/Hosting.trx", "00001.trx")],
            emptyJobs ? [] : [job]);

        Assert.Empty(reports.GetFileNamesForJob(job));
    }

    [Theory]
    [InlineData("")]
    [InlineData("Build")]
    public void UnrecognizedJobsCannotClaimReports(string jobName)
    {
        var job = new TestArtifactExpectation(jobName, "results", TestArtifactFormat.Trx, TestEvidencePolicy.Required);
        var reports = new CombinedTrxReports(
            [("ubuntu-latest/testresults/Hosting.trx", "00001.trx")],
            [job]);

        Assert.Empty(reports.GetFileNamesForJob(job));
    }

    [Fact]
    public void QueriesUseFullExpectationValueEquality()
    {
        var job = CreateJob("Hosting", "ubuntu-latest");
        var reports = new CombinedTrxReports(
            [("ubuntu-latest/testresults/Hosting.trx", "00001.trx")],
            [job]);

        Assert.Equal(["00001.trx"], reports.GetFileNamesForJob(job with { }));
        Assert.Empty(reports.GetFileNamesForJob(CreateJob("Unknown", "ubuntu-latest")));
        Assert.Empty(reports.GetFileNamesForJob(job with { ArtifactName = "other-artifact" }));
        Assert.Empty(reports.GetFileNamesForJob(job with { Format = TestArtifactFormat.Mocha }));
        Assert.Empty(reports.GetFileNamesForJob(job with { EvidencePolicy = TestEvidencePolicy.OptionalDiagnostics }));
        Assert.Empty(reports.GetFileNamesForJob(job with { JobName = "Tests / Package tests - Linux / Hosting (ubuntu-latest)" }));
    }

    [Fact]
    public void AmbiguousAndUnmatchedEntriesDoNotDiscardOtherMatchingReports()
    {
        var baseJob = CreateJob("Hosting", "ubuntu-latest");
        var partition = CreateJob("Hosting-2", "ubuntu-latest");
        var duplicatePartition = partition with { JobName = "Tests / Package tests - Linux / Hosting-2 (ubuntu-latest)" };
        var other = CreateJob("Other", "ubuntu-latest");
        var reports = new CombinedTrxReports(
            [
                ("ubuntu-latest/testresults/Hosting_net10.0_20261103065005.trx", "00001.trx"),
                ("ubuntu-latest/testresults/Hosting-2_net10.0_20261103065037.trx", "00002.trx"),
                ("ubuntu-latest/testresults/Unknown.trx", "00003.trx"),
                ("ubuntu-latest/testresults/Hosting_net11.0_20261103065005.trx", "00004.trx"),
                ("ubuntu-latest/testresults/Other.trx", "00005.trx"),
            ],
            [baseJob, partition, duplicatePartition, other]);

        Assert.Equal(["00001.trx", "00004.trx"], reports.GetFileNamesForJob(baseJob));
        Assert.Empty(reports.GetFileNamesForJob(partition));
        Assert.Empty(reports.GetFileNamesForJob(duplicatePartition));
        Assert.Equal(["00005.trx"], reports.GetFileNamesForJob(other));
    }

    [Fact]
    public void IndexIsBuiltBeforeInputsChange()
    {
        var job = CreateJob("Hosting", "ubuntu-latest");
        List<(string EntryPath, string ExtractedFileName)> extracted = [("ubuntu-latest/testresults/Hosting.trx", "00001.trx")];
        TestArtifactExpectation[] jobs = [job];
        var reports = new CombinedTrxReports(extracted, jobs);

        extracted.Clear();
        jobs[0] = CreateJob("Other", "ubuntu-latest");

        Assert.Equal(["00001.trx"], reports.GetFileNamesForJob(job));
        Assert.Empty(reports.GetFileNamesForJob(jobs[0]));
    }

    private static TestArtifactExpectation CreateJob(string shortName, string runner)
        => new($"Tests / No-package tests / {shortName} ({runner})",
            $"logs-{shortName}-{runner}", TestArtifactFormat.Trx, TestEvidencePolicy.Required);
}
