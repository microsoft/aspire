// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text;
using Aspire.Hosting.ApplicationModel;
using TestResult = Aspire.Hosting.ApplicationModel.TestResult;

#pragma warning disable ASPIRETESTING001

namespace Aspire.Hosting.Go.Tests;

public class GoTestJsonReportTests
{
    [Fact]
    public async Task InterleavedPackagesSubtestsAndRepeatedInvocationsPreserveIdentity()
    {
        var results = await ReadAsync("""
            {"Action":"start","Package":"p"}
            {"Action":"start","Package":"q"}
            {"Action":"run","Package":"p","Test":"TestParent"}
            {"Action":"run","Package":"q","Test":"TestOther"}
            {"Action":"run","Package":"p","Test":"TestParent/child"}
            {"Action":"output","Package":"p","Test":"TestParent/child","Output":"hello: 世界\n"}
            {"Action":"skip","Package":"p","Test":"TestParent/child","Elapsed":0.01}
            {"Action":"pass","Package":"q","Test":"TestOther","Elapsed":0.125}
            {"Action":"pass","Package":"q","Elapsed":0.2}
            {"Action":"pass","Package":"p","Test":"TestParent","Elapsed":0.3}
            {"Action":"run","Package":"p","Test":"TestParent"}
            {"Action":"pass","Package":"p","Test":"TestParent","Elapsed":0.1}
            {"Action":"pass","Package":"p","Elapsed":0.4}
            """);
        Assert.Collection(results,
            result => Assert.Equal(("q.TestOther [invocation 1]", TestOutcome.Passed, TimeSpan.FromMilliseconds(125)),
                (result.Name, result.Outcome, result.Duration)),
            result =>
            {
                Assert.Equal("p.TestParent [invocation 1]", result.Name);
                Assert.Equal("TestParent/child [invocation 1]: Skipped\nhello: 世界\n", result.Message);
            },
            result => Assert.Equal("p.TestParent [invocation 2]", result.Name));
    }

    [Fact]
    public async Task ParentFailureAndPartialResultsSurvivePackageBuildFailure()
    {
        var results = new List<TestResult>();
        await Assert.ThrowsAsync<InvalidDataException>(() => ParseAsync("""
            {"Action":"build-output","ImportPath":"broken","Output":"compiler diagnostic\n"}
            {"Action":"build-fail","ImportPath":"broken"}
            {"Action":"fail","Package":"broken","FailedBuild":"broken"}
            {"Action":"start","Package":"p"}
            {"Action":"run","Package":"p","Test":"TestParent"}
            {"Action":"run","Package":"p","Test":"TestParent/child"}
            {"Action":"pass","Package":"p","Test":"TestParent/child","Elapsed":0.1}
            {"Action":"output","Package":"p","Test":"TestParent","Output":"cleanup failed\n","OutputType":"error"}
            {"Action":"fail","Package":"p","Test":"TestParent","Elapsed":0.2}
            {"Action":"fail","Package":"p","Elapsed":0.3}
            """, results.Add));
        var result = Assert.Single(results);
        Assert.Equal(TestOutcome.Failed, result.Outcome);
        Assert.Equal("TestParent/child [invocation 1]: Passed\ncleanup failed\n", result.Message);
    }

    [Theory]
    [InlineData("pass")]
    [InlineData("skip")]
    public async Task ValidZeroTestPackageIsNotAnInventedTest(string action)
    {
        Assert.Empty(await ReadAsync($$"""
            {"Action":"start","Package":"p"}
            {"Action":"{{action}}","Package":"p"}
            """));
    }

    [Theory]
    [InlineData("")]
    [InlineData("{\"Action\":\"start\",\"Package\":\"p\"}")]
    [InlineData("{\"Action\":\"unknown\",\"Package\":\"p\"}")]
    [InlineData("{\"Action\":\"bench\",\"Package\":\"p\",\"Test\":\"BenchmarkX\"}")]
    [InlineData("{\"Action\":\"pass\",\"Package\":\"p\",\"Test\":\"TestMissing\"}")]
    [InlineData("{\"Action\":\"build-fail\",\"ImportPath\":\"p\"}")]
    [InlineData("{\"Action\":\"build-output\",\"ImportPath\":\"p\"}")]
    [InlineData("{\"Action\":\"output\",\"Package\":\"p\"}")]
    [InlineData("{\"Action\":\"run\",\"Package\":\"p\",\"Test\":\"\"}")]
    public async Task MissingIncompleteAndUnsupportedEventsFailExplicitly(string input)
    {
        await Assert.ThrowsAsync<InvalidDataException>(() => ReadAsync(input));
    }

    [Fact]
    public async Task ParentCannotCompleteBeforeItsActiveSubtests()
    {
        await Assert.ThrowsAsync<InvalidDataException>(() => ParseAsync("""
            {"Action":"run","Package":"p","Test":"TestParent"}
            {"Action":"run","Package":"p","Test":"TestParent/child"}
            {"Action":"pass","Package":"p","Test":"TestParent"}
            """, _ => Assert.Fail()));
    }

    [Fact]
    public async Task MalformedOutputAfterACompletedTestDoesNotEraseTheResult()
    {
        var results = new List<TestResult>();
        await Assert.ThrowsAnyAsync<System.Text.Json.JsonException>(() => ParseAsync("""
            {"Action":"start","Package":"p"}
            {"Action":"run","Package":"p","Test":"TestOne"}
            {"Action":"pass","Package":"p","Test":"TestOne"}
            {malformed
            """, results.Add));
        Assert.Equal("p.TestOne [invocation 1]", Assert.Single(results).Name);
    }

    [Theory]
    [InlineData("-1")]
    [InlineData("1e100")]
    public async Task InvalidDurationsFail(string duration)
    {
        await Assert.ThrowsAsync<InvalidDataException>(() => ReadAsync($$"""
            {"Action":"run","Package":"p","Test":"TestOne"}
            {"Action":"pass","Package":"p","Test":"TestOne","Elapsed":{{duration}}}
            {"Action":"pass","Package":"p"}
            """));
    }

    [Fact]
    public async Task CancellationDoesNotProducePassingEmptyResults()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes("{\"Action\":\"pass\",\"Package\":\"p\"}"));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            GoTestJsonReport.ReadAsync(stream, _ => Assert.Fail(), cancellation.Token));
    }

    [Fact]
    public async Task LargeOutputHasExplicitlyBoundedMessages()
    {
        var text = new string('x', 100_000);
        var results = await ReadAsync($$"""
            {"Action":"run","Package":"p","Test":"TestOne"}
            {"Action":"output","Package":"p","Test":"TestOne","Output":"{{text}}"}
            {"Action":"pass","Package":"p","Test":"TestOne"}
            {"Action":"pass","Package":"p"}
            """);
        var message = Assert.Single(results).Message!;
        Assert.Equal(new string('x', 64 * 1024) +
            "\n[Message truncated; complete output is in the raw artifact.]", message);
    }

    private static async Task<List<TestResult>> ReadAsync(string input)
    {
        var results = new List<TestResult>();
        await ParseAsync(input, results.Add);
        return results;
    }

    private static async Task ParseAsync(string input, Action<TestResult> publish)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(input));
        await GoTestJsonReport.ReadAsync(stream, publish, TestContext.Current.CancellationToken);
    }
}
