// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Net;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Aspire.Dashboard.Configuration;
using Aspire.Dashboard.Telemetry;
using Aspire.Tests.Shared.Telemetry;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Aspire.Dashboard.Tests.Telemetry;

public class DashboardTelemetrySenderTests
{
    [Fact]
    public async Task CreateTelemetryService_WithNoDebugSession_ShowsTelemetryUnsupported()
    {
        var options = new TestDashboardOptions(new DashboardOptions
        {
            DebugSession = new DebugSessionOptions()
        });

        var telemetrySender = new DashboardTelemetrySender(options, NullLogger<DashboardTelemetrySender>.Instance);
        var result = await telemetrySender.TryStartTelemetrySessionAsync();

        Assert.False(result);
    }

    [Fact]
    public async Task CreateTelemetryService_WithDebugSession_Optout_ShowsTelemetryUnsupported()
    {
        var options = new TestDashboardOptions(new DashboardOptions
        {
            DebugSession = new DebugSessionOptions
            {
                Port = 5000,
                ServerCertificate = Convert.ToBase64String(TelemetryTestHelpers.GenerateDummyCertificate().Export(X509ContentType.Cert)),
                Token = "test",
                TelemetryOptOut = true
            }
        });

        Assert.True(options.Value.DebugSession.TryParseOptions(out _));

        var telemetrySender = new DashboardTelemetrySender(options, NullLogger<DashboardTelemetrySender>.Instance);
        var result = await telemetrySender.TryStartTelemetrySessionAsync();

        Assert.False(result);
    }

    [Theory]
    [MemberData(nameof(CreateTelemetryService_WithValidDebugSession_DifferentServerResponses_ShowsTelemetrySupported_MemberData))]
    public async Task CreateTelemetryService_WithValidDebugSession_DifferentServerResponses_ShowsTelemetrySupported(
        HttpStatusCode? telemetryEnabledResponseStatusCode,
        string? telemetryEnabledResponseBody,
        HttpStatusCode? startTelemetryResponseStatusCode,
        bool expectedTelemetryEnabled)
    {
        var options = new TestDashboardOptions(new DashboardOptions
        {
            DebugSession = new DebugSessionOptions
            {
                Port = 5000,
                ServerCertificate = Convert.ToBase64String(TelemetryTestHelpers.GenerateDummyCertificate().Export(X509ContentType.Cert)),
                Token = "test",
                DcpInstanceId = "aspire-extension-run-123-dashboard"
            }
        });

        Assert.True(options.Value.DebugSession.TryParseOptions(out _));

        var telemetrySender = new DashboardTelemetrySender(options, NullLogger<DashboardTelemetrySender>.Instance);
        telemetrySender.CreateHandler = handler => new TestHttpMessageHandler(
            (request, cancellationToken) =>
            {
                Assert.Equal("aspire-extension-run-123-dashboard", request.Headers.GetValues("Microsoft-Developer-DCP-Instance-ID").Single());

                if (request.RequestUri!.AbsolutePath == TelemetryEndpoints.TelemetryEnabled)
                {
                    if (telemetryEnabledResponseStatusCode == null)
                    {
                        return Task.FromException<HttpResponseMessage>(new InvalidOperationException());
                    }
                    return Task.FromResult(new HttpResponseMessage(telemetryEnabledResponseStatusCode.Value) { Content = new StringContent(telemetryEnabledResponseBody ?? string.Empty) });
                }
                else if (request.RequestUri!.AbsolutePath == TelemetryEndpoints.TelemetryStart)
                {
                    if (startTelemetryResponseStatusCode == null)
                    {
                        return Task.FromException<HttpResponseMessage>(new InvalidOperationException());
                    }
                    return Task.FromResult(new HttpResponseMessage(startTelemetryResponseStatusCode.Value));
                }
                else
                {
                    throw new InvalidCastException($"Unexpected path: {request.RequestUri}");
                }
            });
        var result = await telemetrySender.TryStartTelemetrySessionAsync();

        Assert.Equal(expectedTelemetryEnabled, result);
    }

    [Theory]
    [InlineData(false, "http://localhost:5000/")]
    [InlineData(true, "https://localhost:5000/")]
    public void CreateTelemetrySender_WithDebugSession_UsesCorrectScheme(bool isHttps, string expectedUrl)
    {
        var options = new TestDashboardOptions(new DashboardOptions
        {
            DebugSession = new DebugSessionOptions
            {
                Port = 5000,
                ServerCertificate = isHttps ? Convert.ToBase64String(TelemetryTestHelpers.GenerateDummyCertificate().Export(X509ContentType.Cert)) : null,
                Token = "test",
                DcpInstanceId = "aspire-extension-run-123-dashboard"
            }
        });

        Assert.True(options.Value.DebugSession.TryParseOptions(out _));

        var telemetrySender = new DashboardTelemetrySender(options, NullLogger<DashboardTelemetrySender>.Instance);
        Assert.True(telemetrySender.TryCreateHttpClient(out var client));

        Assert.NotNull(client);
        Assert.Equal(expectedUrl, client.BaseAddress?.ToString());
        Assert.Equal("aspire-extension-run-123-dashboard", client.DefaultRequestHeaders.GetValues("Microsoft-Developer-DCP-Instance-ID").Single());
    }

    public static TheoryData<HttpStatusCode?, string?, HttpStatusCode?, bool> CreateTelemetryService_WithValidDebugSession_DifferentServerResponses_ShowsTelemetrySupported_MemberData()
    {
        return new TheoryData<HttpStatusCode?, string?, HttpStatusCode?, bool>
        {
            // No result (connection refused)
            { null, null, null, false },
            // 404 (old version of VS/VSC)
            { HttpStatusCode.NotFound, null, null, false},
            // 200 OK but false (telemetry not supported)
            { HttpStatusCode.OK, JsonSerializer.Serialize(new TelemetryEnabledResponse(IsEnabled: false)), null, false },
            // 200 OK but true (telemetry supported)
            { HttpStatusCode.OK, JsonSerializer.Serialize(new TelemetryEnabledResponse(IsEnabled: true)), HttpStatusCode.OK, true },
        };
    }

    public class TestDashboardOptions(DashboardOptions value) : IOptions<DashboardOptions>
    {
        public DashboardOptions Value { get; } = value;
    }
}

internal sealed class TestHttpMessageHandler : DelegatingHandler
{
    private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _value;

    public TestHttpMessageHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> value)
    {
        _value = value;
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        return _value(request, cancellationToken);
    }
}
