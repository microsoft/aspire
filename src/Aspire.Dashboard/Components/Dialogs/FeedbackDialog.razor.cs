// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Model;
using Aspire.Shared;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Localization;
using Microsoft.FluentUI.AspNetCore.Components;
using DialogResources = Aspire.Dashboard.Resources.Dialogs;
using LayoutResources = Aspire.Dashboard.Resources.Layout;

namespace Aspire.Dashboard.Components.Dialogs;

public partial class FeedbackDialog : IDisposable
{
    internal const string TitleInputId = "feedback-issue-title";
    internal const string MainTextInputId = "feedback-issue-main-text";
    internal const string DoctorOutputInputId = "feedback-issue-doctor-output";
    internal const string AdditionalContextInputId = "feedback-issue-additional-context";
    internal const string OpenIssueButtonId = "feedback-issue-open-button";

    private readonly CancellationTokenSource _captureCts = new();
    private string? _title;
    private string? _mainText;
    private string? _aspireDoctorOutput;
    private string? _additionalContext;
    private bool _isCapturingBugContext;
    private bool _showAspireDoctorOutput;

    [Parameter, EditorRequired]
    public required FeedbackDialogViewModel Content { get; set; }

    [Inject]
    public required IStringLocalizer<LayoutResources> Loc { get; init; }

    [Inject]
    public required IStringLocalizer<DialogResources> DialogsLoc { get; init; }

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
        !_isCapturingBugContext &&
        !string.IsNullOrWhiteSpace(_title) &&
        !string.IsNullOrWhiteSpace(_mainText);

    // The GitHub "new issue" URL is bound to the Open issue button's data-url attribute so the link is
    // opened client-side (buttonOpenLink in app.js) synchronously within the user's click gesture.
    // Opening it from C# via JS interop would run only after a SignalR round-trip, and popup blockers
    // (Firefox, Safari, Brave, iOS) block window.open that isn't on the synchronous call stack of a user
    // gesture. Binding the URL here keeps the value in sync with the editable fields on every keystroke.
    private string IssueUrl => FeedbackIssueUrlBuilder.BuildUrl(new FeedbackIssueContext(
        Kind: IssueKind,
        Title: _title,
        MainText: _mainText,
        AspireDoctorOutput: _aspireDoctorOutput,
        AdditionalContext: _additionalContext));

    protected override async Task OnInitializedAsync()
    {
        // The AppHost description is forwarded by the AppHost (DASHBOARD__APPHOST__INFO), so it is
        // available synchronously and only included for bug reports (matching the CLI feedback
        // command, which gathers AppHost context only for bugs).
        _additionalContext = FeedbackDiagnosticProvider.BuildAdditionalContext(includeAppHostInfo: IssueKind == FeedbackIssueKind.Bug);

        // `aspire doctor` output is only relevant to bug reports, and the dashboard can only capture it
        // when the AppHost forwarded the launching CLI's path (DASHBOARD__CLI__PATH). When it didn't
        // (for example a standalone dashboard with no CLI), we neither show the field nor spawn the
        // capture, because the dashboard never probes for an `aspire` on PATH itself.
        _showAspireDoctorOutput = IssueKind == FeedbackIssueKind.Bug && FeedbackDiagnosticProvider.IsAspireDoctorOutputAvailable;
        if (!_showAspireDoctorOutput)
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

        // The new issue page is opened client-side by the data-openbutton handler (buttonOpenLink in
        // app.js) so window.open runs inside the click gesture and isn't blocked by popup blockers.
        // Here we only close the dialog once the user has triggered issue creation.
        if (Dialog is not null)
        {
            await Dialog.CloseAsync().ConfigureAwait(true);
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
