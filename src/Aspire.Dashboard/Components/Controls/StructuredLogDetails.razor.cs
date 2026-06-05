// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Components.Controls.PropertyValues;
using Aspire.Dashboard.Components.Pages;
using Aspire.Dashboard.Model;
using Aspire.Dashboard.Model.Assistant;
using Aspire.Dashboard.Model.Otlp;
using Aspire.Dashboard.Telemetry;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace Aspire.Dashboard.Components.Controls;

public partial class StructuredLogDetails : IDisposable
{
    [Parameter, EditorRequired]
    public required StructureLogsDetailsViewModel ViewModel { get; set; }

    [Parameter]
    public EventCallback CloseCallback { get; set; }

    [Inject]
    public required BrowserTimeProvider TimeProvider { get; init; }

    [Inject]
    public required IJSRuntime JS { get; init; }

    [Inject]
    public required IAIContextProvider AIContextProvider { get; init; }

    [Inject]
    public required ComponentTelemetryContextProvider TelemetryContextProvider { get; init; }

    internal IQueryable<TelemetryPropertyViewModel> FilteredItems =>
        _logEntryAttributes.Where(ApplyFilter).AsQueryable();

    internal IQueryable<TelemetryPropertyViewModel> FilteredExceptionItems =>
        _exceptionAttributes.Where(ApplyFilter).AsQueryable();

    internal IQueryable<TelemetryPropertyViewModel> FilteredContextItems =>
        _contextAttributes.Where(ApplyFilter).AsQueryable();

    internal IQueryable<TelemetryPropertyViewModel> FilteredResourceItems =>
        ViewModel.LogEntry.ResourceView.AllProperties().Select(p => new TelemetryPropertyViewModel { Name = p.DisplayName, Key = p.Key, Value = p.Value })
            .Where(ApplyFilter).AsQueryable();

    private string _filter = "";
    private bool _dataChanged;
    private StructureLogsDetailsViewModel? _viewModel;
    private Dictionary<string, ComponentMetadata>? _valueComponents;

    private List<TelemetryPropertyViewModel> _logEntryAttributes = null!;
    private List<TelemetryPropertyViewModel> _contextAttributes = null!;
    private List<TelemetryPropertyViewModel> _exceptionAttributes = null!;
    private AIContext? _aiContext;

    protected override void OnInitialized()
    {
        TelemetryContextProvider.Initialize(TelemetryContext);
        _aiContext = CreateAIContext();
    }

    protected override void OnParametersSet()
    {
        if (!ReferenceEquals(ViewModel, _viewModel))
        {
            // Only set data changed flag if the item being view changes.
            if (ViewModel.LogEntry.InternalId != _viewModel?.LogEntry.InternalId)
            {
                _dataChanged = true;

                // Update AI context with new log entry.
                _aiContext?.ContextHasChanged();
            }

            _viewModel = ViewModel;

            // Move some attributes to separate lists, e.g. exception attributes to their own list.
            // Remaining attributes are displayed along side the message.
            var attributes = _viewModel.LogEntry.Attributes
                .Select(a => new TelemetryPropertyViewModel { Name = a.Key, Key = $"unknown-{a.Key}", Value = a.Value })
                .ToList();

            _contextAttributes =
            [
                new TelemetryPropertyViewModel { Name = "Category", Key = KnownStructuredLogFields.CategoryField, Value = _viewModel.LogEntry.Scope.Name }
            ];
            if (!string.IsNullOrEmpty(_viewModel.LogEntry.EventName))
            {
                _contextAttributes.Add(new TelemetryPropertyViewModel { Name = "EventName", Key = KnownStructuredLogFields.EventNameField, Value = _viewModel.LogEntry.EventName });
            }
            if (HasTelemetryBaggage(_viewModel.LogEntry.TraceId))
            {
                _contextAttributes.Add(new TelemetryPropertyViewModel { Name = "TraceId", Key = KnownStructuredLogFields.TraceIdField, Value = _viewModel.LogEntry.TraceId });
            }
            if (HasTelemetryBaggage(_viewModel.LogEntry.SpanId))
            {
                _contextAttributes.Add(new TelemetryPropertyViewModel { Name = "SpanId", Key = KnownStructuredLogFields.SpanIdField, Value = _viewModel.LogEntry.SpanId });
            }

            _exceptionAttributes = [];
            MoveAttributes(attributes, _exceptionAttributes, a => a.Name.StartsWith("exception.", StringComparison.OrdinalIgnoreCase));

            _logEntryAttributes =
            [
                new TelemetryPropertyViewModel { Name = "Level", Key = KnownStructuredLogFields.LevelField, Value = _viewModel.LogEntry.Severity.ToString() },
                new TelemetryPropertyViewModel { Name = "Message", Key = KnownStructuredLogFields.MessageField, Value = _viewModel.LogEntry.Message },
                .. attributes,
            ];

            _valueComponents = new Dictionary<string, ComponentMetadata>
            {
                [KnownStructuredLogFields.TraceIdField] = new ComponentMetadata
                {
                    Type = typeof(TraceIdButtonValue),
                    Parameters = { ["OnClick"] = CloseCallback }
                },
                [KnownStructuredLogFields.SpanIdField] = new ComponentMetadata
                {
                    Type = typeof(SpanIdButtonValue),
                    Parameters = { ["TraceId"] = _viewModel.LogEntry.TraceId }
                },
                [KnownResourceFields.ServiceNameField] = new ComponentMetadata
                {
                    Type = typeof(ResourceNameButtonValue),
                    Parameters = { ["Resource"] = _viewModel.LogEntry.ResourceView.Resource }
                },
                [KnownStructuredLogFields.LevelField] = new ComponentMetadata
                {
                    Type = typeof(LogLevelValue),
                    Parameters = { ["LogEntry"] = _viewModel.LogEntry }
                },
            };
        }
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (_dataChanged)
        {
            if (!firstRender)
            {
                await JS.InvokeVoidAsync("scrollToTop", ".property-grid-container");
            }

            _dataChanged = false;
        }
    }

    private static void MoveAttributes(List<TelemetryPropertyViewModel> source, List<TelemetryPropertyViewModel> destination, Func<TelemetryPropertyViewModel, bool> predicate)
    {
        var insertStart = destination.Count;
        for (var i = source.Count - 1; i >= 0; i--)
        {
            if (predicate(source[i]))
            {
                destination.Insert(insertStart, source[i]);
                source.RemoveAt(i);
            }
        }
    }

    private bool ApplyFilter(TelemetryPropertyViewModel vm)
    {
        return vm.Name.Contains(_filter, StringComparison.CurrentCultureIgnoreCase) ||
            vm.Value?.Contains(_filter, StringComparison.CurrentCultureIgnoreCase) == true;
    }

    // Sometimes a parent ID is added and the value is 0000000000. Don't display unhelpful IDs.
    private static bool HasTelemetryBaggage(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return false;
        }

        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] != '0')
            {
                return true;
            }
        }

        return false;
    }

    private AIContext CreateAIContext()
    {
        return AIContextProvider.AddNew(nameof(StructuredLogDetails), c =>
        {
            c.BuildIceBreakers = (builder, context) => builder.StructuredLogs(context, ViewModel.LogEntry);
        });
    }

    // IComponentWithTelemetry impl
    public ComponentTelemetryContext TelemetryContext { get; } = new(ComponentType.Control, TelemetryComponentIds.StructuredLogDetails);

    public void Dispose()
    {
        TelemetryContext.Dispose();
        _aiContext?.Dispose();
    }
}
