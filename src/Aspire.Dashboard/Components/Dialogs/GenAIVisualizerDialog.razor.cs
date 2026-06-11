// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics.CodeAnalysis;
using Aspire.Dashboard.Components.Pages;
using Aspire.Dashboard.Model;
using Aspire.Dashboard.Model.GenAI;
using Aspire.Dashboard.Model.Markdown;
using Aspire.Dashboard.Otlp.Model;
using Aspire.Dashboard.Otlp.Storage;
using Aspire.Dashboard.Resources;
using Aspire.Dashboard.Telemetry;
using Aspire.Dashboard.Utils;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Localization;
using Microsoft.FluentUI.AspNetCore.Components;
using Microsoft.JSInterop;
using Icons = Microsoft.FluentUI.AspNetCore.Components.Icons;

namespace Aspire.Dashboard.Components.Dialogs;

public partial class GenAIVisualizerDialog : ComponentBase, IComponentWithTelemetry, IDisposable
{
    private static readonly Icon s_wrenchIcon = new Icons.Regular.Size16.Wrench();
    private static readonly Icon s_toolIcon = new Icons.Regular.Size16.Code();

    private readonly string _copyButtonId = $"copy-{Guid.NewGuid():N}";

    private MarkdownProcessor _markdownProcess = default!;
    private Subscription? _resourcesSubscription;
    private Subscription? _tracesSubscription;
    private Subscription? _logsSubscription;

    private List<OtlpSpan> _contextSpans = default!;
    private int _currentSpanContextIndex;
    private GenAIVisualizerDialogViewModel? _content;

    private GenAIItemViewModel? SelectedItem { get; set; }

    private OverviewViewKind OverviewActiveView { get; set; }
    private ItemViewKind MessageActiveView { get; set; }

    [Parameter, EditorRequired]
    public required GenAIVisualizerDialogViewModel Content { get; set; }

    [Inject]
    public required BrowserTimeProvider TimeProvider { get; init; }

    [Inject]
    public required TelemetryRepository TelemetryRepository { get; init; }

    [Inject]
    public required IStringLocalizer<Resources.Dialogs> Loc { get; init; }

    [Inject]
    public required IStringLocalizer<ControlsStrings> ControlsStringsLoc { get; init; }

    [Inject]
    public required ILogger<GenAIVisualizerDialog> Logger { get; init; }

    [Inject]
    public required IJSRuntime JS { get; init; }

    [Inject]
    public required ITelemetryErrorRecorder ErrorRecorder { get; init; }

    [Inject]
    public required ComponentTelemetryContextProvider TelemetryContextProvider { get; init; }

    public bool NoPreviousGenAISpan => _currentSpanContextIndex == 0;
    public bool NoNextGenAISpan => _currentSpanContextIndex >= _contextSpans.Count - 1;

    // IComponentWithTelemetry impl
    public ComponentTelemetryContext TelemetryContext { get; } = new(ComponentType.Control, TelemetryComponentIds.GenAIVisualizerDialog);

    protected override void OnInitialized()
    {
        _markdownProcess = GenAIMarkdownHelper.CreateProcessor(ControlsStringsLoc);
        _resourcesSubscription = TelemetryRepository.OnNewResources(UpdateDialogData);
        _tracesSubscription = TelemetryRepository.OnNewTraces(Content.Span.Source.ResourceKey, SubscriptionType.Read, UpdateDialogData);
        _logsSubscription = TelemetryRepository.OnNewLogs(Content.Span.Source.ResourceKey, SubscriptionType.Read, UpdateDialogData);

        TelemetryContextProvider.Initialize(TelemetryContext);
    }

    protected override void OnParametersSet()
    {
        if (_content != Content)
        {
            _contextSpans = Content.GetContextGenAISpans();
            _currentSpanContextIndex = _contextSpans.FindIndex(s => s.SpanId == Content.Span.SpanId);
            _content = Content;

            if (Content.SelectedLogEntryId != null)
            {
                SelectedItem = Content.Items.SingleOrDefault(e => e.InternalId == Content.SelectedLogEntryId);
            }
        }
    }

    private async Task UpdateDialogData()
    {
        // Multiple threads can call this. Run check inside InvokeAsync to avoid concurrency issues.
        await InvokeAsync(() =>
        {
            var hasUpdatedTrace = TelemetryRepository.HasUpdatedTrace(Content.Span.Trace);
            var newContextSpans = Content.GetContextGenAISpans();

            // Only update dialog data if the current trace has been updated,
            // or if there are new context spans (for the next/previous buttons).
            var newData = (hasUpdatedTrace || newContextSpans.Count > _contextSpans.Count);
            if (newData)
            {
                var span = newContextSpans.Find(s => s.SpanId == Content.Span.SpanId)!;

                _contextSpans = newContextSpans;
                _currentSpanContextIndex = _contextSpans.IndexOf(span);

                TryUpdateViewedGenAISpan(span);
                StateHasChanged();
            }
        });
    }

