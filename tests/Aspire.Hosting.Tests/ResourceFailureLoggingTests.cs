// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.Utils;
using Aspire.TestUtilities;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
using TestConstants = Microsoft.AspNetCore.InternalTesting.TestConstants;

namespace Aspire.Hosting.Tests;

[Trait("Partition", "2")]
public class ExecutableResourceFailureLoggingTests(ITestOutputHelper testOutputHelper)
{
    [Fact]
    [QuarantinedTest("https://github.com/microsoft/aspire/issues/16189")]
    public async Task ExecutableExitsImmediately()
    {
        using var cts = AsyncTestHelpers.CreateDefaultTimeoutTokenSource(TestConstants.DefaultOrchestratorTestLongTimeout);
        using var builder = TestDistributedApplicationBuilder.Create(testOutputHelper);

        var executable = builder.AddExecutable("pwsh", "pwsh", "")
            .WithArgs("-Command", """
                Write-Host "Hello from Stdout"
                [Console]::Error.WriteLine("Hello from Stderr")
                """);
        AddFakeLogging(executable);

        FakeLogCollector logCollector;
        using (var app = builder.Build())
        {
            logCollector = app.Services.GetFakeLogCollector();
            await app.StartAsync(cts.Token).DefaultTimeout(TestConstants.DefaultOrchestratorTestLongTimeout);
            await app.ResourceNotifications.WaitForResourceAsync(executable.Resource.Name, KnownResourceStates.Finished, cts.Token).DefaultTimeout(TestConstants.DefaultOrchestratorTestLongTimeout);
        }

        var logLines = GetLogLines(logCollector);

        Assert.Contains(logLines, x => x.EndsWith("Hello from Stdout"));
        Assert.Contains(logLines, x => x.EndsWith("Hello from Stderr"));
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
}
