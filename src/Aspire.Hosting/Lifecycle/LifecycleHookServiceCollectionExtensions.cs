// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Aspire.Hosting.Lifecycle;

/// <summary>
/// Provides extension methods for adding lifecycle hooks to the <see cref="IServiceCollection"/>.
/// </summary>
public static class LifecycleHookServiceCollectionExtensions
{
    /// <summary>
    /// Adds a distributed application lifecycle hook to the service collection.
    /// </summary>
    /// <typeparam name="T">The type of the distributed application lifecycle hook to add.</typeparam>
    /// <param name="services">The <see cref="IServiceCollection"/> to add the distributed application lifecycle hook to.</param>
    [Obsolete("Use EventingSubscriberServiceCollectionExtensions.AddEventingSubscriber instead.")]
    [AspireExportIgnore(Reason = "IServiceCollection is not exported to ATS, and generic lifecycle hook registration is not ATS-compatible. Use IDistributedApplicationBuilder.addEventingSubscriber instead.")]
    public static void AddLifecycleHook<T>(this IServiceCollection services) where T : class, IDistributedApplicationLifecycleHook
    {
        services.AddSingleton<IDistributedApplicationLifecycleHook, T>();
    }

    /// <summary>
    /// Attempts to add a distributed application lifecycle hook to the service collection.
    /// </summary>
    /// <typeparam name="T">The type of the distributed application lifecycle hook to add.</typeparam>
    /// <param name="services">The <see cref="IServiceCollection"/> to add the distributed application lifecycle hook to.</param>
    [Obsolete("Use EventingSubscriberServiceCollectionExtensions.TryAddEventingSubscriber instead.")]
    [AspireExportIgnore(Reason = "IServiceCollection is not exported to ATS, and generic lifecycle hook registration is not ATS-compatible. Use IDistributedApplicationBuilder.tryAddEventingSubscriber instead.")]
    public static void TryAddLifecycleHook<T>(this IServiceCollection services) where T : class, IDistributedApplicationLifecycleHook
    {
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IDistributedApplicationLifecycleHook, T>());
    }

    /// <summary>
    /// Adds a distributed application lifecycle hook to the service collection.
    /// </summary>
    /// <typeparam name="T">The type of the distributed application lifecycle hook.</typeparam>
    /// <param name="services">The service collection to add the hook to.</param>
    /// <param name="implementationFactory">A factory function that creates the hook implementation.</param>
    [Obsolete("Use EventingSubscriberServiceCollectionExtensions.AddEventingSubscriber instead.")]
    [AspireExportIgnore(Reason = "IServiceCollection is not exported to ATS, and IServiceProvider factory registration is not ATS-compatible. Use IDistributedApplicationBuilder.addEventingSubscriber instead.")]
    public static void AddLifecycleHook<T>(this IServiceCollection services, Func<IServiceProvider, T> implementationFactory) where T : class, IDistributedApplicationLifecycleHook
    {
        services.AddSingleton<IDistributedApplicationLifecycleHook, T>(implementationFactory);
    }

    /// <summary>
    /// Attempts to add a distributed application lifecycle hook to the service collection.
    /// </summary>
    /// <typeparam name="T">The type of the distributed application lifecycle hook.</typeparam>
    /// <param name="services">The service collection to add the hook to.</param>
    /// <param name="implementationFactory">A factory function that creates the hook implementation.</param>
    [Obsolete("Use EventingSubscriberServiceCollectionExtensions.TryAddEventingSubscriber instead.")]
    [AspireExportIgnore(Reason = "IServiceCollection is not exported to ATS, and IServiceProvider factory registration is not ATS-compatible. Use IDistributedApplicationBuilder.tryAddEventingSubscriber instead.")]
    public static void TryAddLifecycleHook<T>(this IServiceCollection services, Func<IServiceProvider, T> implementationFactory) where T : class, IDistributedApplicationLifecycleHook
    {
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IDistributedApplicationLifecycleHook, T>(implementationFactory));
    }
}
