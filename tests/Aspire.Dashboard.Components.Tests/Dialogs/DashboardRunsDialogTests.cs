// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Components.Dialogs;
using Aspire.Dashboard.Components.Tests.Shared;
using Aspire.Dashboard.Model;
using Aspire.Dashboard.Utils;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
using Xunit;

namespace Aspire.Dashboard.Components.Tests.Dialogs;

[UseCulture("en-US")]
public class DashboardRunsDialogTests : DashboardTestContext
{
    private static readonly DashboardRunDescriptor s_currentRun = CreateRun("current", DateTimeOffset.UnixEpoch, isCurrent: true);

    [Fact]
    public void Render_ListsLiveViewThenRecordings()
    {
        var historicalRun = CreateRun(
            "historical",
            new DateTimeOffset(2025, 1, 2, 12, 30, 0, TimeSpan.Zero),
            endedAtUtc: new DateTimeOffset(2025, 1, 2, 13, 30, 0, TimeSpan.Zero));
        var cut = RenderDialog([s_currentRun, historicalRun]);
        var expectedTooltip = FormatHelpers.FormatDateTime(Services.GetRequiredService<BrowserTimeProvider>(), historicalRun.StartedAtUtc.UtcDateTime);

        Assert.Equal(
            "Switch between the live view and read-only recordings of previous runs. Protect a recording from deletion to keep it when older recordings are automatically removed.",
            cut.Find(".runs-description").TextContent);
        Assert.Equal("Past recordings", cut.Find(".runs-group-header").TextContent);
        Assert.Collection(
            cut.FindAll(".run-row"),
            row =>
            {
                Assert.Equal("current", row.GetAttribute("data-run-id"));
                Assert.Contains("selected", row.ClassList);
                var select = row.QuerySelector(".run-select")!;
                Assert.Equal("radio", select.GetAttribute("role"));
                Assert.Equal("true", select.GetAttribute("aria-checked"));
                Assert.Equal("Live view", row.QuerySelector(".run-label")!.TextContent);
                Assert.Single(row.QuerySelectorAll(".run-indicator-slot .dashboard-run-indicator.live"));
                Assert.Null(row.QuerySelector(".run-description"));
                Assert.Equal("Protect from deletion", row.QuerySelector(".run-pin")!.GetAttribute("aria-label"));
                Assert.Equal("false", row.QuerySelector(".run-pin")!.GetAttribute("aria-pressed"));
            },
            row =>
            {
                Assert.Equal("historical", row.GetAttribute("data-run-id"));
                var select = row.QuerySelector(".run-select")!;
                Assert.Equal("false", select.GetAttribute("aria-checked"));
                Assert.Equal(expectedTooltip, select.GetAttribute("data-tooltip"));
                Assert.Equal("352 days ago", row.QuerySelector(".run-label")!.TextContent);
                Assert.Equal("(1h)", row.QuerySelector(".run-description")!.TextContent);
                Assert.Null(row.QuerySelector(".dashboard-run-indicator"));
            });
        var helpLink = cut.Find(".runs-help-link");
        Assert.Equal("Learn about recordings", helpLink.TextContent.Trim());
        Assert.Equal(DashboardRunsDialog.DashboardRunsHelpUrl, helpLink.GetAttribute("href"));
        Assert.Equal("_blank", helpLink.GetAttribute("target"));
    }

    [Fact]
    public void Render_PrunedRecording_ShowsEmptyRecordings()
    {
        var historicalRun = CreateRun("historical", DateTimeOffset.UnixEpoch, endedAtUtc: DateTimeOffset.UnixEpoch);
        historicalRun.IsPruned = true;

        var cut = RenderDialog([s_currentRun, historicalRun]);

        Assert.Equal(["current"], cut.FindAll(".run-row").Select(row => row.GetAttribute("data-run-id")));
        Assert.Equal("No existing recordings", cut.Find(".runs-empty").TextContent);
    }

    [Fact]
    public void Render_IncompatibleRecording_IsDisabledButCanBeProtected()
    {
        var incompatibleRun = new DashboardRunDescriptor(
            "incompatible", DashboardRunStore.SchemaVersion - 1, new DateTimeOffset(2025, 1, 2, 12, 30, 0, TimeSpan.Zero),
            new DateTimeOffset(2025, 1, 2, 13, 30, 0, TimeSpan.Zero), true, "TestApp", string.Empty, IsCurrent: false);
        var unavailableRun = CreateRun("unavailable", new DateTimeOffset(2025, 1, 3, 12, 30, 0, TimeSpan.Zero));
        unavailableRun.IsSelectable = false;

        var cut = RenderDialog([s_currentRun, unavailableRun, incompatibleRun]);

        Assert.Equal(["current", "incompatible"], cut.FindAll(".run-row").Select(row => row.GetAttribute("data-run-id")));
        var row = cut.Find(".run-row[data-run-id='incompatible']");
        Assert.Contains("disabled", row.ClassList);
        var select = row.QuerySelector(".run-select")!;
        Assert.True(select.HasAttribute("disabled"));
        Assert.Equal(
            "This recording can't be viewed because it was created by an incompatible version of the dashboard.",
            select.GetAttribute("data-tooltip"));

        cut.Find(".run-row[data-run-id='incompatible'] .run-pin").Click();

        Assert.True(incompatibleRun.IsPinned);
    }

