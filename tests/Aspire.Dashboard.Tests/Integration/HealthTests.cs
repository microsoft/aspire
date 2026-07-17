// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using Aspire.Dashboard.Utils;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry;
using OpenTelemetry.Trace;
using Xunit;

namespace Aspire.Dashboard.Tests.Integration;

public class HealthTests(ITestOutputHelper testOutputHelper)
{
    [Fact]
    public async Task HealthEndpoint_SendRequest_200Response()
    {
        // Arrange
        await using var app = IntegrationTestHelpers.CreateDashboardWebApplication(testOutputHelper);
        await app.StartAsync().DefaultTimeout();

        await MakeRequestAndAssert($"http://{app.FrontendSingleEndPointAccessor().EndPoint}", HttpVersion.Version11).DefaultTimeout();
        await MakeRequestAndAssert($"http://{app.OtlpServiceHttpEndPointAccessor().EndPoint}", HttpVersion.Version11).DefaultTimeout();
        await MakeRequestAndAssert($"http://{app.OtlpServiceGrpcEndPointAccessor().EndPoint}", HttpVersion.Version20).DefaultTimeout();

        static async Task MakeRequestAndAssert(string basePath, Version httpVersion)
        {
            using var httpClientHandler = new HttpClientHandler { AllowAutoRedirect = false };
            using var client = new HttpClient(httpClientHandler) { BaseAddress = new Uri(basePath) };

            // Act
            var request = new HttpRequestMessage(HttpMethod.Get, $"/{DashboardUrls.HealthBasePath}");
            request.Version = httpVersion;
            request.VersionPolicy = HttpVersionPolicy.RequestVersionExact;
            var response = await client.SendAsync(request).DefaultTimeout();

            // Assert 
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }
    }

    [Fact]
    public async Task HealthEndpoint_OtlpExporterConfigured_ExportsAspNetCoreActivity()
    {
        var exportedActivities = new ConcurrentQueue<Activity>();
        await using var app = IntegrationTestHelpers.CreateDashboardWebApplication(
            testOutputHelper,
            config => config["OTEL_EXPORTER_OTLP_ENDPOINT"] = "http://127.0.0.1:1",
            builder => builder.Services.AddOpenTelemetry()
                .WithTracing(tracing => tracing.AddProcessor(
                    new SimpleActivityExportProcessor(new TestActivityExporter(exportedActivities)))));
        await app.StartAsync().DefaultTimeout();

        using var client = new HttpClient { BaseAddress = new Uri($"http://{app.FrontendSingleEndPointAccessor().EndPoint}") };
        var response = await client.GetAsync($"/{DashboardUrls.HealthBasePath}").DefaultTimeout();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(exportedActivities, activity =>
            activity.Source.Name == "Microsoft.AspNetCore" &&
            activity.Kind == ActivityKind.Server);
    }

    private sealed class TestActivityExporter(ConcurrentQueue<Activity> exportedActivities) : BaseExporter<Activity>
    {
        public override ExportResult Export(in Batch<Activity> batch)
        {
            foreach (var activity in batch)
            {
                exportedActivities.Enqueue(activity);
            }

            return ExportResult.Success;
        }
    }
}
