// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Net;
using System.Text.Json;
using Aspire.TestUtilities;
using Aspire.Hosting.Testing;
using Microsoft.AspNetCore.InternalTesting;

namespace Aspire.Hosting.JavaScript.Tests;

[RequiresTools(["deno"])]
public class DenoFunctionalTests : IClassFixture<DenoAppFixture>
{
    private readonly DenoAppFixture _denoFixture;

    public DenoFunctionalTests(DenoAppFixture denoFixture)
    {
        _denoFixture = denoFixture;
    }

    [Fact]
    public async Task VerifyDenoAppDirectExecutionWorks()
    {
        using var cts = new CancellationTokenSource(TestConstants.LongTimeoutDuration);
        using var denoClient = _denoFixture.App.CreateHttpClient(_denoFixture.DenoAppBuilder!.Resource.Name, "http");
        var response = await denoClient.GetStringAsync("/", cts.Token);

        Assert.Equal("Hello from deno!", response);
    }

    [Fact]
    public async Task VerifyDenoAppTaskScriptWorks()
    {
        using var cts = new CancellationTokenSource(TestConstants.LongTimeoutDuration);
        using var denoClient = _denoFixture.App.CreateHttpClient(_denoFixture.DenoScriptBuilder!.Resource.Name, "http");
        var response = await denoClient.GetStringAsync("/", cts.Token);

        Assert.Equal("Hello from deno task!", response);
    }

    [Fact]
    public async Task VerifyDenoAppExportsNativeTelemetryToDashboard()
    {
        using var cts = new CancellationTokenSource(TestConstants.LongTimeoutDuration);
        var resourceName = _denoFixture.DenoAppBuilder!.Resource.Name;
        using var denoClient = _denoFixture.App.CreateHttpClient(resourceName, "http");

        var response = await denoClient.GetStringAsync("/", cts.Token);
        Assert.Equal("Hello from deno!", response);

        await WaitForDashboardTelemetryAsync(_denoFixture.App, resourceName, cts.Token);
    }

    private static async Task WaitForDashboardTelemetryAsync(DistributedApplication app, string resourceName, CancellationToken cancellationToken)
    {
        using var dashboardClient = app.CreateHttpClient(DenoAppFixture.AspireDashboardResourceName, "http");
        dashboardClient.DefaultRequestHeaders.TryAddWithoutValidation("x-api-key", DenoAppFixture.DashboardApiKey);

        var resourceQuery = Uri.EscapeDataString(resourceName);
        var tracesRequest = $"/api/telemetry/traces?resource={resourceQuery}&limit=10";
        var logsRequest = $"/api/telemetry/logs?resource={resourceQuery}&limit=10";

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var traceCount = await GetTelemetryCountAsync(dashboardClient, tracesRequest, cancellationToken);
            var logCount = await GetTelemetryCountAsync(dashboardClient, logsRequest, cancellationToken);

            if (traceCount > 0 || logCount > 0)
            {
                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken);
        }
    }

    private static async Task<int> GetTelemetryCountAsync(HttpClient client, string requestUri, CancellationToken cancellationToken)
    {
        using var response = await client.GetAsync(requestUri, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return 0;
        }

        response.EnsureSuccessStatusCode();
        using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        return document.RootElement.GetProperty("returnedCount").GetInt32();
    }
}
