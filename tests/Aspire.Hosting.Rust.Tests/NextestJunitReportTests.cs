// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text;
using System.Xml;
using Aspire.Hosting.ApplicationModel;
using TestResult = Aspire.Hosting.ApplicationModel.TestResult;

#pragma warning disable ASPIRETESTING001

namespace Aspire.Hosting.Rust.Tests;

public class NextestJunitReportTests
{
    [Fact]
    public async Task BinaryIdentityFinalOutcomesDurationAndMessagesArePreserved()
    {
        var results = await ReadAsync("""
            <testsuites tests="4" failures="1" errors="1" skipped="1">
              <testsuite name="crate::unit" tests="3" failures="1" errors="1">
                <testcase classname="crate::unit" name="module::same" time="0.001"/>
                <testcase name="failure" time="1.25"><failure type="panic">assertion failed</failure><system-err>stderr</system-err></testcase>
                <testcase name="error"><error message="timeout">timed out</error></testcase>
              </testsuite>
              <testsuite name="crate::integration" tests="1" failures="0" errors="0" skipped="1">
                <testcase name="module::same"><skipped message="ignored"/></testcase>
              </testsuite>
            </testsuites>
            """);
        Assert.Collection(results,
            result =>
            {
                Assert.Equal("crate::unit/module::same", result.Name);
                Assert.Equal(TestOutcome.Passed, result.Outcome);
                Assert.Equal(TimeSpan.FromMilliseconds(1), result.Duration);
                Assert.Null(result.Message);
            },
            result =>
            {
                Assert.Equal(TestOutcome.Failed, result.Outcome);
                Assert.Equal(TimeSpan.FromSeconds(1.25), result.Duration);
                Assert.Equal(string.Join(Environment.NewLine, "failure: panic", "assertion failed", "stderr"), result.Message);
            },
            result =>
            {
                Assert.Equal(TestOutcome.Failed, result.Outcome);
                Assert.Null(result.Duration);
                Assert.Equal(string.Join(Environment.NewLine, "error:  timeout", "timed out"), result.Message);
            },
            result =>
            {
                Assert.Equal("crate::integration/module::same", result.Name);
                Assert.Equal(TestOutcome.Skipped, result.Outcome);
            });
    }

    [Theory]
    [InlineData("<testcase name='x'><flakyFailure/></testcase>")]
    [InlineData("<testcase name='x'><rerunError/></testcase>")]
    [InlineData("<testcase name='x'><skipped/><failure/></testcase>")]
    [InlineData("<testcase name='x'><unknown/></testcase>")]
    [InlineData("<testcase/>")]
    [InlineData("<testsuite/>")]
    public async Task UnsupportedOrAmbiguousResultsFail(string entry)
    {
        await Assert.ThrowsAsync<InvalidDataException>(() => ReadAsync(Wrap(entry)));
    }

    [Theory]
    [InlineData("-1")]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    [InlineData("1e100")]
    [InlineData("bad")]
    public async Task InvalidDurationFails(string duration)
    {
        await Assert.ThrowsAsync<InvalidDataException>(() => ReadAsync(Wrap($"<testcase name='x' time='{duration}'/>")));
    }

    [Theory]
    [InlineData("<testsuites tests='0' failures='0' errors='0'/>")]
    [InlineData("<testsuites tests='2' failures='0' errors='0'><testsuite name='crate' tests='1' failures='0' errors='0'><testcase name='x'/></testsuite></testsuites>")]
    [InlineData("<testsuites tests='1' failures='0' errors='0'><testsuite name='crate' tests='2' failures='0' errors='0'><testcase name='x'/></testsuite></testsuites>")]
    [InlineData("<testsuites tests='1' failures='0' errors='0'><testsuite name='crate' tests='1' failures='1' errors='0'><testcase name='x'/></testsuite></testsuites>")]
    [InlineData("<testsuites tests='-1' failures='0' errors='0'/>")]
    [InlineData("<testsuite/>")]
    public async Task EmptyIncompleteOrInconsistentReportsFail(string xml)
    {
        await Assert.ThrowsAsync<InvalidDataException>(() => ReadAsync(xml));
    }

    [Theory]
    [InlineData("<testsuites")]
    [InlineData("<!DOCTYPE testsuites [<!ENTITY external SYSTEM 'file:///etc/passwd'>]><testsuites>&external;</testsuites>")]
    public async Task MalformedXmlAndExternalEntitiesFail(string xml)
    {
        await Assert.ThrowsAsync<XmlException>(() => ReadAsync(xml));
    }

    [Fact]
    public async Task CancelledCollectionFailsWithoutResults()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(Wrap("<testcase name='x'/>")));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => NextestJunitReport.ReadAsync(stream, cancellation.Token));
    }

    private static string Wrap(string entry)
    {
        return $"<testsuites tests='1' failures='0' errors='0'><testsuite name='crate' tests='1' failures='0' errors='0'>{entry}</testsuite></testsuites>";
    }

    private static async Task<IReadOnlyList<TestResult>> ReadAsync(string xml)
    {
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(xml));

        return await NextestJunitReport.ReadAsync(stream, TestContext.Current.CancellationToken);
    }
}
