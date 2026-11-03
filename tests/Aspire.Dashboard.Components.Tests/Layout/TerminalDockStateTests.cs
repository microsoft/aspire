// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Components.Layout;
using Xunit;

namespace Aspire.Dashboard.Components.Tests.Layout;

public sealed class TerminalDockStateTests
{
    [Fact]
    public void Update_TracksVisibleResourceAndRaisesOnlyForChanges()
    {
        var state = new TerminalDockState();
        var changeCount = 0;
        state.Changed += () => changeCount++;

        state.Update(isVisible: true, resourceName: "frontend");
        state.Update(isVisible: true, resourceName: "frontend");

        Assert.True(state.IsResourceOpen("frontend"));
        Assert.False(state.IsResourceOpen("worker"));
        Assert.Equal(1, changeCount);

        state.Update(isVisible: false, resourceName: null);

        Assert.False(state.IsResourceOpen("frontend"));
        Assert.Equal(2, changeCount);
    }
}
