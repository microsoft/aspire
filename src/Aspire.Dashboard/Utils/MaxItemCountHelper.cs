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
        CreateGetter();

    private static readonly Action<Virtualize<TItem>, int>? s_setMaxItemCount =
        CreateSetter();

    private static Func<Virtualize<TItem>, int>? CreateGetter()
    {
        var type = typeof(Virtualize<TItem>);
        var prop = type.GetProperty("MaxItemCount", BindingFlags.Instance | BindingFlags.Public);

        if (prop == null || !prop.CanRead)
        {
            return null;
        }

        var instance = Expression.Parameter(type, "virtualize");
        var body = Expression.Property(instance, prop);

        return Expression.Lambda<Func<Virtualize<TItem>, int>>(body, instance).Compile();
    }

    private static Action<Virtualize<TItem>, int>? CreateSetter()
    {
        var type = typeof(Virtualize<TItem>);
        var prop = type.GetProperty("MaxItemCount", BindingFlags.Instance | BindingFlags.Public);

        if (prop == null || !prop.CanWrite)
        {
            return null;
        }

        var instance = Expression.Parameter(type, "virtualize");
        var valueParam = Expression.Parameter(typeof(int), "value");
        var body = Expression.Assign(Expression.Property(instance, prop), valueParam);

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

    private static readonly Func<Virtualize<TItem>, object>? s_getAnchorMode =
        CreateAnchorModeGetter();

    private static readonly Action<Virtualize<TItem>>? s_setAnchorModeEnd =
        CreateAnchorModeSetter();

    private static readonly object? s_anchorModeEndValue =
        GetAnchorModeEndValue();

    private static object? GetAnchorModeEndValue()
    {
        var type = typeof(Virtualize<TItem>);
        var prop = type.GetProperty("AnchorMode", BindingFlags.Instance | BindingFlags.Public);

        if (prop == null)
        {
            return null;
        }

        return Enum.Parse(prop.PropertyType, "End");
    }

    private static Func<Virtualize<TItem>, object>? CreateAnchorModeGetter()
    {
        var type = typeof(Virtualize<TItem>);
        var prop = type.GetProperty("AnchorMode", BindingFlags.Instance | BindingFlags.Public);

        if (prop == null || !prop.CanRead)
        {
            return null;
        }

        var instance = Expression.Parameter(type, "virtualize");
        var body = Expression.Convert(Expression.Property(instance, prop), typeof(object));

        return Expression.Lambda<Func<Virtualize<TItem>, object>>(body, instance).Compile();
    }

    private static Action<Virtualize<TItem>>? CreateAnchorModeSetter()
    {
        var type = typeof(Virtualize<TItem>);
        var prop = type.GetProperty("AnchorMode", BindingFlags.Instance | BindingFlags.Public);

        if (prop == null || !prop.CanWrite)
        {
            return null;
        }

        var anchorModeType = prop.PropertyType;
        var endValue = Enum.Parse(anchorModeType, "End");

        var instance = Expression.Parameter(type, "virtualize");
        var body = Expression.Assign(
            Expression.Property(instance, prop),
            Expression.Constant(endValue, anchorModeType));

        return Expression.Lambda<Action<Virtualize<TItem>>>(body, instance).Compile();
    }

    public static bool TrySetAnchorModeEnd(Virtualize<TItem> virtualize)
    {
        if (s_getAnchorMode == null || s_setAnchorModeEnd == null || s_anchorModeEndValue == null)
        {
            return false;
        }

        if (s_getAnchorMode(virtualize).Equals(s_anchorModeEndValue))
        {
            return false;
        }

        s_setAnchorModeEnd(virtualize);
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
