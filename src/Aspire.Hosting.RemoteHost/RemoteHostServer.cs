// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Reflection;
using Aspire.Hosting.RemoteHost.Ats;
using Aspire.Hosting.RemoteHost.CodeGeneration;
using Aspire.Hosting.RemoteHost.Diagnostics;
using Aspire.Hosting.RemoteHost.Language;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace Aspire.Hosting.RemoteHost;

/// <summary>
/// Entry point for running the RemoteHost server.
/// </summary>
public static class RemoteHostServer
{
    /// <summary>
    /// Runs the RemoteHost JSON-RPC server, loading ATS assemblies from configuration and available integration assemblies.
    /// </summary>
    /// <remarks>
    /// The server reads the "AtsAssemblies" section from appsettings.json to determine which
    /// assemblies to scan for <c>Aspire.Hosting.AspireExportAttribute</c> capabilities, and it also
    /// probes available <c>Aspire.Hosting*</c> assemblies from the application output and
    /// integration libraries. The appsettings.json should be in the current working directory.
    /// </remarks>
    /// <param name="args">Command line arguments.</param>
    /// <returns>A task that completes when the server has stopped.</returns>
    public static async Task RunAsync(string[] args)
    {
        var builder = CreateBuilder(args);
        using var host = builder.Build();
        var profilingTelemetry = host.Services.GetRequiredService<RemoteHostProfilingTelemetry>();

        using var activity = profilingTelemetry.StartRemoteHostRun();
        try
        {
            await host.RunAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            activity.SetError(ex);
            throw;
        }
    }

    internal static HostApplicationBuilder CreateBuilder(string[] args)
    {
        var builder = Host.CreateApplicationBuilder(args);
        ConfigureAppHostLogLevel(builder.Logging, builder.Configuration);
        ConfigureServices(builder.Services);
        ConfigureProfilingTelemetry(builder);

        return builder;
    }

    private static void ConfigureAppHostLogLevel(ILoggingBuilder logging, IConfiguration configuration)
    {
        var appHostLogLevelValue = configuration[KnownConfigNames.AppHostLogLevel];
        if (appHostLogLevelValue is not null && Enum.TryParse<LogLevel>(appHostLogLevelValue, ignoreCase: true, out var appHostLogLevel))
        {
            // Add a default filter rule after configuration binding so this setting has the
            // same precedence as Logging__LogLevel__Default without being inherited by child processes.
            logging.AddFilter((_, logLevel) => logLevel >= appHostLogLevel);
        }
    }

    private static void ConfigureServices(IServiceCollection services)
    {
        // Hosted services
        services.AddHostedService<OrphanDetector>();
        services.AddHostedService<JsonRpcServer>();

        // Singletons
        services.AddSingleton<RemoteHostProfilingTelemetry>();
        services.AddSingleton<AssemblyLoader>();
        services.AddSingleton<AtsContextFactory>();
        services.AddSingleton(sp => sp.GetRequiredService<AtsContextFactory>().GetContext());
        services.AddSingleton<CodeGeneratorResolver>();
        services.AddSingleton<LanguageSupportResolver>();

        // Scoped services
        services.AddScoped<CodeGenerationService>();
        services.AddScoped<LanguageService>();
        services.AddScoped<JsonRpcAuthenticationState>();
        services.AddScoped<HandleRegistry>();
        services.AddScoped<CancellationTokenRegistry>();
        services.AddScoped<JsonRpcCallbackInvoker>();
        services.AddScoped<ICallbackInvoker>(sp => sp.GetRequiredService<JsonRpcCallbackInvoker>());
        services.AddScoped<AtsCallbackProxyFactory>();
        // Register Lazy<T> for breaking circular dependency between AtsMarshaller and AtsCallbackProxyFactory
        services.AddScoped(sp => new Lazy<AtsCallbackProxyFactory>(() => sp.GetRequiredService<AtsCallbackProxyFactory>()));
        services.AddScoped<AtsMarshaller>();
        services.AddScoped<CapabilityDispatcher>();
        services.AddScoped<RemoteAppHostService>();
    }

    private static void ConfigureProfilingTelemetry(HostApplicationBuilder builder)
    {
        if (!RemoteHostProfilingTelemetry.ShouldConfigureExporter(builder.Configuration))
        {
            return;
        }

        var resourceBuilder = ResourceBuilder.CreateDefault()
            .AddService(
                serviceName: "aspire-remotehost",
                serviceVersion: GetRemoteHostServiceVersion());

        builder.Services.AddOpenTelemetry()
            .WithTracing(tracing =>
            {
                tracing
                    .AddSource(RemoteHostProfilingTelemetry.ActivitySourceName)
                    .SetResourceBuilder(resourceBuilder)
                    .AddOtlpExporter();
            });
    }

    private static string? GetRemoteHostServiceVersion()
    {
        return typeof(RemoteHostServer).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
    }
}
