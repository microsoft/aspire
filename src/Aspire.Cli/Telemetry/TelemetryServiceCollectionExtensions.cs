// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Runtime.InteropServices;
using Microsoft.Extensions.DependencyInjection;

namespace Aspire.Cli.Telemetry;

/// <summary>
/// Extension methods for registering telemetry services.
/// </summary>
internal static class TelemetryServiceCollectionExtensions
{
    /// <summary>
    /// Adds telemetry services to the specified <see cref="IServiceCollection"/>.
    /// </summary>
    /// <param name="services">The <see cref="IServiceCollection"/> to add services to.</param>
    /// <returns>The <see cref="IServiceCollection"/> so that additional calls can be chained.</returns>
    public static IServiceCollection AddTelemetryServices(this IServiceCollection services)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            services.AddSingleton<IMachineInformationProvider, WindowsMachineInformationProvider>();
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            services.AddSingleton<IMachineInformationProvider, MacOSXMachineInformationProvider>();
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            services.AddSingleton<IMachineInformationProvider, LinuxMachineInformationProvider>();
        }
        else
        {
            services.AddSingleton<IMachineInformationProvider, DefaultMachineInformationProvider>();
        }

        services.AddSingleton<ICIEnvironmentDetector, CIEnvironmentDetector>();
        services.AddSingleton<AspireCliTelemetry>();
        services.AddHostedService(sp => sp.GetRequiredService<AspireCliTelemetry>());

        return services;
    }
}
