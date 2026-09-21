// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Agents.Configuration;
using Aspire.Cli.Agents.Hooks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Aspire.Cli.Agents;

/// <summary>
/// Registers the native agent-configuration layer without acquiring or probing any clients.
/// </summary>
internal static class AgentConfigurationServiceCollectionExtensions
{
    public static IServiceCollection AddAgentConfigurationServices(this IServiceCollection services)
    {
        services.TryAddSingleton<AgentConfigurationPaths>();
        services.TryAddSingleton<AgentConfigurationPlanner>();
        services.TryAddSingleton<AgentConfigurationWriter>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IAgentConfigurationHandler, CopilotConfigurationHandler>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IAgentConfigurationHandler, ClaudeCodeConfigurationHandler>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IAgentConfigurationHandler, VsCodeConfigurationHandler>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IAgentConfigurationHandler, OpenCodeConfigurationHandler>());
        services.TryAddSingleton<ITelemetryHookInstaller, TelemetryHookInstaller>();
        services.TryAddSingleton<ITelemetryHookConfigurator, TelemetryHookConfigurator>();
        services.TryAddSingleton<IAgentInitService, AgentInitService>();

        return services;
    }
}
