// <copyright file="ChaosEndpointsFixture.cs" company="Microsoft Corporation">
// Copyright (c) Microsoft Corporation. All rights reserved.
// </copyright>

using ChaosProxy.Container;
using ChaosProxy.Container.Policy;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Aspire.Hosting.Chaos.UnitTests;

/// <summary>
/// Hosts the chaos /chaos/* HTTP control-plane endpoints in-process via TestServer
/// so endpoint contracts (status codes, JSON shapes, validation) can be tested without
/// spinning up YARP, OpenTelemetry, or the chaos middleware pipeline. Wires the same
/// <see cref="ChaosEndpoints.MapChaosEndpoints"/> extension that the production
/// Program.cs uses, so the production contract IS what these tests assert against.
/// </summary>
internal sealed class ChaosEndpointsFixture : IAsyncDisposable
{
    private readonly IHost _host;

    public ChaosEndpointsFixture()
    {
        Store = new ActivePolicyStore();

        var hostBuilder = Host.CreateDefaultBuilder()
            .ConfigureWebHostDefaults(webHost =>
            {
                webHost.UseTestServer();
                webHost.ConfigureServices(services =>
                {
                    services.AddSingleton(Store);
                    services.AddRouting();
                });
                webHost.Configure(app =>
                {
                    app.UseRouting();
                    app.UseEndpoints(endpoints => endpoints.MapChaosEndpoints());
                });
            });

        _host = hostBuilder.Build();
        _host.Start();
        Client = _host.GetTestClient();
    }

    public ActivePolicyStore Store { get; }

    public HttpClient Client { get; }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await _host.StopAsync().ConfigureAwait(false);
        _host.Dispose();
    }
}
