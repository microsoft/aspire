// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Aspire.Hosting.Native.Cli;
using Aspire.Hosting.Native.Dcp;
using Aspire.Hosting.Native.Server;
using Aspire.Hosting.Native.Runtime;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting.Native.Dashboard;

/// <summary>Launches the Dashboard as a DCP executable and reports authenticated frontend readiness.</summary>
internal sealed class DcpDashboard(ILogger logger)
{
    private NativeCliDashboardUrls _urls = new() { DashboardHealthy = false };
    private string? _name;
    private string? _service;
    public NativeCliDashboardUrls Urls => Volatile.Read(ref _urls);
    public string? ApiToken { get; private set; }

    public async Task StartAsync(DcpApiClient client, string dashboardPath, Uri resourceService,
        NativeDashboardCredentials credentials, NativeServerOptions options, CancellationToken cancellationToken)
    {
        var name = "dashboard-" + Guid.NewGuid().ToString("N");
        var service = name + "-frontend";
        var spec = DcpDashboardConfiguration.Create(dashboardPath, service, resourceService, credentials);
        _name = name;
        _service = service;
        var browserToken = credentials.BrowserToken;
        var dashboardApiToken = credentials.DashboardApiKey;
        await client.CreateAsync(DcpResourceCollection.Services, service, JsonSerializer.SerializeToElement(
            new DcpServiceSpec(WorkloadProtocol.Tcp, DcpAddressAllocationMode.Localhost), DcpJsonContext.Default.DcpServiceSpec),
            null, cancellationToken).ConfigureAwait(false);
        await client.CreateAsync(DcpResourceCollection.Executables, name, JsonSerializer.SerializeToElement(spec, DcpJsonContext.Default.DcpWorkloadSpec),
            new Dictionary<string, string>
            {
                ["service-producer"] = JsonSerializer.Serialize(
                    new[] { new DcpServiceProducer(service, IPAddress.Loopback.ToString(), null) },
                    DcpJsonContext.Default.DcpServiceProducerArray)
            }, cancellationToken).ConfigureAwait(false);
        using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
        {
            Timeout = Timeout.InfiniteTimeSpan
        };
        while (true)
        {
            var process = await client.GetAsync(DcpResourceCollection.Executables, name, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("DCP removed the Dashboard during startup.");
            if ((process.Status?.State ?? DcpProcessState.Unknown).IsTerminal())
            {
                throw new InvalidOperationException("The DCP-owned Dashboard failed to start.");
            }
            var allocation = await client.GetAsync(DcpResourceCollection.Services, service, cancellationToken).ConfigureAwait(false);
            if (process.Status is { State: DcpProcessState.Running, Pid: > 0 } && allocation?.Status is { EffectivePort: > 0 and <= 65535 } endpoint)
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
                    using var requestTimeout = new TimeProviderCancellation(options.ControllerRequestTimeout, options.TimeProvider, cancellationToken);
                    using var response = await http.GetAsync(new Uri(frontend, "health"), requestTimeout.Token).ConfigureAwait(false);
                    if (response.IsSuccessStatusCode)
                    {
                        ApiToken = dashboardApiToken;
                        Volatile.Write(ref _urls, new NativeCliDashboardUrls
                        {
                            DashboardHealthy = true,
                            BaseUrlWithLoginToken = new Uri(frontend, "login?t=" + Uri.EscapeDataString(browserToken)).AbsoluteUri
                        });
                        logger.LogInformation("DCP-owned Dashboard ({ProcessId}) listening on {Address}.", process.Status.Pid, frontend);

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
                    logger.LogDebug("Waiting for the DCP-owned Dashboard listener.");
                }
            }
            await Task.Delay(options.RetryInterval, options.TimeProvider, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task StopAsync(DcpApiClient client, CancellationToken cancellationToken)
    {
        Volatile.Write(ref _urls, new NativeCliDashboardUrls { DashboardHealthy = false });
        if (_name is not null)
        {
            await client.DeleteAsync(DcpResourceCollection.Executables, _name, cancellationToken).ConfigureAwait(false);
        }
        if (_service is not null)
        {
            await client.DeleteAsync(DcpResourceCollection.Services, _service, cancellationToken).ConfigureAwait(false);
        }
    }
}
