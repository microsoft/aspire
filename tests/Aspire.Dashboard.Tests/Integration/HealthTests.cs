// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Net;
using Aspire.Dashboard.Utils;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using OpenTelemetry;
using OpenTelemetry.Trace;
using Xunit;

namespace Aspire.Dashboard.Tests.Integration;

public class HealthTests(HealthTests.Fixture fixture) : IClassFixture<HealthTests.Fixture>
{
    private const string TestActivitySourceName = "Aspire.Dashboard.Tests.Integration.HealthTests";

    [Fact]
    public async Task HealthEndpoint_SendRequest_200Response()
    {
        await MakeRequestAndAssert($"http://{fixture.App.FrontendSingleEndPointAccessor().EndPoint}", HttpVersion.Version11).DefaultTimeout();
        await MakeRequestAndAssert($"http://{fixture.App.OtlpServiceHttpEndPointAccessor().EndPoint}", HttpVersion.Version11).DefaultTimeout();
        await MakeRequestAndAssert($"http://{fixture.App.OtlpServiceGrpcEndPointAccessor().EndPoint}", HttpVersion.Version20).DefaultTimeout();

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
        using var testActivity = fixture.TestActivitySource.StartActivity(nameof(HealthEndpoint_OtlpExporterConfigured_ExportsAspNetCoreActivity));
        Assert.NotNull(testActivity);
        using var observation = fixture.Exporter.ObserveNext(
            testActivity.TraceId,
            activity => activity.Source.Name == "Microsoft.AspNetCore" && activity.Kind == ActivityKind.Server);

        using var client = new HttpClient { BaseAddress = new Uri($"http://{fixture.App.FrontendSingleEndPointAccessor().EndPoint}") };
        var response = await client.GetAsync($"/{DashboardUrls.HealthBasePath}").DefaultTimeout();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var activity = await observation.Task.DefaultTimeout();
        Assert.Equal(testActivity.TraceId, activity.TraceId);
        Assert.Equal("Microsoft.AspNetCore", activity.Source.Name);
        Assert.Equal(ActivityKind.Server, activity.Kind);
    }

    [Fact]
    public async Task OtlpExporterConfigured_ExportsResourceServiceActivity()
    {
        using var testActivity = fixture.TestActivitySource.StartActivity(nameof(OtlpExporterConfigured_ExportsResourceServiceActivity));
        Assert.NotNull(testActivity);
        using var observation = fixture.Exporter.ObserveNext(
            testActivity.TraceId,
            activity => activity.Source.Name == DashboardActivitySource.ActivitySourceName);
        var activitySource = fixture.App.Services.GetRequiredService<DashboardActivitySource>();

        using (var activity = activitySource.ActivitySource.StartActivity("Test resource update", ActivityKind.Consumer))
        {
            Assert.NotNull(activity);
        }

        var exported = await observation.Task.DefaultTimeout();
        Assert.Equal(testActivity.TraceId, exported.TraceId);
        Assert.Equal(DashboardActivitySource.ActivitySourceName, exported.Source.Name);
        Assert.Equal(ActivityKind.Consumer, exported.Kind);
    }

    public sealed class Fixture : IAsyncLifetime
    {
        public DashboardWebApplication App { get; private set; } = null!;
        internal TestActivityExporter Exporter { get; } = new();
        internal ActivitySource TestActivitySource { get; } = new(TestActivitySourceName);

        public async ValueTask InitializeAsync()
        {
            App = IntegrationTestHelpers.CreateDashboardWebApplication(
                NullLoggerFactory.Instance,
                config => config["OTEL_EXPORTER_OTLP_ENDPOINT"] = "http://127.0.0.1:1",
                builder => builder.Services.AddOpenTelemetry()
                    .WithTracing(tracing => tracing
                        .AddSource(TestActivitySourceName)
                        .AddProcessor(new SimpleActivityExportProcessor(Exporter))));
            await App.StartAsync().DefaultTimeout();
        }

        public async ValueTask DisposeAsync()
        {
            await App.DisposeAsync();
            TestActivitySource.Dispose();
        }
    }

    internal sealed class TestActivityExporter : BaseExporter<Activity>
    {
        private readonly object _lock = new();
        private Observation? _observation;

        public Observation ObserveNext(ActivityTraceId traceId, Func<Activity, bool> predicate)
        {
            var observation = new Observation(this, traceId, predicate);
            lock (_lock)
            {
                Assert.Null(_observation);
                _observation = observation;
            }
            return observation;
        }

        public override ExportResult Export(in Batch<Activity> batch)
        {
            foreach (var activity in batch)
            {
                lock (_lock)
                {
                    if (_observation is { } observation &&
                        observation.TraceId == activity.TraceId &&
                        observation.Predicate(activity))
                    {
                        _observation = null;
                        observation.TrySetResult(activity);
                    }
                }
            }

            return ExportResult.Success;
        }

        private void Remove(Observation observation)
        {
            lock (_lock)
            {
                if (ReferenceEquals(_observation, observation))
                {
                    _observation = null;
                }
            }
        }

        internal sealed class Observation(TestActivityExporter exporter, ActivityTraceId traceId, Func<Activity, bool> predicate) : IDisposable
        {
            private readonly TaskCompletionSource<Activity> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

            public ActivityTraceId TraceId { get; } = traceId;
            public Func<Activity, bool> Predicate { get; } = predicate;
            public Task<Activity> Task => _completion.Task;

            public void Dispose() => exporter.Remove(this);

            public void TrySetResult(Activity activity) => _completion.TrySetResult(activity);
        }
    }
}
