// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Components;
using Xunit;

namespace Aspire.Dashboard.Tests;

public class EndAnchorItemsProviderStateTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public void GetInitialResult_SmallInitialLoad_DoesNotLimitLaterAppend(int initialCount)
    {
        var state = new EndAnchorItemsProviderState();
        Assert.Null(state.GetInitialResult(0, Array.Empty<int>(), 0));
        Assert.False(state.TryBeginRefresh());

        Assert.Null(state.GetInitialResult(0, Enumerable.Range(1, initialCount), initialCount));
        Assert.False(state.TryBeginRefresh());

        Assert.Null(state.GetInitialResult(0, Enumerable.Range(1, 386), 386));
        Assert.False(state.TryBeginRefresh());
    }

    [Theory]
    [InlineData(6)]
    [InlineData(386)]
    public void GetInitialResult_LargeInitialLoad_KeepsWorkaroundAndRefreshesOnce(int initialCount)
    {
        var state = new EndAnchorItemsProviderState();
        Assert.Null(state.GetInitialResult(0, Array.Empty<int>(), 0));
        Assert.False(state.TryBeginRefresh());

        var result = state.GetInitialResult(0, Enumerable.Range(1, initialCount), initialCount);
        Assert.True(result.HasValue);
        Assert.Equal(5, result.Value.TotalItemCount);
        Assert.Equal([1, 2, 3, 4, 5], result.Value.Items);
        Assert.True(state.TryBeginRefresh());
        Assert.False(state.TryBeginRefresh());

        Assert.Null(state.GetInitialResult(0, Enumerable.Range(1, initialCount), initialCount));
        Assert.False(state.TryBeginRefresh());
    }

    [Fact]
    public void GetInitialResult_NonzeroStartIndex_DoesNotConsumeInitialLoad()
    {
        var state = new EndAnchorItemsProviderState();
        Assert.Null(state.GetInitialResult(1, [2], 2));

        var result = state.GetInitialResult(0, Enumerable.Range(1, 386), 386);
        Assert.True(result.HasValue);
        Assert.Equal(5, result.Value.TotalItemCount);
        Assert.True(state.TryBeginRefresh());
    }

    [Fact]
    public void GetInitialResult_ClearedAfterSmallInitialLoad_DoesNotRearmWorkaround()
    {
        var state = new EndAnchorItemsProviderState();
        Assert.Null(state.GetInitialResult(0, [1], 1));
        Assert.Null(state.GetInitialResult(0, Array.Empty<int>(), 0));

        Assert.Null(state.GetInitialResult(0, Enumerable.Range(1, 386), 386));
        Assert.False(state.TryBeginRefresh());
    }
}
