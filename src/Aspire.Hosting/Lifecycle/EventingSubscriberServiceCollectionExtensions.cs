// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Aspire.Hosting.Lifecycle;

/// <summary>
/// Provides extension methods for adding event subscribers to the <see cref="IServiceCollection"/>.
/// </summary>
public static class EventingSubscriberServiceCollectionExtensions
{
    /// <summary>
    /// Adds a singleton event subscriber of type <typeparamref name="T"/> to the service collection.
    /// </summary>
    /// <typeparam name="T">A service that implements <see cref="IDistributedApplicationEventingSubscriber"/></typeparam>
    /// <param name="services">The <see cref="IServiceCollection"/> to add the event subscriber to.</param>
    [AspireExportIgnore(Reason = "IServiceCollection is not exported to ATS, and generic service registration is not ATS-compatible. Use IDistributedApplicationBuilder.addEventingSubscriber instead.")]
    public static void AddEventingSubscriber<T>(this IServiceCollection services) where T : class, IDistributedApplicationEventingSubscriber
    {
        services.AddSingleton<IDistributedApplicationEventingSubscriber, T>();
    }

    /// <summary>
    /// Attempts to add a singleton event subscriber of type <typeparamref name="T"/> to the service collection.
    /// </summary>
    /// <typeparam name="T">A service that implements <see cref="IDistributedApplicationEventingSubscriber"/></typeparam>
    /// <param name="services">The <see cref="IServiceCollection"/> to add the event subscriber to.</param>
    [AspireExportIgnore(Reason = "IServiceCollection is not exported to ATS, and generic service registration is not ATS-compatible. Use IDistributedApplicationBuilder.tryAddEventingSubscriber instead.")]
    public static void TryAddEventingSubscriber<T>(this IServiceCollection services) where T : class, IDistributedApplicationEventingSubscriber
    {
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IDistributedApplicationEventingSubscriber, T>());
    }
}
