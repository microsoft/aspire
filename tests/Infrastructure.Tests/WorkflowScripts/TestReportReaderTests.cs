// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.TestTools;
using Xunit;

namespace Infrastructure.Tests;

public sealed class TestReportReaderTests(ITestOutputHelper output)
{
    [Fact]
    public async Task MochaReaderPreservesAssertionsAndHarnessFailuresForCallerClassification()
    {
        using var workspace = TemporaryWorkspace.Create(output);
        var file = Path.Combine(workspace.Path, "mocha.json");
        await File.WriteAllTextAsync(
            file,
            """
            {
              "tests": [{"fullTitle":"suite test(value: one/two)"},{"title":"legacy title"}],
              "failures": [
                {"fullTitle":"suite test(value: one/two)","err":{"name":"AssertionError","message":"Expected true","stack":"frame"}},
                {"title":"after each hook","err":{"name":"NoSuchSessionError","message":"Browser closed"}}
              ]
            }
            """);

        var report = MochaReader.Read(file);

        Assert.Equal(["suite test(value: one/two)", "legacy title"], report.CompletedTests);
        Assert.Collection(
            report.Failures,
            failure =>
            {
                Assert.Equal("suite test(value: one/two)", failure.Name);
                Assert.Equal("AssertionError", failure.ErrorName);
                Assert.Equal("Expected true", failure.ErrorMessage);
                Assert.Equal("frame", failure.StackTrace);
            },
            failure =>
            {
                Assert.Equal("after each hook", failure.Name);
                Assert.Equal("NoSuchSessionError", failure.ErrorName);
                Assert.Equal("Browser closed", failure.ErrorMessage);
                Assert.Equal("", failure.StackTrace);
            });
    }

    [Theory]
    [InlineData("not JSON")]
    [InlineData("[]")]
    [InlineData("""{"tests":[],"failures":{}}""")]
    [InlineData("""{"tests":[false],"failures":[]}""")]
    [InlineData("""{"tests":[],"failures":[{"fullTitle":"test"}]}""")]
    [InlineData("""{"tests":[],"failures":[{"fullTitle":"test","err":{"message":[]}}]}""")]
    [InlineData("""{"tests":[{"fullTitle":"bad\uD800"}],"failures":[]}""")]
    public async Task MochaReaderRejectsMalformedReports(string json)
    {
        using var workspace = TemporaryWorkspace.Create(output);
        var file = Path.Combine(workspace.Path, "mocha.json");
        await File.WriteAllTextAsync(file, json);

        Assert.ThrowsAny<System.Text.Json.JsonException>(() => MochaReader.Read(file));
    }

    [Fact]
    public async Task MochaReaderAcceptsEmptyCompletedReport()
    {
        using var workspace = TemporaryWorkspace.Create(output);
        var file = Path.Combine(workspace.Path, "mocha.json");
        await File.WriteAllTextAsync(file, """{"tests":[],"failures":[]}""");

        var report = MochaReader.Read(file);

        Assert.Empty(report.CompletedTests);
        Assert.Empty(report.Failures);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" xmlns=\"http://microsoft.com/schemas/VisualStudio/TeamTest/2010\"")]
    public async Task TrxDetailedResultsRetainCanonicalNamesAndNewDiagnostics(string xmlNamespace)
    {
        using var workspace = TemporaryWorkspace.Create(output);
        var file = Path.Combine(workspace.Path, "results.trx");
        await File.WriteAllTextAsync(
            file,
            $$"""
            <TestRun{{xmlNamespace}}>
              <TestDefinitions>
                <UnitTest id="test"><TestMethod className="Canonical.Type" name="Method" /></UnitTest>
              </TestDefinitions>
              <Results>
                <UnitTestResult testId="test" testName="Display(value: one/two)" outcome="Failed" executionId="execution-one">
                  <Output><ErrorInfo><Message>Failure</Message><StackTrace>frame</StackTrace></ErrorInfo><StdOut>stdout</StdOut><StdErr>stderr</StdErr></Output>
                </UnitTestResult>
                <UnitTestResult testName="Passed" outcome="Passed" />
              </Results>
            </TestRun>
            """);

        var result = Assert.Single(TrxReader.GetDetailedTestResultsFromTrx(file, result => result.Outcome == "Failed"));

        Assert.Equal("Canonical.Type.Method", result.CanonicalName);
        Assert.Equal("Display(value: one/two)", result.DisplayName);
        Assert.Equal("Failure", result.ErrorMessage);
        Assert.Equal("frame", result.StackTrace);
        Assert.Equal("stdout", result.Stdout);
        Assert.Equal("stderr", result.Stderr);
        Assert.Equal("execution-one", result.ExecutionId);
    }

    [Theory]
    [InlineData("<!DOCTYPE TestRun [<!ELEMENT TestRun ANY>]><TestRun />")]
    [InlineData("<NotATrx />")]
    [InlineData("<TestRun xmlns=\"urn:wrong\" />")]
    [InlineData("<TestRun><")]
    public async Task TrxReaderRejectsUnsafeOrInvalidXml(string xml)
    {
        using var workspace = TemporaryWorkspace.Create(output);
        var file = Path.Combine(workspace.Path, "results.trx");
        await File.WriteAllTextAsync(file, xml);

        Assert.ThrowsAny<System.Xml.XmlException>(() => TrxReader.DeserializeTrxFile(file));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" xmlns=\"http://microsoft.com/schemas/VisualStudio/TeamTest/2010\"")]
    public async Task TrxReaderPreservesRawResultsWithoutCanonicalizingDefinitions(string xmlNamespace)
    {
        using var workspace = TemporaryWorkspace.Create(output);
        var file = Path.Combine(workspace.Path, "results.trx");
        await File.WriteAllTextAsync(
            file,
            $$"""
            <TestRun{{xmlNamespace}}>
              <TestDefinitions>
                <UnitTest id="same"><TestMethod className="Canonical.Type" name="Method" /></UnitTest>
                <UnitTest id="same"><TestMethod className="Other.Type" name="Other" /></UnitTest>
              </TestDefinitions>
              <Results>
                <UnitTestResult testId="same" testName="Tests.Method(value: &quot;one/two&quot;)" outcome="Failed" executionId="first">
                  <Output><ErrorInfo><Message>Failure</Message><StackTrace>frame</StackTrace></ErrorInfo><StdOut>stdout</StdOut><StdErr>stderr</StdErr></Output>
                </UnitTestResult>
                <UnitTestResult testName="No definition" outcome="Passed" />
              </Results>
            </TestRun>
            """);

        var run = TrxReader.DeserializeTrxFile(file)!;

        Assert.Collection(
            run.Results!.UnitTestResults!,
            result =>
            {
                Assert.Equal("Tests.Method(value: \"one/two\")", result.TestName);
                Assert.Equal("Failed", result.Outcome);
                Assert.Equal("Failure", result.Output!.ErrorMessage);
                Assert.Equal("frame", result.Output.StackTrace);
                Assert.Equal("stdout", result.Output.StdOut);
                Assert.Equal("stderr", result.Output.StdErr);
                Assert.Equal("first", result.ExecutionId);
            },
            result =>
            {
                Assert.Equal("No definition", result.TestName);
                Assert.Equal("Passed", result.Outcome);
            });
    }
}
