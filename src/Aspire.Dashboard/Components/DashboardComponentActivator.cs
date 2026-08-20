// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Reflection.Metadata;
using Microsoft.AspNetCore.Components;
using Microsoft.FluentUI.AspNetCore.Components;

[assembly: MetadataUpdateHandler(typeof(Aspire.Dashboard.Components.DashboardComponentActivator))]

namespace Aspire.Dashboard.Components;

internal sealed class DashboardComponentActivator(IServiceProvider serviceProvider) : IComponentActivator
{
    private static readonly ConcurrentDictionary<Type, ObjectFactory> s_factories = new();

    public static void ClearCache(Type[]? _) => s_factories.Clear();

    public IComponent CreateInstance(
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] Type componentType)
    {
        if (!typeof(IComponent).IsAssignableFrom(componentType))
        {
            throw new ArgumentException($"The type {componentType.FullName} does not implement {nameof(IComponent)}.", nameof(componentType));
        }

        var implementationType = componentType == typeof(FluentKeyCode)
            ? typeof(DashboardFluentKeyCode)
            : componentType;
        if (!s_factories.TryGetValue(implementationType, out var factory))
        {
            factory = ActivatorUtilities.CreateFactory(implementationType, Type.EmptyTypes);
            s_factories.TryAdd(implementationType, factory);
        }

        return (IComponent)factory(serviceProvider, []);
    }
}

internal sealed class DashboardFluentKeyCode(LibraryConfiguration configuration) : FluentKeyCode(configuration)
{
    private static readonly KeyCode[] s_modifiers = [KeyCode.Shift, KeyCode.Alt, KeyCode.Ctrl, KeyCode.Meta];

    public override Task SetParametersAsync(ParameterView parameters)
    {
        parameters.SetParameterProperties(this);
        NormalizeParameters();

        // Parameters are already applied so the base call only needs to run component lifecycle methods.
        return base.SetParametersAsync(ParameterView.Empty);
    }

    internal void NormalizeParameters()
    {
        if (IgnoreModifier)
        {
            // FluentKeyCode passes Ignore.Union(modifiers) as an object-valued JS argument. That
            // produces an internal LINQ iterator that System.Text.Json source generation cannot name.
            // Materialize the equivalent ignore set after parameter binding so JS interop uses KeyCode[].
            // https://github.com/microsoft/fluentui-blazor/blob/e4d168193e5d5f334588d977a73fa8159ea8d3b2/src/Core/Components/KeyCode/FluentKeyCode.razor.cs#L121
            Ignore = Ignore.Concat(s_modifiers).Distinct().ToArray();
            IgnoreModifier = false;
        }
    }
}
