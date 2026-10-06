// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Dashboard.Components;

/// <summary>
/// Coordinates the limited initial result and follow-up refresh required by end-anchored virtualized lists.
/// </summary>
internal sealed class EndAnchorItemsProviderState
{
    private const int InitialItemCount = 5;

    private bool _hasReturnedInitialResult;
    private bool _refreshDataAfterRender;

    public (T[] Items, int TotalItemCount)? GetInitialResult<T>(int startIndex, IEnumerable<T> items, int totalItemCount)
    {
        if (_hasReturnedInitialResult || startIndex != 0 || totalItemCount <= InitialItemCount)
        {
            return null;
        }

        var initialItems = items.Take(InitialItemCount).ToArray();
        if (initialItems.Length == 0)
        {
            return null;
        }

        _hasReturnedInitialResult = true;
        _refreshDataAfterRender = true;

        // Virtualize doesn't treat its initial 0 -> N result as an append. Report a small total
        // until actual rows render so the following refresh can establish the End anchor.
        // Remove this workaround once https://github.com/dotnet/aspnetcore/issues/69302 is fixed in Virtualize.
        return (initialItems, InitialItemCount);
    }

    public bool TryBeginRefresh()
    {
        if (!_refreshDataAfterRender)
        {
            return false;
        }

        _refreshDataAfterRender = false;
        return true;
    }
}