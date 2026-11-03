// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;
using AnalyzeCiFailure;
using VerifyXunit;
using Xunit;

namespace Infrastructure.Tests;

/// <summary>Pins explicit enum wire names and rejects unsupported numeric or case-insensitive values.</summary>
public sealed class AnalyzeCiFailureEnumTests
{
    [Theory]
    [InlineData("""{"verdict":"unrecognized"}""")]
    [InlineData("""{"verdict":0}""")]
    [InlineData("""{"verdict":"0"}""")]
    [InlineData("""{"verdict":"FlakyTest"}""")]
    [InlineData("""{"verdict":"FLAKY-TEST"}""")]
    [InlineData("""{"failed_jobs":[{"classification":"unrecognized"}]}""")]
    [InlineData("""{"failed_jobs":[{"classification":0}]}""")]
    [InlineData("""{"failed_tests":[{"classification":"flaky-test"}]}""")]
    [InlineData("""{"failed_tests":[{"classification":0}]}""")]
    public void AnalysisContractRejectsUnknownOrNonWireClassifications(string json)
    {
        Assert.ThrowsAny<JsonException>(() => CiFailureJson.ToContract<AnalysisReport>(CiFailureJson.Parse(json)));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("\"0\"")]
    [InlineData("\"unrecognized\"")]
    public void ClosedVocabulariesRejectNumbersAndUnknownStrings(string json)
    {
        var value = CiFailureJson.Parse(json);
        Assert.ThrowsAny<JsonException>(() => CiFailureJson.ToContract<RunScope>(value));
        Assert.ThrowsAny<JsonException>(() => CiFailureJson.ToContract<EvidenceState>(value));
        Assert.ThrowsAny<JsonException>(() => CiFailureJson.ToContract<CandidateHistoryState>(value));
        Assert.ThrowsAny<JsonException>(() => CiFailureJson.ToContract<AnalysisVerdict>(value));
        Assert.ThrowsAny<JsonException>(() => CiFailureJson.ToContract<JobClassification>(value));
        Assert.ThrowsAny<JsonException>(() => CiFailureJson.ToContract<TestClassification>(value));
        Assert.ThrowsAny<JsonException>(() => CiFailureJson.ToContract<CauseType>(value));
        Assert.ThrowsAny<JsonException>(() => CiFailureJson.ToContract<TestArtifactFormat>(value));
    }

    [Fact]
    public async Task ClosedVocabulariesKeepTheirExplicitWireNames()
    {
        var projection = new
        {
            scopes = Enum.GetValues<RunScope>(),
            evidence = Enum.GetValues<EvidenceState>(),
            history = Enum.GetValues<CandidateHistoryState>(),
            verdicts = Enum.GetValues<AnalysisVerdict>(),
            jobs = Enum.GetValues<JobClassification>(),
            tests = Enum.GetValues<TestClassification>(),
            causes = Enum.GetValues<CauseType>(),
            formats = Enum.GetValues<TestArtifactFormat>(),
        };

        await Verifier.Verify(CiFailureJson.Serialize(projection), "json").UseDirectory("Snapshots");
    }
}
