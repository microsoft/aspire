// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using Microsoft.FluentUI.AspNetCore.Components;

namespace Aspire.Dashboard.Model;

/// <summary>
/// Tracks open Fluent dialogs, drawers, and confirmations so the dashboard dialog provider
/// can cancel them on navigation. Fluent UI v5 does not expose its dialog collection or a dismiss-all API.
/// </summary>
public sealed class NavigationDialogService(IServiceProvider serviceProvider, IFluentLocalizer localizer)
    : DialogService(serviceProvider, localizer)
{
    private readonly ConcurrentDictionary<string, IDialogInstance> _dialogs = new();

    public override async Task<DialogResult> ShowDialogAsync(
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] Type componentType,
        DialogOptions options)
    {
        var onStateChange = options.OnStateChange;
        IDialogInstance? instance = null;
        options.OnStateChange = args =>
        {
            if (args.Instance is { } dialog)
            {
                instance = dialog;
                if (args.State is DialogState.Opening or DialogState.Open)
                {
                    _dialogs.TryAdd(dialog.Id, dialog);
                }
                else
                {
                    _dialogs.TryRemove(dialog.Id, out _);
                }
            }

            onStateChange?.Invoke(args);
        };

        try
        {
            return await base.ShowDialogAsync(componentType, options).ConfigureAwait(true);
        }
        finally
        {
            if (instance is not null)
            {
                _dialogs.TryRemove(instance.Id, out _);
            }

            options.OnStateChange = onStateChange;
        }
    }

    public async Task DismissAllAsync()
    {
        // Closing callbacks can navigate again or open new dialogs. Only dismiss the
        // snapshot belonging to this navigation, claiming each instance before awaiting.
        foreach (var dialog in _dialogs.ToArray().Select(pair => pair.Value).OrderByDescending(dialog => dialog.Index))
        {
            if (_dialogs.TryRemove(dialog.Id, out _))
            {
                await dialog.CloseAsync(DialogResult.Cancel()).ConfigureAwait(true);
            }
        }
    }
}
