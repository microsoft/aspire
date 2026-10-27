// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.DotNet;
using Aspire.Shared.Telemetry;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

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
        services.AddSingleton<IMachineInformationProvider>(sp => TelemetryMetadata.CreateMachineInformationProvider(
            sp.GetRequiredService<ILoggerFactory>(), sp.GetRequiredService<IEnvironment>()));

        services.AddSingleton<ICIEnvironmentDetector, CIEnvironmentDetector>();
        services.AddSingleton<ICodingAgentDetector, CodingAgentDetector>();
        services.AddSingleton<IInternalMicrosoftDetector>(sp =>
        {
            var context = sp.GetRequiredService<CliExecutionContext>();
            var processFactory = sp.GetRequiredService<IProcessExecutionFactory>();
            return new InternalMicrosoftDetector(context.HomeDirectory, context.WorkingDirectory, context.CacheDirectory,
                sp.GetRequiredService<IEnvironment>(), sp.GetRequiredService<TimeProvider>(),
                sp.GetRequiredService<ILogger<InternalMicrosoftDetector>>(),
                (startInfo, stdout, stderr) => processFactory.CreateExecution(startInfo,
                    new ProcessInvocationOptions
                    {
                        SuppressLogging = true,
                        StandardOutputCallback = stdout,
                        StandardErrorCallback = stderr
                    }),
                sp.GetRequiredService<ICIEnvironmentDetector>().IsCIEnvironment());
        });
        services.AddSingleton<TelemetryTagsSource>();
        services.AddSingleton<AspireCliTelemetry>();
        services.AddSingleton<ProfilingTelemetry>();

        return services;
    }
}
