// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.Agents.AI.Foundry.Hosting;

/// <summary>
/// Registration helpers for the developer-only utilities shipped in this sample.
/// </summary>
public static class HostedContributorSetupExtensions
{
    /// <summary>
    /// Registers developer-only services that allow a hosted Foundry agent to run outside the
    /// Foundry platform, for example inside a Docker container during contributor debugging.
    /// </summary>
    /// <param name="services">The service collection to register the developer-only services into.</param>
    /// <returns>The same <see cref="IServiceCollection"/> for chaining.</returns>
    public static IServiceCollection AddDevTemporaryLocalContributorSetup(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton<HostedSessionIsolationKeyProvider, DevTemporaryLocalUserIdProvider>();

        return services;
    }
}