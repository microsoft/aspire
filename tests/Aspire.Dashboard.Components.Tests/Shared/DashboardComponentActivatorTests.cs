// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Components;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.FluentUI.AspNetCore.Components;
using Xunit;

namespace Aspire.Dashboard.Components.Tests.Shared;

public class DashboardComponentActivatorTests
{
    [Fact]
    public void CreateInstance_FluentKeyCode_MaterializesModifierIgnoreSet()
    {
        using var services = new ServiceCollection().BuildServiceProvider();
        var activator = new DashboardComponentActivator(services);

        var keyCode = Assert.IsType<DashboardFluentKeyCode>(activator.CreateInstance(typeof(FluentKeyCode)));
        keyCode.NormalizeParameters();

        Assert.False(keyCode.IgnoreModifier);
        Assert.Equal([KeyCode.Shift, KeyCode.Alt, KeyCode.Ctrl, KeyCode.Meta], keyCode.Ignore);
    }

    [Fact]
    public void NormalizeParameters_ExplicitModifierBehavior_IsPreserved()
    {
        var keyCode = new DashboardFluentKeyCode();
        ParameterView.FromDictionary(new Dictionary<string, object?>
        {
            [nameof(FluentKeyCode.IgnoreModifier)] = false,
            [nameof(FluentKeyCode.Ignore)] = new[] { KeyCode.Escape }
        }).SetParameterProperties(keyCode);

        keyCode.NormalizeParameters();

        Assert.False(keyCode.IgnoreModifier);
        Assert.Equal([KeyCode.Escape], keyCode.Ignore);
    }
}
