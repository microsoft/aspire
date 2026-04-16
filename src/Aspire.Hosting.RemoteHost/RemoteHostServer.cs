// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Hosting.RemoteHost.Ats;
using Aspire.Hosting.RemoteHost.CodeGeneration;
using Aspire.Hosting.RemoteHost.Language;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

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
    public static Task RunAsync(string[] args)
    {
        var builder = Host.CreateApplicationBuilder(args);
        ConfigureServices(builder.Services);

        var host = builder.Build();

        return host.RunAsync();
    }

    private static void ConfigureServices(IServiceCollection services)
    {
        // Hosted services
        services.AddHostedService<OrphanDetector>();
        services.AddHostedService<JsonRpcServer>();
        // Integration host launcher is registered as a hosted service so its StopAsync
        // runs during graceful shutdown, killing any integration host processes it spawned.
        services.AddSingleton<IntegrationHostLauncher>();
        services.AddHostedService(sp => sp.GetRequiredService<IntegrationHostLauncher>());

        // Singletons
        services.AddSingleton<AssemblyLoader>();
        services.AddSingleton<AtsContextFactory>();
        services.AddSingleton(sp => sp.GetRequiredService<AtsContextFactory>().GetContext());
        services.AddSingleton<CodeGeneratorResolver>();
        services.AddSingleton<LanguageSupportResolver>();
        services.AddSingleton<ExternalCapabilityRegistry>();

        // Scoped services
        services.AddScoped<CodeGenerationService>();
        services.AddScoped<LanguageService>();
        services.AddScoped<JsonRpcAuthenticationState>();
        services.AddSingleton<HandleRegistry>();
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
}
