// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text;
using System.Xml;
using TestResult = Aspire.Hosting.ApplicationModel.TestResult;

#pragma warning disable ASPIRETESTING001 // Tests the experimental pytest results.

namespace Aspire.Hosting.Python.Tests;

public class PytestJunitReportTests
{
    [Fact]
    public async Task BuiltInOutcomesPreserveParameterizedNamesDurationsAndDiagnostics()
    {
        var results = await ReadAsync("""
            <testsuites><testsuite tests="5" failures="1" errors="1" skipped="2">
              <testcase classname="test_api" name="test_value[1]" time="0.125" />
              <testcase classname="test_api" name="test_value[2]" time="0.25">
                <failure message="assert 1 == 2">traceback</failure><system-out>captured stdout</system-out>
              </testcase>
              <testcase classname="test_api" name="test_skip" time="0">
                <skipped type="pytest.skip" message="unavailable">skip reason</skipped>
              </testcase>
              <testcase classname="test_api" name="test_xfail">
                <skipped type="pytest.xfail" message="known issue" />
              </testcase>
              <testcase name="test_collection.py" time="0">
                <error message="collection failure">ImportError</error><system-err>captured stderr</system-err>
              </testcase>
            </testsuite></testsuites>
            """);

        await Verify(results);
    }

    [Fact]
    public async Task OverlappingTeardownOutcomesAreNotDeduplicatedOrDropped()
    {
        var results = await ReadAsync("""
            <testsuite tests="3" failures="1" errors="2" skipped="1">
              <testcase classname="fixture" name="same" time="0.1"><failure message="call failed" /></testcase>
              <testcase classname="fixture" name="same" time="0.2"><error message="teardown failed" /></testcase>
              <testcase classname="fixture" name="skipped"><skipped message="skip" /><error message="teardown" /></testcase>
            </testsuite>
            """);

        await Verify(results);
    }

    [Theory]
    [InlineData("<other />")]
    [InlineData("<testsuites />")]
    [InlineData("<testsuite tests='1' failures='0' errors='0' skipped='0' />")]
    [InlineData("<testsuite tests='-1' failures='0' errors='0' skipped='0' />")]
    [InlineData("<testsuite tests='1' failures='1' errors='0' skipped='0'><testcase name='test' /></testsuite>")]
    [InlineData("<testsuite tests='1' failures='0' errors='0' skipped='0'><testcase /></testsuite>")]
    [InlineData("<testsuite tests='1' failures='0' errors='0' skipped='0'><testcase name='test' time='NaN' /></testsuite>")]
    [InlineData("<testsuite tests='1' failures='0' errors='0' skipped='0'><testcase name='test' time='-1' /></testsuite>")]
    [InlineData("<testsuite tests='1' failures='0' errors='0' skipped='0'><testcase name='test' time='1e30' /></testsuite>")]
    [InlineData("<testsuite tests='1' failures='0' errors='0' skipped='0'><testcase name='test'><rerun /></testcase></testsuite>")]
    [InlineData("<testsuite tests='1' failures='0' errors='0' skipped='0'><testsuite /></testsuite>")]
    public async Task InvalidReportsFailExplicitly(string xml)
    {
        await Assert.ThrowsAsync<InvalidDataException>(() => ReadAsync(xml));
    }

    [Fact]
    public async Task PassingCallWithTeardownErrorDoesNotInventAHiddenPassingOutcome()
    {
        var results = await ReadAsync("""
            <testsuite tests="2" failures="0" errors="1" skipped="0">
              <testcase classname="fixture" name="passing_call" time="0.1"><error message="teardown failed" /></testcase>
            </testsuite>
            """);

        await Verify(results);
    }

    [Theory]
    [InlineData("<testsuite>")]
    [InlineData("<!DOCTYPE testsuite [<!ENTITY external SYSTEM 'file:///outside'>]><testsuite>&external;</testsuite>")]
    public async Task MalformedXmlAndExternalEntitiesAreRejected(string xml)
    {
        await Assert.ThrowsAsync<XmlException>(() => ReadAsync(xml));
    }

    [Fact]
    public async Task CancellationDoesNotReturnPartialResults()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes("<testsuite />"));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => PytestJunitReport.ReadAsync(stream, cancellation.Token));
    }

    [Fact]
    public async Task EmptySuiteHasNoInventedResults()
    {
        Assert.Empty(await ReadAsync("<testsuite tests='0' failures='0' errors='0' skipped='0' />"));
    }

    private static async Task<IReadOnlyList<TestResult>> ReadAsync(string xml)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(xml));
        return await PytestJunitReport.ReadAsync(stream, TestContext.Current.CancellationToken);
    }
}
