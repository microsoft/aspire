
// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using OpenTelemetry.Exporter;
using Xunit;

namespace Aspire.Seq.Tests;

public class SeqTests
{
    [Fact]
    public void SeqEndpointCanBeConfigured()
    {
        var builder = Host.CreateEmptyApplicationBuilder(null);
        builder.AddSeqEndpoint("seq", s =>
        {
            s.DisableHealthChecks = true;
            s.Logs.TimeoutMilliseconds = 1000;
            s.Traces.Protocol = OtlpExportProtocol.Grpc;
        });

        using var host = builder.Build();
    }

    [Fact]
    public void ServerUrlSettingOverridesExporterEndpoints()
    {
        var builder = Host.CreateEmptyApplicationBuilder(null);
        var serverUrl = "http://localhost:9876";

        SeqSettings settings = new SeqSettings();

        builder.AddSeqEndpoint("seq", s =>
        {
            settings = s;
            s.ServerUrl = serverUrl;
            s.ApiKey = "TestKey123!";
            s.Logs.Endpoint = new Uri("http://localhost:1234/ingest/otlp/v1/logs");
            s.Traces.Endpoint = new Uri("http://localhost:1234/ingest/otlp/v1/traces");
        });

        Assert.Equal(settings.Logs.Endpoint, new Uri("http://localhost:9876/ingest/otlp/v1/logs"));
        Assert.Equal(settings.Traces.Endpoint, new Uri("http://localhost:9876/ingest/otlp/v1/traces"));
    }

    [Fact]
    public void ApiKeySettingIsMergedWithConfiguredHeaders()
    {
        var builder = Host.CreateEmptyApplicationBuilder(null);

        SeqSettings settings = new SeqSettings();

        builder.AddSeqEndpoint("seq", s =>
        {
            settings = s;
            s.DisableHealthChecks = true;
            s.ApiKey = "TestKey123!";
            s.Logs.Headers = "speed=fast,quality=good";
            s.Traces.Headers = "quality=good,speed=fast";
        });

        Assert.Equal("speed=fast,quality=good,X-Seq-ApiKey=TestKey123!", settings.Logs.Headers);
        Assert.Equal("quality=good,speed=fast,X-Seq-ApiKey=TestKey123!", settings.Traces.Headers);
    }

    [Fact]
    public async Task HealthCheckDescriptionDoesNotExposeCredentialsFromServerUrl()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var endpoint = new Uri($"http://127.0.0.1:{port}");
        using var serverCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var serverTask = WriteResponseAsync(listener, HttpStatusCode.ServiceUnavailable, serverCts.Token);
        var serverUrl = $"http://seq-user:secret-pass@{endpoint.Host}:{endpoint.Port}";
        var builder = Host.CreateEmptyApplicationBuilder(null);
        builder.Configuration.AddInMemoryCollection([
            new KeyValuePair<string, string?>("ConnectionStrings:seq", serverUrl)
        ]);

        builder.AddSeqEndpoint("seq");

        using var host = builder.Build();
        var healthCheckService = host.Services.GetRequiredService<HealthCheckService>();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var healthCheckReport = await healthCheckService.CheckHealthAsync(cts.Token);
        Assert.True(healthCheckReport.Entries.TryGetValue("Seq", out var entry));

        Assert.Equal(HealthStatus.Unhealthy, entry.Status);
        Assert.NotNull(entry.Description);
        Assert.Contains($"http://{endpoint.Host}:{endpoint.Port}/health", entry.Description);
        Assert.DoesNotContain("seq-user", entry.Description);
        Assert.DoesNotContain("secret-pass", entry.Description);
        await serverTask;
    }

    private static async Task WriteResponseAsync(TcpListener listener, HttpStatusCode statusCode, CancellationToken cancellationToken)
    {
        using var client = await listener.AcceptTcpClientAsync(cancellationToken);
        await using var stream = client.GetStream();

        var response = Encoding.ASCII.GetBytes(
            $"HTTP/1.1 {(int)statusCode} {statusCode}\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
        await stream.WriteAsync(response, cancellationToken);
    }
}
