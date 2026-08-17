// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

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
        services.AddSingleton<IMachineInformationProvider>(sp =>
        {
            var environment = sp.GetRequiredService<IEnvironment>();
            if (environment.IsWindows())
            {
                return ActivatorUtilities.CreateInstance<WindowsMachineInformationProvider>(sp);
            }
            else if (environment.IsMacOS())
            {
                return ActivatorUtilities.CreateInstance<MacOSXMachineInformationProvider>(sp);
            }
            else if (environment.IsLinux())
            {
                return ActivatorUtilities.CreateInstance<LinuxMachineInformationProvider>(sp);
            }
            else
            {
                return ActivatorUtilities.CreateInstance<DefaultMachineInformationProvider>(sp);
            }
        });

        services.AddSingleton<ICIEnvironmentDetector, CIEnvironmentDetector>();
        services.AddSingleton<ICodingAgentDetector, CodingAgentDetector>();
        services.AddSingleton<IInternalMicrosoftDetector, InternalMicrosoftDetector>();
        services.AddSingleton<TelemetryTagsSource>();
        services.AddSingleton<AspireCliTelemetry>();
        services.AddSingleton<ProfilingTelemetry>();
        services.AddHostedService(sp => sp.GetRequiredService<AspireCliTelemetry>());

        return services;
    }
}
