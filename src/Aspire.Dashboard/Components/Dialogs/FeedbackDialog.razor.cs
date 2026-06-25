// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Model;
using Aspire.Shared;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Localization;
using Microsoft.FluentUI.AspNetCore.Components;
using Microsoft.JSInterop;
using DialogResources = Aspire.Dashboard.Resources.Dialogs;
using LayoutResources = Aspire.Dashboard.Resources.Layout;

namespace Aspire.Dashboard.Components.Dialogs;

public partial class FeedbackDialog : IDisposable
{
    internal const string TitleInputId = "feedback-issue-title";
    internal const string MainTextInputId = "feedback-issue-main-text";
    internal const string DoctorOutputInputId = "feedback-issue-doctor-output";
    internal const string AdditionalContextInputId = "feedback-issue-additional-context";

    private readonly CancellationTokenSource _captureCts = new();
    private string? _title;
    private string? _mainText;
    private string? _aspireDoctorOutput;
    private string? _additionalContext;
    private bool _isCapturingBugContext;
    private bool _isOpeningIssue;

    [Parameter, EditorRequired]
    public required FeedbackDialogViewModel Content { get; set; }

    [Inject]
    public required IStringLocalizer<LayoutResources> Loc { get; init; }

    [Inject]
    public required IStringLocalizer<DialogResources> DialogsLoc { get; init; }

    [Inject]
    public required IJSRuntime JS { get; init; }

    [Inject]
    public required IServiceProvider ServiceProvider { get; init; }

    [CascadingParameter]
    public FluentDialog? Dialog { get; set; }

    internal FeedbackIssueKind IssueKind => Content.Kind switch
    {
        nameof(FeedbackIssueKind.Bug) => FeedbackIssueKind.Bug,
        nameof(FeedbackIssueKind.Idea) => FeedbackIssueKind.Idea,
        nameof(FeedbackIssueKind.General) => FeedbackIssueKind.General,
        _ => throw new InvalidOperationException($"Unknown feedback kind '{Content.Kind}'.")
    };

    private string MainTextLabel => IssueKind switch
    {
        FeedbackIssueKind.Bug => Loc[nameof(LayoutResources.MainLayoutProvideFeedbackBugDescriptionLabel)],
        FeedbackIssueKind.Idea => Loc[nameof(LayoutResources.MainLayoutProvideFeedbackIdeaDescriptionLabel)],
        FeedbackIssueKind.General => Loc[nameof(LayoutResources.MainLayoutProvideFeedbackGeneralDescriptionLabel)],
        _ => throw new InvalidOperationException($"Unknown feedback kind '{Content.Kind}'.")
    };

    private bool CanOpenIssue =>
        !_isOpeningIssue &&
        !_isCapturingBugContext &&
        !string.IsNullOrWhiteSpace(_title) &&
        !string.IsNullOrWhiteSpace(_mainText);

    protected override async Task OnInitializedAsync()
    {
        // The AppHost description is forwarded by the AppHost (DASHBOARD__APPHOST__INFO), so it is
        // available synchronously and only included for bug reports (matching the CLI feedback
        // command, which gathers AppHost context only for bugs).
        _additionalContext = FeedbackDiagnosticProvider.BuildAdditionalContext(includeAppHostInfo: IssueKind == FeedbackIssueKind.Bug);

        // The `aspire doctor` output is only relevant to bug reports and launches the CLI, so skip it
        // entirely for idea/general feedback to avoid spawning a process when it isn't needed.
        if (IssueKind != FeedbackIssueKind.Bug)
        {
            return;
        }

        _isCapturingBugContext = true;
        try
        {
            _aspireDoctorOutput = await FeedbackDiagnosticProvider.CaptureAspireDoctorOutputAsync(_captureCts.Token).ConfigureAwait(true);
        }
        catch (OperationCanceledException) when (_captureCts.IsCancellationRequested)
        {
        }
        finally
        {
            _isCapturingBugContext = false;
        }
    }

    private IDashboardFeedbackDiagnosticProvider FeedbackDiagnosticProvider =>
        ServiceProvider.GetRequiredService<IDashboardFeedbackDiagnosticProvider>();

    private async Task OpenIssueAsync()
    {
        if (!CanOpenIssue)
        {
            return;
        }

        _isOpeningIssue = true;
        try
        {
            var url = FeedbackIssueUrlBuilder.BuildUrl(new FeedbackIssueContext(
                Kind: IssueKind,
                Title: _title,
                MainText: _mainText,
                AspireDoctorOutput: _aspireDoctorOutput,
                AdditionalContext: _additionalContext));

            await JS.InvokeVoidAsync("open", url, "_blank", "noopener,noreferrer").ConfigureAwait(true);

            if (Dialog is not null)
            {
                await Dialog.CloseAsync().ConfigureAwait(true);
            }
        }
        finally
        {
            _isOpeningIssue = false;
        }
    }

    private async Task CancelAsync()
    {
        if (Dialog is not null)
        {
            await Dialog.CancelAsync().ConfigureAwait(true);
        }
    }

    public void Dispose()
    {
        _captureCts.Cancel();
        _captureCts.Dispose();
    }
}
