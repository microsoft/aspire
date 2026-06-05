// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Concurrent;
using System.Text;
using Aspire.Dashboard.Resources;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Localization;
using Microsoft.FluentUI.AspNetCore.Components;

namespace Aspire.Dashboard.Utils;

internal static class DashboardUIHelpers
{
    public const string MessageBarSection = "MessagesTop";

    // these are language names supported by highlight.js
    public const string XmlFormat = "xml";
    public const string JsonFormat = "json";
    public const string JavascriptFormat = "javascript";
    public const string PlaintextFormat = "plaintext";
    public const string MarkdownFormat = "markdown";
    public const string PropertiesFormat = "properties";

    // The initial data fetch for a FluentDataGrid doesn't include a count of items to return.
    // The data grid doesn't specify a count because it doesn't know how many items fit in the UI.
    // Once it knows the height of items and the height of the grid then it specifies the desired item count
    // and then virtualization will fetch more data as needed. The problem with this is the initial fetch
    // could fetch all available data when it doesn't need to.
    //
    // If there is no count then default to a limit to avoid getting all data.
    // Given the size of rows on dashboard grids, 100 rows should always fill the grid on the screen.
    public const int DefaultDataGridResultCount = 100;

    // Don't attempt to display more than 2 highlighted commands. Many commands will take up too much space.
    public const int MaxHighlightedCommands = 2;

    public static readonly TimeSpan ToastTimeout = TimeSpan.FromMilliseconds(5000);

    public static (ColumnResizeLabels resizeLabels, ColumnSortLabels sortLabels) CreateGridLabels(IStringLocalizer<ControlsStrings> loc)
    {
        var resizeLabels = ColumnResizeLabels.Default with
        {
            ExactLabel = loc[nameof(ControlsStrings.FluentDataGridHeaderCellResizeLabel)],
            ResizeMenu = loc[nameof(ControlsStrings.FluentDataGridHeaderCellResizeButtonText)],
            DiscreteLabel = loc[nameof(ControlsStrings.FluentDataGridHeaderCellResizeDiscreteLabel)],
            GrowAriaLabel = loc[nameof(ControlsStrings.FluentDataGridHeaderCellGrowAriaLabelText)],
            ResetAriaLabel = loc[nameof(ControlsStrings.FluentDataGridHeaderCellResetAriaLabelText)],
            ShrinkAriaLabel = loc[nameof(ControlsStrings.FluentDataGridHeaderCellShrinkAriaLabelText)],
            SubmitAriaLabel = loc[nameof(ControlsStrings.FluentDataGridHeaderCellSubmitAriaLabelText)]
        };
        var sortLabels = ColumnSortLabels.Default with
        {
            SortMenu = loc[nameof(ControlsStrings.FluentDataGridHeaderCellSortButtonText)],
            SortMenuAscendingLabel = loc[nameof(ControlsStrings.FluentDataGridHeaderCellSortAscendingButtonText)],
            SortMenuDescendingLabel = loc[nameof(ControlsStrings.FluentDataGridHeaderCellSortDescendingButtonText)]
        };
        return (resizeLabels, sortLabels);
    }

    private static readonly ConcurrentDictionary<int, TextMask> s_cachedMasking = new();

    public static TextMask GetMaskingText(int length)
    {
        return s_cachedMasking.GetOrAdd(length, static i =>
        {
            const string markupMaskingChar = "&#x25cf;";
            const string textMaskingChar = "●";

            return new TextMask(
                new MarkupString(Repeat(markupMaskingChar, i)),
                Repeat(textMaskingChar, i)
            );

            static string Repeat(string s, int n) => new StringBuilder(s.Length * n)
                .Insert(0, s, n)
                .ToString();
        });
    }

    public static async Task<Message> DisplayMaxLimitMessageAsync(IMessageService messageService, string title, string message, Action onClose)
    {
        return await messageService.ShowMessageBarAsync(options =>
        {
            options.Title = title;
            options.Body = message;
            options.Intent = MessageIntent.Info;
            options.Section = "MessagesTop";
            options.AllowDismiss = true;
            options.OnClose = m =>
            {
                onClose();
                return Task.CompletedTask;
            };
        }).ConfigureAwait(false);
    }

    public static string? ResolveTooltip(string value)
    {
        // FluentSelects in the dashboard are wide enough to display at least 30 characters.
        // Only display a tooltip if the value length is greater.
        const int TooltipLengthThreshold = 30;

        return value is { Length: > TooltipLengthThreshold } ? value : null;
    }

    /// <summary>
    /// Safely converts a duration to milliseconds as an integer.
    /// If the value exceeds int.MaxValue, returns int.MaxValue. A duration must be longer than 24 days to hit this limit. That should almost never happen and it's ok to truncate at this point.
    /// </summary>
    public static int SafeConvertToMilliseconds(TimeSpan duration)
    {
        var milliseconds = duration.TotalMilliseconds;
        
        if (milliseconds >= int.MaxValue)
        {
            return int.MaxValue;
        }
        
        if (milliseconds <= int.MinValue)
        {
            return int.MinValue;
        }
        
        return (int)milliseconds;
    }

    /// <summary>
    /// Masks querystring parameter values in a URL for display purposes.
    /// For example, "http://localhost:5000/login?t=token123" becomes "http://localhost:5000/login?t=●●●●●●●●"
    /// </summary>
    /// <param name="url">The URL to mask</param>
    /// <returns>The URL with masked querystring values</returns>
    public static string MaskQueryStringValues(string url)
    {
        var questionMarkIndex = url.IndexOf('?');
        if (questionMarkIndex == -1)
        {
            // No query string, return as is
            return url;
        }

        var baseUrl = url.Substring(0, questionMarkIndex);
        var queryString = url.Substring(questionMarkIndex);
        
        // Parse the query string using QueryHelpers
        var queryParams = QueryHelpers.ParseQuery(queryString);
        var maskedParts = new List<string>();

        foreach (var param in queryParams)
        {
            // Mask the value with 8 bullet points (unencoded for display)
            var maskedValue = GetMaskingText(8).Text;
            maskedParts.Add($"{param.Key}={maskedValue}");
        }

        // Rebuild the query string with masked values (unencoded for display)
        return $"{baseUrl}?{string.Join("&", maskedParts)}";
    }
}

internal record TextMask(MarkupString MarkupString, string Text);
