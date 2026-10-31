// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using TestResult = Aspire.Hosting.ApplicationModel.TestResult;

#pragma warning disable ASPIRETESTING001

namespace Aspire.Hosting.Rust.Tests;

public class NextestCollectionTests(ITestOutputHelper outputHelper)
{
    [Fact]
    public async Task NonzeroExecutionStillPublishesAValidFinalReport()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var output = workspace.CreateDirectory("tests").FullName;
        await File.WriteAllTextAsync(Path.Combine(output, NextestJunitReport.FileName), """
            <testsuites tests="1" failures="1" errors="0">
              <testsuite name="crate" tests="1" failures="1" errors="0">
                <testcase name="failed"><failure>assertion</failure></testcase>
              </testsuite>
            </testsuites>
            """);
        var results = new List<TestResult>();
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var captured = false;
        var context = new ResourceTestingContext(new TestRunContext(workspace.Path), "tests", output,
            results.Add, completion.Task, TestContext.Current.CancellationToken)
        {
            CaptureOutputCallback = (_, _) =>
            {
                captured = true;
                return Task.CompletedTask;
            }
        };
        var collection = NextestJunitReport.CollectAsync(context);
        Assert.False(collection.IsCompleted);
        completion.SetResult(false);
        await collection;
        Assert.True(captured);
        var result = Assert.Single(results);
        Assert.Equal(TestOutcome.Failed, result.Outcome);
        Assert.Equal("crate/failed", result.Name);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingReportFailsRegardlessOfExit(bool passed)
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var output = workspace.CreateDirectory("tests").FullName;
        var context = new ResourceTestingContext(new TestRunContext(workspace.Path), "tests", output,
            _ => Assert.Fail("Missing reports must not fabricate outcomes."), Task.FromResult(passed),
            TestContext.Current.CancellationToken)
        {
            CaptureOutputCallback = (_, _) => Task.CompletedTask
        };
        await Assert.ThrowsAsync<FileNotFoundException>(() => NextestJunitReport.CollectAsync(context));
    }

    [Fact]
    public async Task SymbolicReportIsRejected()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var output = workspace.CreateDirectory("tests").FullName;
        var external = Path.Combine(workspace.Path, "external.xml");
        await File.WriteAllTextAsync(external, "<testsuites/>");
        File.CreateSymbolicLink(Path.Combine(output, NextestJunitReport.FileName), external);
        var context = new ResourceTestingContext(new TestRunContext(workspace.Path), "tests", output,
            _ => Assert.Fail("Symbolic reports must not publish results."), Task.FromResult(true),
            TestContext.Current.CancellationToken)
        {
            CaptureOutputCallback = (_, _) => Task.CompletedTask
        };
        await Assert.ThrowsAsync<InvalidDataException>(() => NextestJunitReport.CollectAsync(context));
    }
}
