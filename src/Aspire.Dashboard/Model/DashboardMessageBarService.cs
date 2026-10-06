// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Components.Controls;
using Microsoft.FluentUI.AspNetCore.Components;

namespace Aspire.Dashboard.Model;

public sealed class DashboardMessageBarContent
{
    public string? Title { get; init; }

    public required string Message { get; init; }

    public bool UseMarkupString { get; init; }

    public bool AllowDismiss { get; init; } = true;

    public string? LinkText { get; init; }

    public string? LinkUrl { get; init; }

    public string? PrimaryAction { get; init; }

    public string? SecondaryAction { get; init; }
}

public sealed class DashboardMessageBarReference(
    IMessageBarInstance instance,
    Task<MessageBarResult> result,
    Microsoft.FluentUI.AspNetCore.Components.INotificationService notificationService)
{
    public IMessageBarInstance Instance => instance;

    public Task<MessageBarResult> Result => result;

    public Task CloseAsync(object? data = null)
    {
        return notificationService.CloseAsync(instance, data);
    }
}

public sealed class DashboardMessageBarService(Microsoft.FluentUI.AspNetCore.Components.INotificationService notificationService)
{
    public async Task<DashboardMessageBarReference> ShowAsync(
        DashboardMessageBarContent content,
        MessageBarIntent intent,
        string section,
        Func<MessageBarResult, Task>? onClose = null)
    {
        var opened = new TaskCompletionSource<IMessageBarInstance>(TaskCreationOptions.RunContinuationsAsynchronously);
        var options = new MessageBarOptions
        {
            Intent = intent,
            Section = section,
            AllowDismiss = content.AllowDismiss,
            ResultTiming = MessageBarResultTiming.Closed,
            OnStatusChange = args =>
            {
                if (args.Status == MessageBarLifecycleStatus.Visible && args.Instance is { } instance)
                {
                    opened.TrySetResult(instance);
                }
            }
        };
        options.Parameters[nameof(DashboardMessageBar.Content)] = content;

        var resultTask = notificationService.ShowMessageBarAsync<DashboardMessageBar>(options);
        var instance = await WaitForVisibleAsync(opened.Task, resultTask).ConfigureAwait(false);

        var reference = new DashboardMessageBarReference(instance, resultTask, notificationService);
        if (onClose is not null)
        {
            _ = InvokeOnCloseAsync(resultTask, onClose);
        }

        return reference;
    }

    internal static async Task<IMessageBarInstance> WaitForVisibleAsync(
        Task<IMessageBarInstance> openedTask,
        Task<MessageBarResult> resultTask)
    {
        var firstCompleted = await Task.WhenAny(openedTask, resultTask).ConfigureAwait(false);
        if (firstCompleted == openedTask)
        {
            return await openedTask.ConfigureAwait(false);
        }

        await resultTask.ConfigureAwait(false);
        throw new InvalidOperationException("The message bar closed before becoming visible.");
    }

    private static async Task InvokeOnCloseAsync(Task<MessageBarResult> resultTask, Func<MessageBarResult, Task> onClose)
    {
        var result = await resultTask.ConfigureAwait(false);
        await onClose(result).ConfigureAwait(false);
    }
}
