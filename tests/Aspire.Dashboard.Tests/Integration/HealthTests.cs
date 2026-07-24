// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

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
        var exportedActivity = new TaskCompletionSource<Activity>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var app = IntegrationTestHelpers.CreateDashboardWebApplication(
            testOutputHelper,
            config => config["OTEL_EXPORTER_OTLP_ENDPOINT"] = "http://127.0.0.1:1",
            builder => builder.Services.AddOpenTelemetry()
                .WithTracing(tracing => tracing.AddProcessor(
                    new SimpleActivityExportProcessor(new TestActivityExporter(
                        exportedActivity,
                        activity => activity.Source.Name == "Microsoft.AspNetCore" && activity.Kind == ActivityKind.Server)))));
        await app.StartAsync().DefaultTimeout();

        using var client = new HttpClient { BaseAddress = new Uri($"http://{app.FrontendSingleEndPointAccessor().EndPoint}") };
        var response = await client.GetAsync($"/{DashboardUrls.HealthBasePath}").DefaultTimeout();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var activity = await exportedActivity.Task.DefaultTimeout();
        Assert.Equal("Microsoft.AspNetCore", activity.Source.Name);
        Assert.Equal(ActivityKind.Server, activity.Kind);
    }

    [Fact]
    public async Task OtlpExporterConfigured_ExportsResourceServiceActivity()
    {
        var exportedActivity = new TaskCompletionSource<Activity>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var app = IntegrationTestHelpers.CreateDashboardWebApplication(
            testOutputHelper,
            config => config["OTEL_EXPORTER_OTLP_ENDPOINT"] = "http://127.0.0.1:1",
            builder => builder.Services.AddOpenTelemetry()
                .WithTracing(tracing => tracing.AddProcessor(
                    new SimpleActivityExportProcessor(new TestActivityExporter(
                        exportedActivity,
                        activity => activity.Source.Name == DashboardActivitySource.ActivitySourceName)))));
        await app.StartAsync().DefaultTimeout();
        var activitySource = app.Services.GetRequiredService<DashboardActivitySource>();

        using (var activity = activitySource.ActivitySource.StartActivity("Test resource update", ActivityKind.Consumer))
        {
            Assert.NotNull(activity);
        }

        var exported = await exportedActivity.Task.DefaultTimeout();
        Assert.Equal(DashboardActivitySource.ActivitySourceName, exported.Source.Name);
        Assert.Equal(ActivityKind.Consumer, exported.Kind);
    }

    private sealed class TestActivityExporter(
        TaskCompletionSource<Activity> exportedActivity,
        Func<Activity, bool> predicate) : BaseExporter<Activity>
    {
        public override ExportResult Export(in Batch<Activity> batch)
        {
            foreach (var activity in batch)
            {
                if (predicate(activity))
                {
                    exportedActivity.TrySetResult(activity);
                }
            }

            return ExportResult.Success;
        }
    }
}
