// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.RegularExpressions;
using Aspire.Hosting;
using Aspire.Hosting.Tests.Utils;
using Aspire.TestUtilities;
using Microsoft.AspNetCore.InternalTesting;
using SamplesIntegrationTests;
using SamplesIntegrationTests.Infrastructure;
using Xunit;

namespace Aspire.Playground.Tests;

[RequiresFeature(TestFeature.Docker)]
public class ProjectSpecificTests(ITestOutputHelper _testOutput)
{
    [Fact]
    [QuarantinedTest("https://github.com/microsoft/aspire/issues/9171")]
    public async Task WithDockerfileTest()
    {
        var appHost = await DistributedApplicationTestFactory.CreateAsync(typeof(Projects.WithDockerfile_AppHost), _testOutput);
        await using var app = await appHost.BuildAsync();

        await app.StartAsync();
        await app.WaitForResources().WaitAsync(TestConstants.ExtraLongTimeoutTimeSpan);

        await app.WaitForTextAsync($"I'm Batman. - Batman")
                .WaitAsync(TestConstants.ExtraLongTimeoutTimeSpan);

        app.EnsureNoErrorsLogged();
        await app.StopAsync();
    }

    [Fact]
    [QuarantinedTest("https://github.com/microsoft/aspire/issues/6867")]
    public async Task KafkaTest()
    {
        var appHost = await DistributedApplicationTestFactory.CreateAsync(typeof(Projects.KafkaBasic_AppHost), _testOutput);
        await using var app = await appHost.BuildAsync();

        await app.StartAsync();
        await app.WaitForResources().WaitAsync(TestConstants.ExtraLongTimeoutTimeSpan);

        // Wait for the producer to start sending messages
        await app.WaitForTextAsync("Hello, World!").WaitAsync(TestConstants.ExtraLongTimeoutTimeSpan);

        // Wait for the consumer to receive some messages
        await WaitForAllTextAsync(app,
            [
                "Hello, World! 343",
                "Received 1000 messages."
            ],
            timeoutSecs: 120);

        app.EnsureNoErrorsLogged();
        await app.StopAsync();
    }

    [Fact]
    [RequiresFeature(TestFeature.Docker)]
    [RequiresTools(["func"])]
    [ActiveIssue("https://github.com/microsoft/aspire/issues/9243")]
    public async Task AzureFunctionsTest()
    {
        var appHost = await DistributedApplicationTestFactory.CreateAsync(typeof(Projects.AzureFunctionsEndToEnd_AppHost), _testOutput);
        await using var app = await appHost.BuildAsync();

        await app.StartAsync();
        await app.WaitForResources().WaitAsync(TestConstants.ExtraLongTimeoutTimeSpan);

        // Wait for the 'Job host started' message as an indication
        // that the Functions host has initialized correctly
        await WaitForAllTextAsync(app,
            [
                "Worker process started and initialized."
            ],
            resourceName: "funcapp",
            timeoutSecs: 160);

        // Assert that HTTP triggers work correctly
        var response = await AppHostTests.CreateHttpClientWithResilience(app, "funcapp").GetAsync("/api/injected-resources");

        // The output contains multiple text lines.
        // There are some URLs which contain port number, but the port numbers may vary, so we replace those for test validation.
        var output = await response.Content.ReadAsStringAsync();
        output = Regex.Replace(output, pattern: @"(?<=http:\/\/127\.0\.0\.1:)\d+", replacement: "*");

        _testOutput.WriteLine($"[DEBUG] Response:\r\n{output}");
        var expectedStrings = new string[]
        {
            "Aspire-injected EventHubProducerClient namespace: localhost",
            "Aspire-injected QueueServiceClient URI: http://127.0.0.1:*/devstoreaccount1",
            "Aspire-injected BlobServiceClient URI: http://127.0.0.1:*/devstoreaccount1",
            "Aspire-injected BlobContainerClient URI: http://127.0.0.1:*/devstoreaccount1/myblobcontainer"
        };
        foreach (string s in expectedStrings)
        {
            Assert.Contains(s, output);
        }

        await WaitForAllTextAsync(app,
            [
                "Executed 'Functions.injected-resources'"
            ],
            resourceName: "funcapp",
            timeoutSecs: 160);

        using var apiServiceClient = AppHostTests.CreateHttpClientWithResilience(app, "apiservice");
        // Assert that Azure Storage Queue triggers work correctly
        await apiServiceClient.GetAsync("/publish/asq");
        await WaitForAllTextAsync(app,
            [
                "Executed 'Functions.MyAzureQueueTrigger'"
            ],
            resourceName: "funcapp",
            timeoutSecs: 160);

        // Assert that Azure Storage Blob triggers work correctly
        await apiServiceClient.GetAsync("/publish/blob");
        await WaitForAllTextAsync(app,
            [
                "Executed 'Functions.MyAzureBlobTrigger'"
            ],
            resourceName: "funcapp",
            timeoutSecs: 160);

        // Assert that EventHubs triggers work correctly
        await apiServiceClient.GetAsync("/publish/eventhubs");
        await WaitForAllTextAsync(app,
            [
                "Executed 'Functions.MyEventHubTrigger'"
            ],
            resourceName: "funcapp",
            timeoutSecs: 160);

#if !SKIP_UNSTABLE_EMULATORS // https://github.com/microsoft/aspire/issues/7066
        // Assert that ServiceBus triggers work correctly
        await apiServiceClient.GetAsync("/publish/asb");
        await WaitForAllTextAsync(app,
            [
                "Executed 'Functions.MyServiceBusTrigger'"
            ],
            resourceName: "funcapp",
            timeoutSecs: 160);
#endif

        // TODO: The following line is commented out because the test fails due to an erroneous log in the Functions App
        // resource that happens after the Functions host has been built. The error log shows up after the Functions
        // worker extension has been built and before the host has launched.
        // app.EnsureNoErrorsLogged();
        await app.StopAsync();
    }

    internal static Task WaitForAllTextAsync(DistributedApplication app, IEnumerable<string> logTexts, string? resourceName = null, int timeoutSecs = -1)
    {
        CancellationTokenSource cts = new();
        if (timeoutSecs > 0)
        {
            cts.CancelAfter(TimeSpan.FromSeconds(timeoutSecs));
        }

        return app.WaitForAllTextAsync(logTexts, resourceName, cts.Token);
    }
}