    [Fact]
    public void Select_InvokesSelectionWithRunIdOrNullForLiveView()
    {
        var historicalRun = CreateRun("historical", DateTimeOffset.UnixEpoch, endedAtUtc: DateTimeOffset.UnixEpoch);
        var selections = new List<string?>();

        var cut = RenderDialog([s_currentRun, historicalRun], selectedRunId: "historical", onSelect: selections.Add);
        Assert.Equal("true", cut.Find(".run-row[data-run-id='historical'] .run-select").GetAttribute("aria-checked"));
        Assert.Single(cut.FindAll(".run-row[data-run-id='historical'] .dashboard-run-indicator.recording"));
        Assert.Empty(cut.FindAll(".run-row[data-run-id='current'] .dashboard-run-indicator"));

        cut.Find(".run-row[data-run-id='historical'] .run-select").Click();
        cut.Find(".run-row[data-run-id='current'] .run-select").Click();

        Assert.Equal(["historical", null], selections);
    }

    [Fact]
    public void Pin_ReordersRecordingsAndKeepsFocusOnThePinButton()
    {
        var historicalRuns = new[]
        {
            CreateRun("unpinned-b", new DateTimeOffset(2025, 1, 2, 12, 0, 0, TimeSpan.Zero)),
            CreateRun("pinned-b", new DateTimeOffset(2025, 1, 2, 11, 0, 0, TimeSpan.Zero)),
            CreateRun("unpinned-a", new DateTimeOffset(2025, 1, 2, 9, 0, 0, TimeSpan.Zero)),
            CreateRun("pinned-a", new DateTimeOffset(2025, 1, 2, 10, 0, 0, TimeSpan.Zero))
        };
        var runStore = new FluentUISetupHelpers.TestDashboardRunStore([s_currentRun, .. historicalRuns]);
        runStore.SetRunPinned(historicalRuns[1], isPinned: true);
        runStore.SetRunPinned(historicalRuns[3], isPinned: true);
        var cut = RenderDialog(runStore);
        Assert.Equal(["current", "pinned-b", "pinned-a", "unpinned-b", "unpinned-a"], GetRowIds(cut));

        var pinButton = cut.Find(".run-row[data-run-id='unpinned-b'] .run-pin");
        var pinButtonId = pinButton.Id;
        pinButton.Click();

        Assert.Equal(["current", "unpinned-b", "pinned-b", "pinned-a", "unpinned-a"], GetRowIds(cut));
        var pinned = cut.Find(".run-row[data-run-id='unpinned-b'] .run-pin");
        Assert.Equal(pinButtonId, pinned.Id);
        Assert.Equal("true", pinned.GetAttribute("aria-pressed"));
        Assert.Equal("Allow deletion", pinned.GetAttribute("aria-label"));
        Assert.Contains("pinned", pinned.ClassList);
        Assert.Equal(
            [pinButtonId],
            JSInterop.Invocations.Where(invocation => invocation.Identifier == "focusElement")
                .Select(invocation => Assert.IsType<string>(Assert.Single(invocation.Arguments))));
    }

    [Fact]
    public void Pin_Failure_LogsAndKeepsRecordingUnprotected()
    {
        var historicalRun = CreateRun("historical", DateTimeOffset.UnixEpoch, endedAtUtc: DateTimeOffset.UnixEpoch);
        var runStore = new FluentUISetupHelpers.TestDashboardRunStore([s_currentRun, historicalRun]);
        var exception = new IOException("The run metadata could not be written.");
        runStore.OnSetRunPinned = (run, _) =>
        {
            if (run.RunId == historicalRun.RunId)
            {
                throw exception;
            }
        };
        var testSink = new TestSink();
        Services.AddSingleton<ILogger<DashboardRunsDialog>>(new TestLogger<DashboardRunsDialog>(new TestLoggerFactory(testSink, enabled: true)));
        var cut = RenderDialog(runStore);

        cut.Find(".run-row[data-run-id='historical'] .run-pin").Click();

        Assert.False(historicalRun.IsPinned);
        Assert.Equal("false", cut.Find(".run-row[data-run-id='historical'] .run-pin").GetAttribute("aria-pressed"));
        var errorLog = Assert.Single(testSink.Writes);
        Assert.Equal(LogLevel.Error, errorLog.LogLevel);
        Assert.Equal("Failed to update the pinned state of dashboard run 'historical'.", errorLog.Message);
        Assert.Same(exception, errorLog.Exception);
    }

    private IRenderedComponent<DashboardRunsDialog> RenderDialog(DashboardRunDescriptor[] runs, string? selectedRunId = null, Action<string?>? onSelect = null)
        => RenderDialog(new FluentUISetupHelpers.TestDashboardRunStore(runs), selectedRunId, onSelect);

    private IRenderedComponent<DashboardRunsDialog> RenderDialog(FluentUISetupHelpers.TestDashboardRunStore runStore, string? selectedRunId = null, Action<string?>? onSelect = null)
    {
        FluentUISetupHelpers.AddCommonDashboardServices(this, dashboardRunStore: runStore);
        FluentUISetupHelpers.SetupFluentUIComponents(this);
        JSInterop.SetupVoid("focusElement", _ => true).SetVoidResult();

        var content = new DashboardRunsDialogContent(selectedRunId ?? s_currentRun.RunId, runId =>
        {
            onSelect?.Invoke(runId);
            return Task.CompletedTask;
        });

        return Render<DashboardRunsDialog>(builder => builder.Add(p => p.Content, content));
    }

    private static List<string?> GetRowIds(IRenderedComponent<DashboardRunsDialog> cut) =>
        cut.FindAll(".run-row").Select(row => row.GetAttribute("data-run-id")).ToList();

    private static DashboardRunDescriptor CreateRun(string runId, DateTimeOffset startedAtUtc, DateTimeOffset? endedAtUtc = null, bool isCurrent = false) =>
        new(runId, DashboardRunStore.SchemaVersion, startedAtUtc, endedAtUtc, CleanShutdown: !isCurrent, "TestApp", string.Empty, isCurrent);
}
