// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using Aspire.Hosting.Native.Cli;
using Aspire.Hosting.Native.Dcp;
using Aspire.Hosting.Native.Server;

namespace Aspire.Hosting.Native.Dashboard;

/// <summary>Launches the Dashboard as a DCP executable and reports authenticated frontend readiness.</summary>
internal sealed class DcpDashboard
{
    private NativeCliDashboardUrls _urls = new() { DashboardHealthy = false };
    private string? _name;
    private string? _service;
    public NativeCliDashboardUrls Urls => Volatile.Read(ref _urls);

    public async Task StartAsync(DcpApiClient client, string dashboardPath, Uri resourceService,
        string apiKey, NativeServerOptions options, CancellationToken cancellationToken)
    {
        if (!Path.IsPathFullyQualified(dashboardPath) || !File.Exists(dashboardPath))
        {
            throw new ArgumentException("The Dashboard path must identify an existing absolute executable or assembly.", nameof(dashboardPath));
        }
        var name = "dashboard-" + Guid.NewGuid().ToString("N");
        var service = name + "-frontend";
        _name = name;
        _service = service;
        var browserToken = Environment.GetEnvironmentVariable("AppHost__BrowserToken")
            ?? Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        ArgumentException.ThrowIfNullOrWhiteSpace(browserToken);
        var otlpToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        await client.CreateAsync("services", service, JsonSerializer.SerializeToElement(
            new DcpServiceSpec("TCP", "Localhost"), DcpJsonContext.Default.DcpServiceSpec),
            null, cancellationToken).ConfigureAwait(false);
        var assembly = dashboardPath.EndsWith(".dll", StringComparison.OrdinalIgnoreCase);
        var bundled = Path.GetFileNameWithoutExtension(dashboardPath) == "aspire-managed";
        var spec = new DcpWorkloadSpec
        {
            ExecutablePath = assembly ? "dotnet" : dashboardPath,
            WorkingDirectory = Path.GetDirectoryName(dashboardPath)!,
            ExecutionType = "Process",
            Args = assembly ? [dashboardPath] : bundled ? ["dashboard"] : [],
            Env =
            [
                // DCP allocates and substitutes the frontend port before process
                // launch, exactly as it does for other executable producers.
                new("ASPNETCORE_URLS", "http://127.0.0.1:{{- portForServing \"" + service + "\" -}}"),
                new("ASPIRE_DASHBOARD_OTLP_ENDPOINT_URL", "http://127.0.0.1:0"),
                new("ASPIRE_RESOURCE_SERVICE_ENDPOINT_URL", resourceService.AbsoluteUri),
                new("DASHBOARD__RESOURCESERVICECLIENT__AUTHMODE", "ApiKey"),
                new("DASHBOARD__RESOURCESERVICECLIENT__APIKEY", apiKey),
                new("DASHBOARD__FRONTEND__AUTHMODE", "BrowserToken"),
                new("DASHBOARD__FRONTEND__BROWSERTOKEN", browserToken),
                new("DASHBOARD__OTLP__AUTHMODE", "ApiKey"),
                new("DASHBOARD__OTLP__PRIMARYAPIKEY", otlpToken),
                new("ASPIRE_DASHBOARD_SUPPRESS_BROWSER_TOKEN_IN_OUTPUT", "true")
            ]
        };
        await client.CreateAsync("executables", name, JsonSerializer.SerializeToElement(spec, DcpJsonContext.Default.DcpWorkloadSpec),
            new Dictionary<string, string>
            {
                ["service-producer"] = JsonSerializer.Serialize(
                    new[] { new DcpServiceProducer(service, IPAddress.Loopback.ToString(), null) },
                    DcpJsonContext.Default.DcpServiceProducerArray)
            }, cancellationToken).ConfigureAwait(false);
        using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
        {
            Timeout = options.ControllerRequestTimeout
        };
        while (true)
        {
            var process = await client.GetAsync("executables", name, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("DCP removed the Dashboard during startup.");
            if (process.Status?.State is "Exited" or "Finished" or "FailedToStart")
            {
                throw new InvalidOperationException("The DCP-owned Dashboard failed to start.");
            }
            var allocation = await client.GetAsync("services", service, cancellationToken).ConfigureAwait(false);
            if (process.Status is { State: "Running", Pid: > 0 } && allocation?.Status is { EffectivePort: > 0 and <= 65535 } endpoint)
            {
                var address = endpoint.EffectiveAddress ?? throw new InvalidDataException("The Dashboard allocation has no address.");
                if (!address.Equals("localhost", StringComparison.OrdinalIgnoreCase) &&
                    (!IPAddress.TryParse(address, out var host) || !IPAddress.IsLoopback(host)))
                {
                    throw new InvalidDataException("DCP must allocate a loopback Dashboard endpoint.");
                }
                var frontend = new UriBuilder("http", address, endpoint.EffectivePort.Value).Uri;
                try
                {
                    using var response = await http.GetAsync(new Uri(frontend, "health"), cancellationToken).ConfigureAwait(false);
                    if (response.IsSuccessStatusCode)
                    {
                        Volatile.Write(ref _urls, new NativeCliDashboardUrls
                        {
                            DashboardHealthy = true,
                            BaseUrlWithLoginToken = new Uri(frontend, "login?t=" + Uri.EscapeDataString(browserToken)).AbsoluteUri
                        });
                        Console.WriteLine($"DCP-owned Dashboard ({process.Status.Pid}) listening on {frontend}.");

                        return;
                    }
                    if (response.StatusCode != HttpStatusCode.ServiceUnavailable)
                    {
                        throw new InvalidOperationException($"Dashboard readiness failed ({(int)response.StatusCode}).");
                    }
                }
                catch (HttpRequestException exception) when (exception.InnerException is
                    SocketException { SocketErrorCode: SocketError.ConnectionRefused })
                {
                    Console.Error.WriteLine("Waiting for the DCP-owned Dashboard listener.");
                }
            }
            await Task.Delay(options.RetryInterval, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task StopAsync(DcpApiClient client, CancellationToken cancellationToken)
    {
        Volatile.Write(ref _urls, new NativeCliDashboardUrls { DashboardHealthy = false });
        if (_name is not null)
        {
            await client.DeleteAsync("executables", _name, cancellationToken).ConfigureAwait(false);
        }
        if (_service is not null)
        {
            await client.DeleteAsync("services", _service, cancellationToken).ConfigureAwait(false);
        }
    }
}
