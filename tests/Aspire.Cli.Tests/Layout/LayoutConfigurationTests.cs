// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Layout;

namespace Aspire.Cli.Tests.LayoutTests;

public class LayoutConfigurationTests
{
    [Fact]
    public void LayoutComponentValuesRemainStable()
    {
        Assert.Equal(0, (int)LayoutComponent.Cli);
        Assert.Equal(1, (int)LayoutComponent.Dcp);
        Assert.Equal(2, (int)LayoutComponent.Managed);
        Assert.Equal(3, (int)LayoutComponent.Dashboard);
    }
}
