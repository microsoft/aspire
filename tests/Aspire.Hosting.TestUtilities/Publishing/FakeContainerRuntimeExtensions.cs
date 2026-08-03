// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIRECONTAINERRUNTIME001

using Aspire.Hosting.Publishing;
using Aspire.Shared;
using Microsoft.Extensions.DependencyInjection;

namespace Aspire.Hosting.Tests.Publishing;

/// <summary>
/// Registration helpers that make a <see cref="FakeContainerRuntime"/> the runtime seen by
/// production code, regardless of which container runtime is installed on the host.
/// </summary>
public static class FakeContainerRuntimeExtensions
{
    /// <summary>
    /// Registers <paramref name="runtime"/> as the container runtime used by the application.
    /// </summary>
    /// <remarks>
    /// Registering only the keyed <see cref="IContainerRuntime"/> under <c>"docker"</c> is not enough:
    /// production code resolves the runtime through <see cref="IContainerRuntimeResolver"/>, which
    /// auto-detects the installed runtime and then looks up the keyed service for whichever runtime it
    /// found. On a machine with Podman but no Docker that lookup uses the <c>"podman"</c> key, so a fake
    /// registered only under <c>"docker"</c> is silently bypassed and the real runtime executes.
    /// Overriding the resolver itself keeps tests independent of the host's container runtime.
    /// </remarks>
    public static IServiceCollection AddFakeContainerRuntime(this IServiceCollection services, FakeContainerRuntime runtime)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(runtime);

        services.AddKeyedSingleton<IContainerRuntime>(KnownContainerRuntimes.Docker, runtime);
        services.AddKeyedSingleton<IContainerRuntime>(KnownContainerRuntimes.Podman, runtime);
        services.AddSingleton<IContainerRuntimeResolver>(runtime);

        return services;
    }
}
