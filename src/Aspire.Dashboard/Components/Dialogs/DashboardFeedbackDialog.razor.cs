// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Model;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Localization;
using Microsoft.FluentUI.AspNetCore.Components;
using Microsoft.JSInterop;

namespace Aspire.Dashboard.Components.Dialogs;

public partial class DashboardFeedbackDialog
{
    internal const string DiscussionUrl = "https://github.com/microsoft/aspire/discussions/new?category=dashboard-feedback";

    private int _step = 1;
    private string? _status;

    [Parameter, EditorRequired]
    public required DashboardFeedback Content { get; set; }

    [CascadingParameter]
    public required IDialogInstance Dialog { get; set; }

    [Inject]
    public required IStringLocalizer<Resources.Dialogs> Loc { get; init; }

    [Inject]
    public required IJSRuntime JS { get; init; }

    private string Markdown => Content.BuildMarkdown(Loc);

    private void Continue()
    {
        _step = 2;
        _status = null;
    }

    private void Back()
    {
        _step = 1;
        _status = null;
    }

    private void SetReason(DashboardFeedbackReason reason, bool selected)
    {
        if (selected)
        {
            Content.Reasons.Add(reason);
        }
        else
        {
            Content.Reasons.Remove(reason);
            if (reason == DashboardFeedbackReason.Other)
            {
                Content.OtherReason = null;
            }
        }
    }

    private string GetReasonText(DashboardFeedbackReason reason) => reason switch
    {
        DashboardFeedbackReason.Navigation => Loc[nameof(Resources.Dialogs.DashboardFeedbackReasonNavigation)],
        DashboardFeedbackReason.FindingApplicationUrls => Loc[nameof(Resources.Dialogs.DashboardFeedbackReasonFindingApplicationUrls)],
        DashboardFeedbackReason.TelemetryWorkflow => Loc[nameof(Resources.Dialogs.DashboardFeedbackReasonTelemetryWorkflow)],
        DashboardFeedbackReason.Terminals => Loc[nameof(Resources.Dialogs.DashboardFeedbackReasonTerminals)],
        DashboardFeedbackReason.PerformanceOrAccessibility => Loc[nameof(Resources.Dialogs.DashboardFeedbackReasonPerformanceOrAccessibility)],
        DashboardFeedbackReason.MissingFeature => Loc[nameof(Resources.Dialogs.DashboardFeedbackReasonMissingFeature)],
        DashboardFeedbackReason.Other => Loc[nameof(Resources.Dialogs.DashboardFeedbackReasonOther)],
        _ => throw new ArgumentOutOfRangeException(nameof(reason))
    };

    private async Task CompleteAsync()
    {
        try
        {
            var copied = await JS.InvokeAsync<bool>("copyTextAndOpen", Markdown, DiscussionUrl);
            if (Content.SwitchToLegacyAsync is not null)
            {
                await Content.SwitchToLegacyAsync();
            }
            else if (!copied)
            {
                _status = Loc[nameof(Resources.Dialogs.DashboardFeedbackCopyFailed)];
            }
            else
            {
                await Dialog.CloseAsync();
            }
        }
        catch (JSException)
        {
            _status = Loc[nameof(Resources.Dialogs.DashboardFeedbackCopyFailed)];
        }
    }
}
