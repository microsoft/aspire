// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Components;
using Microsoft.AspNetCore.Components.Web.Virtualization;
using Microsoft.FluentUI.AspNetCore.Components;

namespace Aspire.Dashboard.Extensions;

internal static class ComponentExtensions
{
    public static async Task SafeRefreshDataAsync<T>(this FluentDataGrid<T>? dataGrid)
    {
        if (dataGrid != null)
        {
            await dataGrid.RefreshDataAsync().ConfigureAwait(false);
        }
    }

    public static async Task SafeRefreshDataAsync<T>(this Virtualize<T>? virtualize)
    {
        if (virtualize != null)
        {
            await virtualize.RefreshDataAsync().ConfigureAwait(false);
        }
    }

    public static async Task SafeRefreshDataAsync(this LogViewer? logViewer)
    {
        if (logViewer != null)
        {
            await logViewer.RefreshDataAsync().ConfigureAwait(false);
        }
    }
}
