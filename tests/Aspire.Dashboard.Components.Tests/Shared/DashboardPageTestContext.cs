// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections;
using Bunit;
using Microsoft.AspNetCore.Components.Web.Virtualization;
using Microsoft.JSInterop;
using System.Reflection;

namespace Aspire.Dashboard.Components.Tests.Shared;
public abstract class DashboardTestContext : TestContext
{
    private const string VirtualizeJsFunctionsPrefix = "Blazor._internal.Virtualize.";

    private static readonly Lazy<(PropertyInfo ValueProperty, MethodInfo Callback)> s_virtualizeReflection = new(() =>
    {
        var virtualizeJsInteropType = typeof(Virtualize<>).Assembly
            .GetType("Microsoft.AspNetCore.Components.Web.Virtualization.VirtualizeJsInterop")!;
        var dotNetObjectReferenceType = typeof(DotNetObjectReference<>).MakeGenericType(virtualizeJsInteropType);

        return (
            dotNetObjectReferenceType.GetProperty("Value", BindingFlags.Public | BindingFlags.Instance)!,
            virtualizeJsInteropType.GetMethod("OnSpacerBeforeVisible")!);
    });

    public DashboardTestContext()
    {
        // Increase from default 1 second as Helix/GitHub Actions can be slow.
        DefaultWaitTimeout = TimeSpan.FromSeconds(10);

        // bUnit 1.x invokes the three-parameter Virtualize callback used through .NET 10. .NET 11
        // adds a visibility-reason parameter. Intercept initialization until bUnit ships support
        // for the new signature.
        RemoveBunitVirtualizeHandler();
        JSInterop.SetupVoid(
            invocation => invocation.Identifier.StartsWith(VirtualizeJsFunctionsPrefix, StringComparison.Ordinal))
            .SetVoidResult();
        JSInterop.SetupVoid($"{VirtualizeJsFunctionsPrefix}init", arguments =>
        {
            var (valueProperty, callback) = s_virtualizeReflection.Value;
            var virtualizeJsInterop = valueProperty.GetValue(arguments.Arguments[0]!);
            callback.Invoke(virtualizeJsInterop, [0f, 0f, 1_000_000_000f, 0]);
            return true;
        }).SetVoidResult();
        JSInterop.Setup<bool>($"{VirtualizeJsFunctionsPrefix}isFollowingBottom", _ => true).SetResult(false);
    }

    private void RemoveBunitVirtualizeHandler()
    {
        var handlersField = typeof(BunitJSInterop).GetField("handlers", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var handlers = (IDictionary)handlersField.GetValue(JSInterop)!;
        foreach (IList handlerList in handlers.Values)
        {
            for (var i = handlerList.Count - 1; i >= 0; i--)
            {
                if (handlerList[i]!.GetType().Name == "VirtualizeJSRuntimeInvocationHandler")
                {
                    handlerList.RemoveAt(i);
                }
            }
        }
    }
}
