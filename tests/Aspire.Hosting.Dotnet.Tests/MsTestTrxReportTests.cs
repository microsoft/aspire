// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text;
using System.Xml;
using System.Xml.Linq;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Dotnet.Tests.Helpers;

#pragma warning disable ASPIRETESTING001 // Tests the experimental testing integration.

namespace Aspire.Hosting.Dotnet.Tests;

public class MsTestTrxReportTests
{
    [Fact]
    public async Task NormalizesDataRowsOutcomesDurationsAndEscapedDiagnostics()
    {
        var document = MsTestReportFixture.Create("Passed", "Failed", "NotExecuted", "Passed");
        var entries = document.Descendants(MsTestReportFixture.Namespace + "UnitTestResult").ToArray();
        entries[1].Add(new XElement(MsTestReportFixture.Namespace + "Output",
            new XElement(MsTestReportFixture.Namespace + "ErrorInfo",
                new XElement(MsTestReportFixture.Namespace + "Message", "expected <value> & got \"other\""),
                new XElement(MsTestReportFixture.Namespace + "StackTrace", "at Example.Tests.DataRow()")),
            new XElement(MsTestReportFixture.Namespace + "StdOut", "test output"),
            new XElement(MsTestReportFixture.Namespace + "StdErr", "test error")));
        entries[2].Attribute("duration")!.Remove();
        entries[2].Add(new XElement(MsTestReportFixture.Namespace + "Output",
            new XElement(MsTestReportFixture.Namespace + "StdOut", "skip reason")));
        var report = await ReadAsync(document);

        Assert.True(report.Failed);
        Assert.Equal(4, report.Results.Count);
        await Verify(report.Results.Select(result => new
        {
            result.Name,
            Outcome = result.Outcome.ToString(),
            result.Duration,
            result.Message
        }));
    }

    [Fact]
    public async Task SummaryTimeoutsDoNotInventPerTestIdentitiesAndRunFailureDoesNotDiscardPassingResults()
    {
        var document = MsTestReportFixture.Create("Failed", "Failed");
        var counters = document.Descendants(MsTestReportFixture.Namespace + "Counters").Single();
        counters.SetAttributeValue("failed", 1);
        counters.SetAttributeValue("timeout", 1);
        Assert.Equal([TestOutcome.Failed, TestOutcome.Failed],
            (await ReadAsync(document)).Results.Select(result => result.Outcome));

        document = MsTestReportFixture.Create("Passed");
        document.Descendants(MsTestReportFixture.Namespace + "ResultSummary").Single().SetAttributeValue("outcome", "Failed");
        var report = await ReadAsync(document);
        Assert.True(report.Failed);
        Assert.Equal(TestOutcome.Passed, Assert.Single(report.Results).Outcome);
    }

    [Theory]
    [InlineData("Error", TestOutcome.Error)]
    [InlineData("Timeout", TestOutcome.TimedOut)]
    [InlineData("Inconclusive", TestOutcome.Inconclusive)]
    [InlineData("Aborted", TestOutcome.Cancelled)]
    public async Task ExplicitTerminalTrxOutcomesRetainTheirMeaning(string source, TestOutcome expected)
    {
        var report = await ReadAsync(MsTestReportFixture.Create(source));
        Assert.True(report.Failed);
        Assert.Equal(expected, Assert.Single(report.Results).Outcome);
    }

    [Theory]
    [InlineData("Error")]
    [InlineData("Timeout")]
    [InlineData("Aborted")]
    [InlineData("Inconclusive")]
    public async Task RunLevelFailureDoesNotRewritePassedOrSkippedTests(string outcome)
    {
        var document = MsTestReportFixture.Create("Passed", "NotExecuted");
        document.Descendants(MsTestReportFixture.Namespace + "ResultSummary").Single().SetAttributeValue("outcome", outcome);
        var report = await ReadAsync(document);
        Assert.True(report.Failed);
        Assert.Equal([TestOutcome.Passed, TestOutcome.Skipped], report.Results.Select(result => result.Outcome));
    }

    [Theory]
    [InlineData("InProgress")]
    [InlineData("Pending")]
    [InlineData("Warning")]
    [InlineData("Completed")]
    [InlineData("NotRunnable")]
    [InlineData("Disconnected")]
    [InlineData("PassedButRunAborted")]
    [InlineData("Cancelled")]
    public async Task UnsupportedOrNonTestStatesCannotBecomePassingOrSkippedResults(string outcome)
    {
        await Assert.ThrowsAsync<InvalidDataException>(() => ReadAsync(MsTestReportFixture.Create(outcome)));
    }

