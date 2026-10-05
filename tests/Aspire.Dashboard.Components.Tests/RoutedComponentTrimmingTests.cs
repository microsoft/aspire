// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using Microsoft.AspNetCore.Components;
using Xunit;

namespace Aspire.Dashboard.Components.Tests;

public class RoutedComponentTrimmingTests
{
    [Fact]
    public void RoutedComponents_PreserveRuntimeActivatedMembers()
    {
        const DynamicallyAccessedMemberTypes requiredMembers =
            DynamicallyAccessedMemberTypes.PublicConstructors |
            DynamicallyAccessedMemberTypes.PublicProperties |
            DynamicallyAccessedMemberTypes.NonPublicProperties;

        var preservedTypes = typeof(DashboardWebApplication).GetConstructors()
            .SelectMany(constructor => constructor.GetCustomAttributes<DynamicDependencyAttribute>())
            .Where(dependency => (dependency.MemberTypes & requiredMembers) == requiredMembers)
            .Select(dependency => dependency.Type)
            .ToHashSet();
        var routedComponents = typeof(DashboardWebApplication).Assembly.GetTypes()
            .Where(type => type.IsDefined(typeof(RouteAttribute), inherit: false))
            .ToList();

        Assert.NotEmpty(routedComponents);
        Assert.All(routedComponents, component => Assert.Contains(component, preservedTypes));
    }
}
