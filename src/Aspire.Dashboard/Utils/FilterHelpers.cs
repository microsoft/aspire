// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Components.Dialogs;
using Aspire.Dashboard.Model;
using Aspire.Dashboard.Model.Otlp;
using Aspire.Dashboard.Resources;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Localization;
using Microsoft.FluentUI.AspNetCore.Components;
using Icons = Microsoft.FluentUI.AspNetCore.Components.Icons;

namespace Aspire.Dashboard.Utils;

public static class FilterHelpers
{
    public static IEnumerable<TelemetryFilter> GetEnabledFilters(this IEnumerable<TelemetryFilter> filters)
    {
        return filters.Where(filter => filter.Enabled);
    }

    public static List<MenuButtonItem> GetFilterMenuItems(
        IReadOnlyList<FieldTelemetryFilter> filters,
        Action clearFilters,
        Func<FieldTelemetryFilter?, Task> openFilterAsync,
        Func<Task> afterChangeAsync,
        IStringLocalizer<StructuredFiltering> filterLoc,
        IStringLocalizer<Dialogs> dialogsLoc)
    {
        var filterMenuItems = new List<MenuButtonItem>();

        foreach (var filter in filters)
        {
            filterMenuItems.Add(new MenuButtonItem
            {
                OnClick = () => openFilterAsync(filter),
                Text = filter.GetDisplayText(filterLoc),
                Icon = filter.Enabled ? new Icons.Regular.Size16.Play() : new Icons.Regular.Size16.Pause(),
                Class = "filter-menu-item",
            });
        }

        filterMenuItems.Add(new MenuButtonItem
        {
            IsDivider = true
        });

        if (filters.GetEnabledFilters().Any())
        {
            filterMenuItems.Add(new MenuButtonItem
            {
                Text = dialogsLoc[nameof(Dialogs.FilterDialogDisableAll)],
                Icon = new Icons.Regular.Size16.Pause(),
                OnClick = async () =>
                {
                    foreach (var filter in filters)
                    {
                        filter.Enabled = false;
                    }

                    await afterChangeAsync().ConfigureAwait(true);
                }
            });
        }
        else
        {
            filterMenuItems.Add(new MenuButtonItem
            {
                Text = dialogsLoc[nameof(Dialogs.FilterDialogEnableAll)],
                Icon = new Icons.Regular.Size16.Play(),
                OnClick = async () =>
                {
                    foreach (var filter in filters)
                    {
                        filter.Enabled = true;
                    }

                    await afterChangeAsync().ConfigureAwait(true);
                }
            });
        }

        filterMenuItems.Add(new MenuButtonItem
        {
            Text = dialogsLoc[nameof(Dialogs.SettingsRemoveAllButtonText)],
            Icon = new Icons.Regular.Size16.Delete(),
            OnClick = async () =>
            {
                clearFilters();
                await afterChangeAsync().ConfigureAwait(true);
            }
        });

        return filterMenuItems;
    }

    public static async Task OpenFilterAsync(
        FieldTelemetryFilter? entry,
        DashboardDialogService dialogService,
        EventCallback<DialogResult> onDialogResult,
        List<string> propertyKeys,
        List<string> knownKeys,
        Func<string, Dictionary<string, int>> getFieldValues,
        IStringLocalizer<StructuredFiltering> filterLoc)
    {
        var title = entry is not null ? filterLoc[nameof(StructuredFiltering.DialogTitleEditFilter)] : filterLoc[nameof(StructuredFiltering.DialogTitleAddFilter)];
        var parameters = new DialogParameters
        {
            OnDialogResult = onDialogResult,
            Title = title,
            Alignment = HorizontalAlignment.Right,
            PrimaryAction = null,
            SecondaryAction = null,
            Width = dialogService.IsDesktop ? "450px" : "100%"
        };
        var data = new FilterDialogViewModel
        {
            Filter = entry,
            PropertyKeys = propertyKeys,
            KnownKeys = knownKeys,
            GetFieldValues = getFieldValues
        };
        await dialogService.ShowPanelAsync<FilterDialog>(data, parameters).ConfigureAwait(false);
    }
}
