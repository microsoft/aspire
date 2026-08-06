// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Utils;
using Aspire.TestUtilities;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
using TestConstants = Microsoft.AspNetCore.InternalTesting.TestConstants;

namespace Aspire.Hosting.Containers.Tests;

[Trait("Partition", "6")]
public class ContainerResourceFailureLoggingTests(ITestOutputHelper testOutputHelper)
{
    [Fact]
    [RequiresFeature(TestFeature.Docker)]
    public async Task BadContainerRuntimeArg()
    {
        using var cts = AsyncTestHelpers.CreateDefaultTimeoutTokenSource(TestConstants.DefaultOrchestratorTestLongTimeout);
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);

        var container = builder.AddContainer("container", "nginx")
            .WithContainerRuntimeArgs("--illegal");
        AddFakeLogging(container);

        FakeLogCollector logCollector;
        using (var app = builder.Build())
        {
            logCollector = app.Services.GetFakeLogCollector();
            await app.StartAsync(cts.Token).DefaultTimeout(TestConstants.DefaultOrchestratorTestLongTimeout);
            await app.ResourceNotifications.WaitForResourceAsync(container.Resource.Name, KnownResourceStates.FailedToStart, cts.Token).DefaultTimeout(TestConstants.DefaultOrchestratorTestLongTimeout);
        }

        var logLines = GetLogLines(logCollector);
        Assert.Contains(logLines, x => x.EndsWith("unknown flag: --illegal"));
    }

    [Fact]
    [RequiresFeature(TestFeature.Docker)]
    public async Task BadImage()
    {
        using var cts = AsyncTestHelpers.CreateDefaultTimeoutTokenSource(TestConstants.DefaultOrchestratorTestLongTimeout);
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);

        var container = builder.AddContainer("container", "does-not-exist")
            .WithImageRegistry("does.not.exist.internal")
            .WithImagePullPolicy(ImagePullPolicy.Always);
        AddFakeLogging(container);

        FakeLogCollector logCollector;
        using (var app = builder.Build())
        {
            logCollector = app.Services.GetFakeLogCollector();
            await app.StartAsync(cts.Token).DefaultTimeout(TestConstants.DefaultOrchestratorTestLongTimeout);
            await app.ResourceNotifications.WaitForResourceAsync(container.Resource.Name, KnownResourceStates.FailedToStart, cts.Token).DefaultTimeout(TestConstants.DefaultOrchestratorTestLongTimeout);
        }

        var logLines = GetLogLines(logCollector);
        Assert.Contains(logLines, x => x.Contains("Error response from daemon"));
    }

    [Fact]
    [RequiresFeature(TestFeature.Docker)]
    public async Task NeedsAuthentication()
    {
        using var cts = AsyncTestHelpers.CreateDefaultTimeoutTokenSource(TestConstants.DefaultOrchestratorTestLongTimeout);
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);

        var container = builder.AddContainer("container", "mattermost.com/go-msft-fips:1.24.6")
            .WithImageRegistry("cgr.dev")
            .WithImagePullPolicy(ImagePullPolicy.Always);
        AddFakeLogging(container);

        FakeLogCollector logCollector;
        using (var app = builder.Build())
        {
            logCollector = app.Services.GetFakeLogCollector();
            await app.StartAsync(cts.Token).DefaultTimeout(TestConstants.DefaultOrchestratorTestLongTimeout);
            await app.ResourceNotifications.WaitForResourceAsync(container.Resource.Name, KnownResourceStates.FailedToStart, cts.Token).DefaultTimeout(TestConstants.DefaultOrchestratorTestLongTimeout);
        }

        var logLines = GetLogLines(logCollector);
        Assert.Contains(logLines, x => x.Contains("Error response from daemon"));
    }

    [Fact]
    [RequiresFeature(TestFeature.Docker)]
    public async Task ContainerExitsImmediatelyAfterStart()
    {
        using var cts = AsyncTestHelpers.CreateDefaultTimeoutTokenSource(TestConstants.DefaultOrchestratorTestLongTimeout);
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);

        var container = builder.AddContainer("container", "nginx")
            .WithArgs("--illegal-argument");
        AddFakeLogging(container);

        FakeLogCollector logCollector;
        using (var app = builder.Build())
        {
            logCollector = app.Services.GetFakeLogCollector();
            await app.StartAsync(cts.Token).DefaultTimeout(TestConstants.DefaultOrchestratorTestLongTimeout);
            await app.ResourceNotifications.WaitForResourceAsync(container.Resource.Name, [KnownResourceStates.Exited, KnownResourceStates.FailedToStart], cts.Token).DefaultTimeout(TestConstants.DefaultOrchestratorTestLongTimeout);

            var logLines = GetLogLines(logCollector);
            AssertSingleLogLine(
                logLines,
                x => x.Contains("exec: --illegal-argument: not found"),
                "container startup failure from nginx entrypoint");
        }
    }

    private static void AddFakeLogging<T>(IResourceBuilder<T> builder)
        where T : IResource
    {
        var category = $"{builder.ApplicationBuilder.Environment.ApplicationName}.Resources.{builder.Resource.Name}";
        builder.ApplicationBuilder.Services.AddLogging(x => x.AddFakeLogging(y => y.FilteredCategories.Add(category)));
    }

    private static List<string> GetLogLines(FakeLogCollector logCollector)
    {
        return [.. logCollector.GetSnapshot()
                .Select(x => x.StructuredState?.SingleOrDefault(x => x.Key == "LineContent"))
                .Where(x => x is not null)
                .Select(x => x?.Value!)];
    }

    private static void AssertSingleLogLine(List<string> logLines, Func<string, bool> predicate, string expected)
    {
        var matches = logLines.Where(predicate).ToList();
        Assert.True(
            matches.Count == 1,
            $"Expected exactly one log line for {expected}, but found {matches.Count}.{Environment.NewLine}Captured resource logs:{Environment.NewLine}{FormatLogLines(logLines)}");
    }

    private static string FormatLogLines(List<string> logLines)
    {
        return logLines.Count == 0
            ? "<none>"
            : string.Join(Environment.NewLine, logLines.Select((line, index) => $"{index + 1}: {line}"));
    }
}
