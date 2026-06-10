// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.FluentUI.AspNetCore.Components;

namespace Aspire.Dashboard.Model;

/// <summary>
/// Extension methods on IToastService to provide backward compatibility with v4 API patterns.
/// The v4 API had synchronous methods and an OnClose event; v5 uses async methods exclusively.
/// </summary>
internal static class ToastServiceExtensions
{
    /// <summary>
    /// Compatibility shim for the removed FluentUI v4 ShowCommunicationToast method.
    /// In v5, all toast methods are async and use ToastOptions directly.
    /// </summary>
    public static void ShowCommunicationToast<TContent>(this IToastService toastService, ToastParameters<TContent> parameters)
    {
        _ = toastService.ShowToastAsync(options =>
        {
            options.Id = parameters.Id;
            options.Intent = parameters.Intent;
            options.Title = parameters.Title;
            options.Type = ToastType.Communication;
            if (parameters.Timeout is { } timeout)
            {
                options.Timeout = timeout;
            }
        });
    }

    /// <summary>
    /// Compatibility shim for the removed FluentUI v4 CloseToast method.
    /// </summary>
    public static void CloseToast(this IToastService toastService, string? id)
    {
        if (id is not null)
        {
            _ = toastService.DismissAsync(id);
        }
    }

    /// <summary>
    /// Compatibility shim for the removed FluentUI v4 UpdateToast method.
    /// </summary>
    public static void UpdateToast<TContent>(this IToastService toastService, string? id, ToastParameters<TContent> parameters)
    {
        // In v5, UpdateToastAsync requires an IToastInstance which we don't have from the v4 pattern.
        // The closest approach is to dismiss and re-show.
        if (id is not null)
        {
            _ = toastService.DismissAsync(id);
        }
        ShowCommunicationToast(toastService, parameters);
    }
}
