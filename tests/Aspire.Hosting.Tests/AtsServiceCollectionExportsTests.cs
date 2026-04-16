// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Linq.Expressions;
using System.Reflection;
using Aspire.Hosting.Lifecycle;
using Microsoft.Extensions.DependencyInjection;

namespace Aspire.Hosting.Tests;

[Trait("Partition", "5")]
public class AtsServiceCollectionExportsTests
{
    [Fact]
    public void TryAddEventingSubscriber_AllowsDistinctCallbacks()
    {
        var services = new ServiceCollection();
        var method = typeof(DistributedApplication).Assembly
            .GetType("Aspire.Hosting.Ats.ServiceCollectionExports", throwOnError: true)!
            .GetMethod("TryAddEventingSubscriber", BindingFlags.Public | BindingFlags.Static)!;

        var firstSubscriber = CreateCallback(method.GetParameters()[1].ParameterType);
        var secondSubscriber = CreateCallback(method.GetParameters()[1].ParameterType);

        method.Invoke(null, [services, firstSubscriber]);
        method.Invoke(null, [services, firstSubscriber]);
        method.Invoke(null, [services, secondSubscriber]);

        var subscribers = services
            .Where(descriptor => descriptor.ServiceType == typeof(IDistributedApplicationEventingSubscriber))
            .Select(descriptor => descriptor.ImplementationInstance)
            .ToList();

        Assert.Equal(2, subscribers.Count);
    }

    private static Delegate CreateCallback(Type delegateType)
    {
        var contextType = delegateType.GenericTypeArguments[0];
        var contextParameter = Expression.Parameter(contextType, "context");
        var completedTask = Expression.Property(null, typeof(Task).GetProperty(nameof(Task.CompletedTask))!);

        return Expression.Lambda(delegateType, completedTask, contextParameter).Compile();
    }
}
