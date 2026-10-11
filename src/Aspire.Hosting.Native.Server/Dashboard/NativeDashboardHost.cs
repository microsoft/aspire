// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Net;
using System.Security.Cryptography;
using System.Text;
using Aspire.Hosting.Native.Api;
using Aspire.Hosting.Native.Server;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;

namespace Aspire.Hosting.Native.Dashboard;

/// <summary>Hosts the existing Dashboard protocol outside the BCL-only application kernel.</summary>
internal sealed class NativeDashboardHost : IAsyncDisposable
{
    private readonly WebApplication _application;
    private readonly NativeServerOptions _options;
    public Uri Address { get; }

    private NativeDashboardHost(WebApplication application, Uri address, NativeServerOptions options)
    {
        _application = application;
        Address = address;
        _options = options;
    }

    public static Task<NativeDashboardHost> StartAsync(NativeApplicationServer server, string applicationName,
        string apiKey, int port, CancellationToken cancellationToken)
        => StartAsync(server, applicationName, apiKey, port, new(), cancellationToken);

    public static async Task<NativeDashboardHost> StartAsync(NativeApplicationServer server, string applicationName,
        string apiKey, int port, NativeServerOptions options, CancellationToken cancellationToken)
    {
        options.Validate();
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);
        ArgumentOutOfRangeException.ThrowIfNegative(port);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(port, 65535);
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { Args = [] });
        builder.WebHost.ConfigureKestrel(options =>
            options.Listen(IPAddress.Loopback, port, listen => listen.Protocols = HttpProtocols.Http2));
        // ASP.NET Core's container is private to this protocol adapter. Integrations
        // receive scoped ATS capabilities, never framework services or this container.
        builder.Services.AddGrpc(options =>
        {
            options.MaxReceiveMessageSize = 256 * 1024;
            options.MaxSendMessageSize = 4 * 1024 * 1024;
        });
        builder.Services.AddSingleton(new NativeDashboardService(server, applicationName, options.Runtime));
        var application = builder.Build();
        var expected = Encoding.UTF8.GetBytes(apiKey);
        application.Use(async (context, next) =>
        {
            var supplied = context.Request.Headers["x-resource-service-api-key"];
            if (supplied.Count != 1 || supplied[0] is not { } value ||
                !CryptographicOperations.FixedTimeEquals(expected, Encoding.UTF8.GetBytes(value)))
            {
                // Match the resource-service API-key header used by the Dashboard.
                context.Response.StatusCode = 401;
                return;
            }
            await next(context).ConfigureAwait(false);
        });
        application.MapGrpcService<NativeDashboardService>();
        try
        {
            await application.StartAsync(cancellationToken).ConfigureAwait(false);
            var addresses = application.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!;

            return new NativeDashboardHost(application, new Uri(addresses.Addresses.Single()), options);
        }
        catch
        {
            await application.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        using var timeout = new CancellationTokenSource(_options.DashboardShutdownTimeout);
        try
        {
            await _application.StopAsync(timeout.Token).ConfigureAwait(false);
        }
        finally
        {
            await _application.DisposeAsync().ConfigureAwait(false);
        }
    }
}
