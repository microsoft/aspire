// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Linq.Expressions;
using System.Reflection;
using Microsoft.AspNetCore.Components.Web.Virtualization;
using Microsoft.FluentUI.AspNetCore.Components;

namespace Aspire.Dashboard.Utils;

// Temporary work around to set MaxItemCount on Virtualize component via reflection.
// Required because dashboard currently targets .NET 8 and MaxItemCount isn't available.
//
// ASP.NET Core issue: https://github.com/dotnet/aspnetcore/issues/63651
// Note that this work around should be left in place for a while after the issue is fixed in ASP.NET Core.
// .NET 9 needs to be patched, and users may have unpatched versions of .NET 9 on their machines for a while.
public static class VirtualizeHelper<TItem>
{
    private static readonly Func<Virtualize<TItem>, int>? s_getMaxItemCount =
        CreateIntGetter("MaxItemCount");

    private static readonly Action<Virtualize<TItem>, int>? s_setMaxItemCount =
        CreateIntSetter("MaxItemCount");

    private static readonly Func<Virtualize<TItem>, int>? s_getAnchorMode =
        CreateEnumAsIntGetter("AnchorMode");

    private static readonly Action<Virtualize<TItem>, int>? s_setAnchorMode =
        CreateEnumAsIntSetter("AnchorMode");

    private static Func<Virtualize<TItem>, int>? CreateIntGetter(string propertyName)
    {
        var type = typeof(Virtualize<TItem>);
        var prop = type.GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public);

        if (prop == null || !prop.CanRead)
        {
            return null;
        }

        var instance = Expression.Parameter(type, "virtualize");
        var body = Expression.Property(instance, prop);

        return Expression.Lambda<Func<Virtualize<TItem>, int>>(body, instance).Compile();
    }

    private static Action<Virtualize<TItem>, int>? CreateIntSetter(string propertyName)
    {
        var type = typeof(Virtualize<TItem>);
        var prop = type.GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public);

        if (prop == null || !prop.CanWrite)
        {
            return null;
        }

        var instance = Expression.Parameter(type, "virtualize");
        var valueParam = Expression.Parameter(typeof(int), "value");
        var body = Expression.Assign(Expression.Property(instance, prop), valueParam);

        return Expression.Lambda<Action<Virtualize<TItem>, int>>(body, instance, valueParam).Compile();
    }

    private static Func<Virtualize<TItem>, int>? CreateEnumAsIntGetter(string propertyName)
    {
        var type = typeof(Virtualize<TItem>);
        var prop = type.GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public);

        if (prop == null || !prop.CanRead)
        {
            return null;
        }

        var instance = Expression.Parameter(type, "virtualize");
        var body = Expression.Convert(Expression.Property(instance, prop), typeof(int));

        return Expression.Lambda<Func<Virtualize<TItem>, int>>(body, instance).Compile();
    }

    private static Action<Virtualize<TItem>, int>? CreateEnumAsIntSetter(string propertyName)
    {
        var type = typeof(Virtualize<TItem>);
        var prop = type.GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public);

        if (prop == null || !prop.CanWrite)
        {
            return null;
        }

        var instance = Expression.Parameter(type, "virtualize");
        var valueParam = Expression.Parameter(typeof(int), "value");
        var body = Expression.Assign(
            Expression.Property(instance, prop),
            Expression.Convert(valueParam, prop.PropertyType));

        return Expression.Lambda<Action<Virtualize<TItem>, int>>(body, instance, valueParam).Compile();
    }

    public static bool TrySetMaxItemCount(Virtualize<TItem> virtualize, int max)
    {
        if (s_getMaxItemCount == null || s_setMaxItemCount == null)
        {
            return false;
        }

        if (s_getMaxItemCount(virtualize) == max)
        {
            return false;
        }

        s_setMaxItemCount(virtualize, max);
        return true;
    }

    /// <summary>
    /// Sets AnchorMode to End on the Virtualize component via reflection.
    /// VirtualizeAnchorMode.End pins the viewport to the bottom so new items at the end auto-scroll into view.
    /// </summary>
    public static bool TrySetAnchorModeEnd(Virtualize<TItem> virtualize)
    {
        // VirtualizeAnchorMode.End = 2
        const int end = 2;

        if (s_getAnchorMode == null || s_setAnchorMode == null)
        {
            return false;
        }

        if (s_getAnchorMode(virtualize) == end)
        {
            return false;
        }

        s_setAnchorMode(virtualize, end);
        return true;
    }
}

public static class FluentDataGridHelper<TGridItem>
{
    private static readonly Func<FluentDataGrid<TGridItem>, Virtualize<(int, TGridItem)>>? s_getVirtualize =
        CreateGetter();

    private static Func<FluentDataGrid<TGridItem>, Virtualize<(int, TGridItem)>>? CreateGetter()
    {
        var type = typeof(FluentDataGrid<TGridItem>);
        var field = type.GetField("_virtualizeComponent", BindingFlags.Instance | BindingFlags.NonPublic);

        if (field == null)
        {
            return null;
        }

        var instance = Expression.Parameter(type, "dataGrid");
        var body = Expression.Convert(Expression.Field(instance, field), typeof(Virtualize<(int, TGridItem)>));

        return Expression.Lambda<Func<FluentDataGrid<TGridItem>, Virtualize<(int, TGridItem)>>> (body, instance).Compile();
    }

    private static Virtualize<(int, TGridItem)>? GetVirtualize(FluentDataGrid<TGridItem> dataGrid)
        => s_getVirtualize?.Invoke(dataGrid);

    public static bool TrySetMaxItemCount(FluentDataGrid<TGridItem> dataGrid, int max)
    {
        var virtualize = GetVirtualize(dataGrid);
        if (virtualize == null)
        {
            return false;
        }

        return VirtualizeHelper<(int, TGridItem)>.TrySetMaxItemCount(virtualize, max);
    }

    public static bool TrySetAnchorModeEnd(FluentDataGrid<TGridItem> dataGrid)
    {
        var virtualize = GetVirtualize(dataGrid);
        if (virtualize == null)
        {
            return false;
        }

        return VirtualizeHelper<(int, TGridItem)>.TrySetAnchorModeEnd(virtualize);
    }
}