    private void ResetToolStates()
    {
        foreach (var td in Content.ToolDefinitions)
        {
            td.Expanded = false;
            td.Highlighted = false;
        }
    }

    private void OnViewItem(GenAIItemViewModel? viewModel)
    {
        SelectedItem = viewModel;
    }

    private async Task GoBackAsync()
    {
        var previousIndex = SelectedItem?.Index;
        SelectedItem = null;
        OverviewActiveView = OverviewViewKind.InputOutput;

        if (previousIndex is { } index)
        {
            // Allow the UI to render the overview before scrolling.
            await Task.Delay(50);
            await JS.InvokeVoidAsync("scrollToElement", $"genai-message-{index}");
        }
    }

    private async Task ViewToolDefinitionAsync(ToolDefinitionViewModel toolDefinition)
    {
        SelectedItem = null;
        OverviewActiveView = OverviewViewKind.Tools;
        toolDefinition.Expanded = true;
        toolDefinition.Highlighted = true;

        // Allow the UI to render the tools tab before scrolling.
        await Task.Delay(50);
        await JS.InvokeVoidAsync("scrollToElement", toolDefinition.ElementId);
    }

    private bool TryGetToolCall(string id, [NotNullWhen(true)] out GenAIItemViewModel? itemVM, [NotNullWhen(true)] out ToolCallRequestPart? toolCallRequestPart)
    {
        foreach (var messages in Content.InputMessages)
        {
            foreach (var part in messages.ItemParts)
            {
                if (part.MessagePart is ToolCallRequestPart { } p && p.Id == id)
                {
                    itemVM = messages;
                    toolCallRequestPart = p;
                    return true;
                }
            }
        }

        itemVM = null;
        toolCallRequestPart = null;
        return false;
    }

    private Task HandleSelectedTreeItemChangedAsync()
    {
        var selectedIndex = Content.SelectedTreeItem?.Data as int?;
        SelectedItem = Content.Items.FirstOrDefault(m => m.Index == selectedIndex);
        if (SelectedItem != null)
        {
            ResetToolStates();
        }
        StateHasChanged();
        return Task.CompletedTask;
    }

    private void OnOverviewTabChange(FluentTab newTab)
    {
        var id = newTab.Id?.Substring("tab-overview-".Length);

        if (id is null
            || !Enum.TryParse(typeof(OverviewViewKind), id, out var o)
            || o is not OverviewViewKind viewKind)
        {
            return;
        }

        if (viewKind != OverviewViewKind.Tools)
        {
            ResetToolStates();
        }

        OverviewActiveView = viewKind;
    }

    private void OnMessageTabChange(FluentTab newTab)
    {
        var id = newTab.Id?.Substring("tab-message-".Length);

        if (id is null
            || !Enum.TryParse(typeof(ItemViewKind), id, out var o)
            || o is not ItemViewKind viewKind)
        {
            return;
        }

        MessageActiveView = viewKind;
    }

    private void OnPreviousGenAISpan()
    {
        if (TryGetContextSpanByIndex(_currentSpanContextIndex - 1, out var span))
        {
            TryUpdateViewedGenAISpan(span);
        }
    }

    private void OnNextGenAISpan()
    {
        if (TryGetContextSpanByIndex(_currentSpanContextIndex + 1, out var span))
        {
            TryUpdateViewedGenAISpan(span);
        }
    }

    private bool TryGetContextSpanByIndex(int index, [NotNullWhen(true)] out OtlpSpan? span)
    {
        span = _contextSpans.ElementAtOrDefault(index);
        return span != null;
    }

