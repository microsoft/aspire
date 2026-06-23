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
    private FluentTextField? _titleTextField;

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
        _additionalContext = FeedbackDiagnosticProvider.BuildAdditionalContext();

        // Resolve the AppHost line out-of-band: it can require launching MSBuild or Node.js, so it
        // must not block the synchronous environment lines from populating immediately. Remember the
        // generated value so the result is only appended when the user hasn't edited the field.
        var generatedContext = _additionalContext;
        var appHostContextTask = FeedbackDiagnosticProvider.CaptureAppHostContextAsync(_captureCts.Token);

        if (IssueKind == FeedbackIssueKind.Bug)
        {
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

        try
        {
            if (await appHostContextTask.ConfigureAwait(true) is { } appHostContext &&
                string.Equals(_additionalContext, generatedContext, StringComparison.Ordinal))
            {
                _additionalContext = generatedContext + appHostContext;
            }
        }
        catch (OperationCanceledException) when (_captureCts.IsCancellationRequested)
        {
        }
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender)
        {
            await Task.Yield();
            _titleTextField?.FocusAsync();
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