    [Theory]
    [InlineData("Error", "error")]
    [InlineData("Timeout", "timeout")]
    [InlineData("Inconclusive", "inconclusive")]
    [InlineData("Aborted", "aborted")]
    public async Task ExplicitOutcomeCountersMustMatchRows(string outcome, string counter)
    {
        var document = MsTestReportFixture.Create(outcome);
        document.Descendants(MsTestReportFixture.Namespace + "Counters").Single().SetAttributeValue(counter, 0);
        await Assert.ThrowsAsync<InvalidDataException>(() => ReadAsync(document));
    }

    [Fact]
    public async Task ExplicitTimeoutCannotBeCountedAsAnOrdinaryFailure()
    {
        var document = MsTestReportFixture.Create("Timeout");
        var counters = document.Descendants(MsTestReportFixture.Namespace + "Counters").Single();
        counters.SetAttributeValue("failed", 1);
        counters.SetAttributeValue("timeout", 0);
        await Assert.ThrowsAsync<InvalidDataException>(() => ReadAsync(document));
    }

    [Fact]
    public async Task ZeroTestsAndAllSkippedRemainDistinct()
    {
        var empty = await ReadAsync(MsTestReportFixture.Create());
        Assert.Empty(empty.Results);
        Assert.False(empty.Failed);
        var skipped = await ReadAsync(MsTestReportFixture.Create("NotExecuted"));
        Assert.Equal(TestOutcome.Skipped, Assert.Single(skipped.Results).Outcome);
    }

    [Theory]
    [InlineData("unknown-outcome")]
    [InlineData("missing-name")]
    [InlineData("invalid-duration")]
    [InlineData("negative-duration")]
    [InlineData("missing-definition")]
    [InlineData("invalid-identity")]
    [InlineData("duplicate-execution")]
    [InlineData("duplicate-definition")]
    [InlineData("incomplete-summary")]
    [InlineData("invalid-counter")]
    [InlineData("unexpected-root")]
    [InlineData("unexpected-namespace")]
    [InlineData("nested-results")]
    [InlineData("nonzero-error")]
    [InlineData("in-progress-run")]
    [InlineData("duplicate-results")]
    public async Task InvalidOrIncompleteReportsFailExplicitly(string invalidCase)
    {
        var document = MsTestReportFixture.Create("Passed");
        var ns = MsTestReportFixture.Namespace;
        var entry = document.Descendants(ns + "UnitTestResult").Single();
        var definition = document.Descendants(ns + "UnitTest").Single();
        var counters = document.Descendants(ns + "Counters").Single();
        switch (invalidCase)
        {
            case "unknown-outcome": entry.SetAttributeValue("outcome", "Unknown"); break;
            case "missing-name": entry.Attribute("testName")!.Remove(); break;
            case "invalid-duration": entry.SetAttributeValue("duration", "not a duration"); break;
            case "negative-duration": entry.SetAttributeValue("duration", "-00:00:01"); break;
            case "missing-definition": definition.Remove(); break;
            case "invalid-identity": entry.SetAttributeValue("executionId", "not-a-guid"); break;
            case "duplicate-execution": entry.Parent!.Add(new XElement(entry)); break;
            case "duplicate-definition": definition.Parent!.Add(new XElement(definition)); break;
            case "incomplete-summary": counters.SetAttributeValue("total", 2); break;
            case "invalid-counter": counters.SetAttributeValue("passed", "-1"); break;
            case "unexpected-root": document.Root!.Name = ns + "Other"; break;
            case "unexpected-namespace": document.Root!.Name = "TestRun"; break;
            case "nested-results": entry.Add(new XElement(ns + "InnerResults", new XElement(entry))); break;
            case "nonzero-error": counters.SetAttributeValue("error", 1); break;
            case "in-progress-run": document.Descendants(ns + "ResultSummary").Single().SetAttributeValue("outcome", "InProgress"); break;
            case "duplicate-results": document.Root!.Add(new XElement(ns + "Results")); break;
        }
        await Assert.ThrowsAsync<InvalidDataException>(() => ReadAsync(document));
    }

    [Theory]
    [InlineData("<TestRun>")]
    [InlineData("<!DOCTYPE TestRun [<!ENTITY external SYSTEM 'file:///not-read'>]><TestRun>&external;</TestRun>")]
    public async Task MalformedXmlAndDtdsAreRejected(string xml)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(xml));
        await Assert.ThrowsAsync<XmlException>(() => MsTestTrxReport.ReadAsync(stream, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task CancelledParsingDoesNotProduceResults()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(MsTestReportFixture.Create("Passed").ToString()));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => MsTestTrxReport.ReadAsync(stream, cancellation.Token));
    }

    private static async Task<MsTestTrxReport.Report> ReadAsync(XDocument document)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(document.ToString()));

        return await MsTestTrxReport.ReadAsync(stream, TestContext.Current.CancellationToken);
    }
}