    private bool TryUpdateViewedGenAISpan(OtlpSpan newSpan)
    {
        var selectedIndex = SelectedItem?.Index;

        var spanDetailsViewModel = SpanDetailsViewModel.Create(newSpan, TelemetryRepository, TelemetryRepository.GetResources());
        var dialogViewModel = GenAIVisualizerDialogViewModel.Create(spanDetailsViewModel, selectedLogEntryId: null, ErrorRecorder, TelemetryRepository, Content.GetContextGenAISpans);

        if (selectedIndex != null)
        {
            SelectedItem = dialogViewModel.Items.SingleOrDefault(e => e.Index == selectedIndex);
        }

        Content = dialogViewModel;
        _currentSpanContextIndex = _contextSpans.IndexOf(newSpan);

        if (OverviewActiveView is OverviewViewKind.Tools && Content.ToolDefinitions.Count == 0)
        {
            OverviewActiveView = OverviewViewKind.InputOutput;
        }
        else if (OverviewActiveView is OverviewViewKind.Evaluations && Content.Evaluations.Count == 0)
        {
            OverviewActiveView = OverviewViewKind.InputOutput;
        }

        return true;
    }

    private static string GetToolHeadingTooltip(ToolDefinitionViewModel vm)
    {
        if (string.IsNullOrEmpty(vm.ToolDefinition.Description))
        {
            return vm.ToolDefinition.Name ?? string.Empty;
        }

        return $"{vm.ToolDefinition.Name} - {vm.ToolDefinition.Description}";
    }

    private record DataInfo(string Url, string MimeType, string FileName);

    private static bool TryGetDataPart(GenAIItemPartViewModel itemPart, HashSet<string>? matchingMimeTypes, [NotNullWhen(true)] out DataInfo? dataInfo)
    {
        switch (itemPart.MessagePart)
        {
            case BlobPart blobPart:
                {
                    if (MatchMimeType(blobPart.MimeType, matchingMimeTypes))
                    {
                        if (!string.IsNullOrEmpty(blobPart.Content))
                        {
                            dataInfo = new DataInfo(
                                Url: $"data:{blobPart.MimeType};base64,{blobPart.Content}",
                                MimeType: blobPart.MimeType!,
                                FileName: CalculateFileName(currentFileName: null, blobPart.MimeType!));
                            return true;
                        }
                    }
                    break;
                }
            case UriPart uriPart:
                {
                    if (MatchMimeType(uriPart.MimeType, matchingMimeTypes))
                    {
                        if (!string.IsNullOrEmpty(uriPart.Uri))
                        {
                            // Only attempt to display image if it is an http/https address.
                            if (Uri.TryCreate(uriPart.Uri, UriKind.Absolute, out var result) && result.Scheme.ToLowerInvariant() is "http" or "https")
                            {
                                dataInfo = new DataInfo(
                                    Url: uriPart.Uri,
                                    MimeType: uriPart.MimeType!,
                                    FileName: CalculateFileName(Path.GetFileName(result.LocalPath), uriPart.MimeType!));
                                return true;
                            }
                        }
                    }
                    break;
                }
        }

        dataInfo = null;
        return false;

        static bool MatchMimeType(string? mimeType, HashSet<string>? matchingMimeTypes)
        {
            if (!string.IsNullOrEmpty(mimeType))
            {
                return matchingMimeTypes is null || matchingMimeTypes.Contains(mimeType);
            }

            return false;
        }

        static string CalculateFileName(string? currentFileName, string mimeType)
        {
            if (!string.IsNullOrEmpty(currentFileName))
            {
                return currentFileName;
            }

            if (MimeTypeHelpers.MimeToExtension.TryGetValue(mimeType, out var extension))
            {
                return $"download{extension}";
            }
            else
            {
                // The part didn't include a name (probably a blob) and we don't know the mime type.
                // We have to give a download file name without an extension.
                return "download";
            }
        }
    }

    public void Dispose()
    {
        _resourcesSubscription?.Dispose();
        _tracesSubscription?.Dispose();
        _logsSubscription?.Dispose();
        TelemetryContext.Dispose();
    }

    public static async Task OpenDialogAsync(DashboardDialogService dialogService,
        OtlpSpan span, long? selectedLogEntryId,
        TelemetryRepository telemetryRepository, ITelemetryErrorRecorder errorRecorder, List<OtlpResource> resources, Func<List<OtlpSpan>> getContextGenAISpans)
    {
        var title = span.Name;
        var width = dialogService.IsDesktop ? "75vw" : "100vw";
        var parameters = new DialogParameters
        {
            Title = title,
            Width = $"min(1000px, {width})",
            TrapFocus = true,
            Modal = true,
            PreventScroll = true,
        };

        var spanDetailsViewModel = SpanDetailsViewModel.Create(span, telemetryRepository, resources);

        var dialogViewModel = GenAIVisualizerDialogViewModel.Create(spanDetailsViewModel, selectedLogEntryId, errorRecorder, telemetryRepository, getContextGenAISpans);

        await dialogService.ShowDialogAsync<GenAIVisualizerDialog>(dialogViewModel, parameters);
    }
}

