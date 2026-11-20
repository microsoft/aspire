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
    public void ExactIdentitySeparatesBaseAndPartitionReports(bool reverseJobs)
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
    [InlineData("Hosting-Unknown_net10.0_20261103065037.trx")]
    [InlineData("Hosting_Unknown_net11.0_20261103065037.trx")]
    public void StructuredReportsFromUnknownProducersCannotSupplyKnownPrefixes(string fileName)
    {
        var job = CreateJob("Hosting", "ubuntu-latest");
        var reports = new CombinedTrxReports(
            [($"ubuntu-latest/testresults/{fileName}", "00001.trx")],
            [job]);

        Assert.Empty(reports.GetFileNamesForJob(job));
    }

    [Fact]
    public void StructuredReportIdentityCannotBeShadowedByAFrameworkNamedJob()
    {
        var job = CreateJob("Hosting", "ubuntu-latest");
        var other = CreateJob("Hosting_net10.0", "ubuntu-latest");
        var reports = new CombinedTrxReports(
            [("ubuntu-latest/testresults/Hosting_net10.0_20261103065037.trx", "00001.trx")],
            [job, other]);

        Assert.Equal(["00001.trx"], reports.GetFileNamesForJob(job));
        Assert.Empty(reports.GetFileNamesForJob(other));
    }

    [Theory]
    [InlineData("Hosting", "Hosting.trx")]
    [InlineData("Hosting-2", "Hosting-2.trx")]
    [InlineData("Hosting_Group", "Hosting_Group.trx")]
    [InlineData("Hosting_Group", "Hosting_Group_net10.0_20261103065037.trx")]
    public void ExactReportIdentityPreservesTheFullShortName(string shortName, string fileName)
    {
        var job = CreateJob(shortName, "ubuntu-latest");
        var reports = new CombinedTrxReports(
            [($"ubuntu-latest/testresults/{fileName}", "00001.trx")],
            [job]);

        Assert.Equal(["00001.trx"], reports.GetFileNamesForJob(job));
    }

    [Theory]
    [InlineData("Hosting_net10.0_.trx")]
    [InlineData("Hosting__20261103065037.trx")]
    [InlineData("Hosting_net10.0_2026110306503.trx")]
    [InlineData("Hosting_net10.0_202611030650370.trx")]
    [InlineData("Hosting_net10.0_timestamp.trx")]
    [InlineData("Hosting-2_net10.0_timestamp.trx")]
    [InlineData("Hosting_net10.0_20261103065037.txt")]
    [InlineData("Hosting-.trx")]
    public void MalformedReportNamesCannotSupplyKnownPrefixes(string fileName)
    {
        var job = CreateJob("Hosting", "ubuntu-latest");
        var partition = CreateJob("Hosting-2", "ubuntu-latest");
        var reports = new CombinedTrxReports(
            [($"ubuntu-latest/testresults/{fileName}", "00001.trx")],
            [job, partition]);

        Assert.Empty(reports.GetFileNamesForJob(job));
        Assert.Empty(reports.GetFileNamesForJob(partition));
    }

    [Fact]
    public void ClassSuffixFallbackUsesTheLongestJobBoundary()
    {
        var job = CreateJob("Hosting", "ubuntu-latest");
        var partition = CreateJob("Hosting-2", "ubuntu-latest");
        var reports = new CombinedTrxReports(
            [
                ("ubuntu-latest/testresults/Hosting-BatchTests.trx", "00001.trx"),
                ("ubuntu-latest/testresults/Hosting-2-Nested.BatchTests.trx", "00002.trx"),
                ("ubuntu-latest/testresults/HostingOther-BatchTests.trx", "00003.trx"),
                ("windows-latest/testresults/Hosting-BatchTests.trx", "00004.trx"),
            ],
            [job, partition]);

        Assert.Equal(["00001.trx"], reports.GetFileNamesForJob(job));
        Assert.Equal(["00002.trx"], reports.GetFileNamesForJob(partition));
    }

    [Fact]
    public void AmbiguousExactJobsCannotUseClassSuffixFallback()
    {
        var job = CreateJob("Hosting", "ubuntu-latest");
        var partition = CreateJob("Hosting-2", "ubuntu-latest");
        var duplicate = partition with { JobName = "Tests / Package tests - Linux / Hosting-2 (ubuntu-latest)" };
        var reports = new CombinedTrxReports(
            [
                ("ubuntu-latest/testresults/Hosting-2.trx", "00001.trx"),
                ("ubuntu-latest/testresults/Hosting-2_net10.0_20261103065037.trx", "00002.trx"),
                ("ubuntu-latest/testresults/Hosting.trx", "00003.trx"),
            ],
            [job, partition, duplicate]);

        Assert.Equal(["00003.trx"], reports.GetFileNamesForJob(job));
        Assert.Empty(reports.GetFileNamesForJob(partition));
        Assert.Empty(reports.GetFileNamesForJob(duplicate));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DuplicateJobIdentitiesRemainAmbiguous(bool identicalExpectation)
    {
        var first = CreateJob("Hosting", "ubuntu-latest");
        var second = identicalExpectation ? first with { } : first with
        {
            JobName = "Tests / Package tests - Linux / Hosting (ubuntu-latest)",
        };
        var reports = new CombinedTrxReports(
            [
                ("ubuntu-latest/testresults/Hosting.trx", "00001.trx"),
                ("ubuntu-latest/testresults/Hosting_net10.0_20261103065037.trx", "00002.trx"),
                ("ubuntu-latest/testresults/Hosting-BatchTests.trx", "00003.trx"),
            ],
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

    [Theory]
    [InlineData("8-core-ubuntu-latest", "ubuntu-latest")]
    [InlineData("16-core-windows-latest", "windows-latest")]
    [InlineData("large-macos-latest", "macos-latest")]
    public void PrefixedJobRunnersMatchTheirAggregateBucket(string runner, string bucket)
    {
        var job = CreateJob("Hosting-2", runner);
        var reports = new CombinedTrxReports(
            [
                ($"{bucket}/testresults/Hosting-2_net10.0_20261103065037.trx", "00001.trx"),
                ($"{bucket}/testresults/Hosting-2.trx", "00002.trx"),
                ($"{bucket}/testresults/Hosting-2-BatchTests.trx", "00003.trx"),
            ],
            [job]);

        Assert.Equal(["00001.trx", "00002.trx", "00003.trx"], reports.GetFileNamesForJob(job));
    }

    [Fact]
    public void JobsCollapsingToTheSameAggregateIdentityStayAmbiguous()
    {
        var standard = CreateJob("Hosting", "ubuntu-latest");
        var custom = CreateJob("Hosting", "8-core-ubuntu-latest");
        var reports = new CombinedTrxReports(
            [
                ("ubuntu-latest/testresults/Hosting_net10.0_20261103065037.trx", "00001.trx"),
                ("ubuntu-latest/testresults/Hosting.trx", "00002.trx"),
                ("ubuntu-latest/testresults/Hosting-BatchTests.trx", "00003.trx"),
            ],
            [standard, custom]);

        Assert.Empty(reports.GetFileNamesForJob(standard));
        Assert.Empty(reports.GetFileNamesForJob(custom));
    }

    [Theory]
    [InlineData("notubuntu-latest", "ubuntu-latest")]
    [InlineData("8-core-notubuntu-latest", "ubuntu-latest")]
    [InlineData("8-core-Ubuntu-latest", "ubuntu-latest")]
    [InlineData("8-core-ubuntu-latest-extra", "ubuntu-latest")]
    [InlineData("ubuntu-latest-x64", "ubuntu-latest")]
    [InlineData("linux-arm64", "linux-arm64")]
    [InlineData("notubuntu-latest", "notubuntu-latest")]
    [InlineData("8-core-ubuntu-latest", "8-core-ubuntu-latest")]
    [InlineData("8-core-ubuntu-latest", "Ubuntu-latest")]
    public void RunnerMatchingRequiresAnExactSupportedBucketOrDelimitedJobSuffix(string runner, string bucket)
    {
        var job = CreateJob("Hosting", runner);
        var reports = new CombinedTrxReports(
            [
                ($"{bucket}/testresults/Hosting_net10.0_20261103065037.trx", "00001.trx"),
                ($"{bucket}/testresults/Hosting.trx", "00002.trx"),
                ($"{bucket}/testresults/Hosting-BatchTests.trx", "00003.trx"),
            ],
            [job]);

        Assert.Empty(reports.GetFileNamesForJob(job));
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

        Assert.Empty(reports.GetFileNamesForJob(trx));
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
