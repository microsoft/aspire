// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.Native.Server;
using Aspire.Shared;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

BundleVersionLease? acquiredLease;
try
{
    acquiredLease = BundleVersionLease.TryAcquireFromEnvironment("apphost-server-v2", args.FirstOrDefault());
}
catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
{
    Console.Error.WriteLine($"Failed to acquire Aspire bundle lease: {ex.Message}");
    Environment.ExitCode = 1;
    return;
}
using var bundleLease = acquiredLease;

// Native transport arguments belong to our parser, not the generic host's
// command-line configuration provider. Keep its existing environment contract.
var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { DisableDefaults = true });
builder.Configuration.AddEnvironmentVariables();
var settings = NativeServerConfiguration.Parse(builder.Configuration, launchedByCli: args is ["server", "--contentRoot", _]);
builder.Logging
    .SetMinimumLevel(settings.TraceOperations ? LogLevel.Debug : LogLevel.Information)
    .AddSimpleConsole(options => options.SingleLine = true)
    .AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);
builder.Services.AddSingleton(settings);
builder.Services.AddSingleton(settings.Options.TimeProvider);
builder.Services.Configure<ConsoleLifetimeOptions>(options => options.SuppressStatusMessages = true);
builder.Services.Configure<HostOptions>(options => options.ShutdownTimeout = settings.Options.CleanupTimeout);
builder.Services.AddHostedService(services => new NativeServerHostedService(settings, args,
    services.GetRequiredService<ILoggerFactory>(), services.GetRequiredService<IHostApplicationLifetime>()));
using var host = builder.Build();
await host.RunAsync().ConfigureAwait(false);
