// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using Aspire.Dashboard.Otlp.Model;
using Aspire.Dashboard.Resources;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Localization;
using Microsoft.FluentUI.AspNetCore.Components;
using Icons = Microsoft.FluentUI.AspNetCore.Components.Icons;

namespace Aspire.Dashboard.Model;

public static class TraceLinkHelpers
{
    public static async Task<bool> WaitForSpanToBeAvailableAsync(
        string traceId,
        string spanId,
        Func<string, string, OtlpSpan?> getSpan,
        DashboardDialogService dialogService,
        Func<Func<Task>, Task> dispatcher,
        IStringLocalizer<Dialogs> loc,
        CancellationToken cancellationToken)
    {
        return await WaitForDataToBeAvailableAsync(
            (ct) => Task.FromResult(getSpan(traceId, spanId) != null),
            string.Format(CultureInfo.InvariantCulture, loc[nameof(Dialogs.OpenSpanDialogMessage)], OtlpHelpers.ToShortenedId(spanId)),
            dialogService,
            dispatcher,
            loc,
            cancellationToken).ConfigureAwait(false);
    }

    public static async Task<bool> WaitForDataToBeAvailableAsync(
        Func<CancellationToken, Task<bool>> isAvailableCallback,
        string unavailableText,
        DashboardDialogService dialogService,
        Func<Func<Task>, Task> dispatcher,
        IStringLocalizer<Dialogs> loc,
        CancellationToken cancellationToken)
    {
        var isAvailable = await isAvailableCallback(cancellationToken).ConfigureAwait(false);

        // Data isn't available yet. Display a dialog until the data is ready or the user cancels the dialog.
        if (!isAvailable)
        {
            using var cts = new CancellationTokenSource();
            using var registration = cancellationToken.Register(cts.Cancel);

            var reference = await dialogService.ShowMessageBoxAsync(new DialogParameters<MessageBoxContent>()
            {
                Content = new MessageBoxContent
                {
                    Intent = MessageBarIntent.Info,
                    Icon = new Icons.Filled.Size24.Info(),
                    IconColor = Color.Info,
                    MarkupMessage = new MarkupString(unavailableText),
                },
                DialogType = DialogType.MessageBox,
                PrimaryAction = string.Empty,
                SecondaryAction = loc[nameof(Dialogs.OpenSpanDialogCancelButtonText)]
            }).ConfigureAwait(false);

            // In v5, ShowMessageBoxAsync blocks until the dialog is closed by the user.
            // The dialog result indicates whether the user canceled.
            if (reference.Cancelled)
            {
                // Dialog was canceled before span was ready. Exit without navigating.
                return false;
            }
        }

        return true;
    }
}
