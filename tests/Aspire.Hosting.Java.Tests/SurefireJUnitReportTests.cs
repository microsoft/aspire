// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text;
using System.Xml;
using Aspire.Hosting.ApplicationModel;
using TestResult = Aspire.Hosting.ApplicationModel.TestResult;

#pragma warning disable ASPIRETESTING001

namespace Aspire.Hosting.Java.Tests;

public class SurefireJUnitReportTests
{
    internal const string PassingReport = """
        <testsuite name="example.ServiceTest" tests="1" failures="0" errors="0" skipped="0">
          <properties><property name="java.version" value="21"/></properties>
          <testcase classname="example.ServiceTest" name="probe(String)[1]" time="0.125"/>
        </testsuite>
        """;

    [Fact]
    public async Task MapsOnlyExplicitOutcomesAndPreservesNamesDiagnosticsAndDurations()
    {
        var results = await ReadAsync("""
            <testsuite name="example.ServiceTest" tests="4" failures="1" errors="1" skipped="1">
              <testcase classname="example.ServiceTest" name="pass[one &amp; two]" time="0.125">
                <system-out>diagnostic output</system-out>
              </testcase>
              <testcase classname="example.ServiceTest" name="assertion" time="0.5">
                <failure type="org.opentest4j.AssertionFailedError" message="expected value">stack trace</failure>
              </testcase>
              <testcase classname="example.ServiceTest" name="timeout" time="1">
                <error type="java.util.concurrent.TimeoutException" message="timed out">timeout stack</error>
              </testcase>
              <testcase classname="example.ServiceTest" name="disabled">
                <skipped message="disabled reason"/>
              </testcase>
            </testsuite>
            """);
        Assert.Equal(new[] { TestOutcome.Passed, TestOutcome.Failed, TestOutcome.Error, TestOutcome.Skipped },
            results.Select(result => result.Outcome));
        Assert.Equal("example.ServiceTest.pass[one & two]", results[0].Name);
        Assert.Equal(TimeSpan.FromMilliseconds(125), results[0].Duration);
        Assert.Null(results[3].Duration);
        Assert.Equal("diagnostic output", results[0].Message);
        Assert.Equal("failure: org.opentest4j.AssertionFailedError expected value" + Environment.NewLine + "stack trace", results[1].Message);
        Assert.Equal("error: java.util.concurrent.TimeoutException timed out" + Environment.NewLine + "timeout stack", results[2].Message);
        Assert.Equal("skipped:  disabled reason", results[3].Message);
    }

    [Theory]
    [InlineData("<testsuites/>")]
    [InlineData("<testsuite")]
    [InlineData("<!DOCTYPE testsuite [<!ENTITY x 'unsafe'>]><testsuite>&x;</testsuite>")]
    [InlineData("<testsuite xmlns='urn:unknown'/>")]
    [InlineData("<testsuite name='suite' tests='1' failures='0' errors='0' skipped='0'/>")]
    [InlineData("<testsuite name='suite' tests='0' failures='-1' errors='0' skipped='0'/>")]
    [InlineData("<testsuite name='suite' tests='0' failures='0' errors='0'/>")]
    [InlineData("<testsuite name='suite' tests='0' failures='0' errors='0' skipped='0'><testsuite/></testsuite>")]
    public async Task RejectsMalformedUnknownAndInconsistentReports(string xml)
    {
        var error = await Record.ExceptionAsync(() => ReadAsync(xml));
        Assert.True(error is InvalidDataException or XmlException, error?.ToString() ?? "Report unexpectedly succeeded.");
    }

    [Theory]
    [InlineData("<rerunFailure/>")]
    [InlineData("<flakyFailure/>")]
    [InlineData("<cancelled/>")]
    [InlineData("<timeout/>")]
    [InlineData("<failure/><error/>")]
    public async Task RejectsUnknownAndMultipleOutcomes(string outcome)
    {
        await Assert.ThrowsAsync<InvalidDataException>(() => ReadAsync(PassingReport.Replace(
            "time=\"0.125\"/>", $"time=\"0.125\">{outcome}</testcase>", StringComparison.Ordinal)));
    }

    [Theory]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    [InlineData("-0.01")]
    [InlineData("1,5")]
    [InlineData("1e99")]
    public async Task RejectsInvalidDurations(string duration)
    {
        await Assert.ThrowsAsync<InvalidDataException>(() => ReadAsync(
            PassingReport.Replace("0.125", duration, StringComparison.Ordinal)));
    }

    [Fact]
    public async Task RejectsDuplicateIdentitiesAndMissingNames()
    {
        await Assert.ThrowsAsync<InvalidDataException>(() => ReadAsync(PassingReport.Replace(
            "</testsuite>", "<testcase classname='example.ServiceTest' name='probe(String)[1]'/></testsuite>", StringComparison.Ordinal)));
        await Assert.ThrowsAsync<InvalidDataException>(() => ReadAsync(
            PassingReport.Replace("classname=\"example.ServiceTest\"", "", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task ExplicitZeroTestSuiteDoesNotInventResults()
    {
        Assert.Empty(await ReadAsync("<testsuite name='empty' tests='0' failures='0' errors='0' skipped='0'/>"));
    }

    private static async Task<IReadOnlyList<TestResult>> ReadAsync(string xml)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(xml));
        return await SurefireJUnitReport.ReadAsync(stream, TestContext.Current.CancellationToken);
    }
}
