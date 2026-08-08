// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using Aspire.Dashboard.Model;
using Aspire.Dashboard.Model.Otlp;
using Aspire.Dashboard.Otlp.Storage;
using Aspire.Dashboard.Utils;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.FluentUI.AspNetCore.Components;
using Microsoft.JSInterop;

namespace Aspire.Dashboard.Components.Dialogs;

public partial class FilterDialog : IAsyncDisposable
{
    // Cancels in-flight telemetry reads when the dialog closes. Reads now run against SQLite on the thread
    // pool, so without this a closed dialog leaves a full-table scan running with nobody waiting on it.
    private readonly CancellationTokenSource _disposeCts = new();
    private long _fieldValuesUpdateVersion;
    private List<SelectViewModel<FilterCondition>> _filterConditions = null!;
    private List<SelectViewModel<FilterCondition>> _stringFilterConditions = null!;
    private List<SelectViewModel<FilterCondition>> _numericFilterConditions = null!;
    private List<SelectViewModel<FilterCondition>> _dateFilterConditions = null!;

    private SelectViewModel<FilterCondition> CreateFilterSelectViewModel(FilterCondition condition) =>
        new SelectViewModel<FilterCondition> { Id = condition, Name = FieldTelemetryFilter.ConditionToString(condition, FilterLoc) };

    [CascadingParameter]
    public FluentDialog? Dialog { get; set; }

    [Parameter]
    public FilterDialogViewModel Content { get; set; } = default!;

    [Inject]
    public required DashboardDataSource DataSource { get; init; }

    [Inject]
    public required ILogger<FilterDialog> Logger { get; init; }

    public ITelemetryRepository TelemetryRepository => DataSource.TelemetryRepository;

    [Inject]
    public required IJSRuntime JS { get; init; }

    private IJSObjectReference? _jsModule;
    private ElementReference _datePickerInput;
    private FilterDialogFormModel _formModel = default!;
    private List<SelectViewModel<string>> _parameters = default!;
    private List<SelectViewModel<FieldValue>> _filteredValues = default!;
    private List<SelectViewModel<FieldValue>>? _allValues;
    private bool _loadingPropertyKeys = true;
    private bool _loadingFieldValues = true;

    public EditContext EditContext { get; private set; } = default!;

    protected override async Task OnInitializedAsync()
    {
        _stringFilterConditions =
        [
            CreateFilterSelectViewModel(FilterCondition.Equals),
            CreateFilterSelectViewModel(FilterCondition.Contains),
            CreateFilterSelectViewModel(FilterCondition.NotEqual),
            CreateFilterSelectViewModel(FilterCondition.NotContains)
        ];

        _numericFilterConditions =
        [
            CreateFilterSelectViewModel(FilterCondition.Equals),
            CreateFilterSelectViewModel(FilterCondition.NotEqual),
            CreateFilterSelectViewModel(FilterCondition.GreaterThanOrEqual),
            CreateFilterSelectViewModel(FilterCondition.GreaterThan),
            CreateFilterSelectViewModel(FilterCondition.LessThanOrEqual),
            CreateFilterSelectViewModel(FilterCondition.LessThan)
        ];

        _dateFilterConditions =
        [
            CreateFilterSelectViewModel(FilterCondition.GreaterThanOrEqual),
            CreateFilterSelectViewModel(FilterCondition.GreaterThan),
            CreateFilterSelectViewModel(FilterCondition.LessThanOrEqual),
            CreateFilterSelectViewModel(FilterCondition.LessThan),
            CreateFilterSelectViewModel(FilterCondition.Equals),
            CreateFilterSelectViewModel(FilterCondition.NotEqual)
        ];

        _filterConditions = _stringFilterConditions;

        _formModel = new FilterDialogFormModel();
        EditContext = new EditContext(_formModel);

        _filteredValues = [];
        _parameters = CreateParameters([]);

        if (Content.Filter is { } filter)
        {
            _formModel.Parameter = _parameters.SingleOrDefault(c => c.Id == filter.Field);
            UpdateSelectedParameter();
            _formModel.Condition = _filterConditions.SingleOrDefault(c => c.Id == filter.Condition) ?? GetDefaultCondition();
            SetFormValue(filter.Value);
        }
        else
        {
            _formModel.Parameter = _parameters.FirstOrDefault();
            UpdateSelectedParameter();
            _formModel.Condition = GetDefaultCondition();
            SetFormValue("");
        }

        if (!await UpdateParameterFieldValuesAsync())
        {
            return;
        }
        ValueChanged();

        try
        {
            var propertyKeys = await Content.GetPropertyKeysAsync(_disposeCts.Token);

            var selectedParameter = _formModel.Parameter?.Id;
            _parameters = CreateParameters(propertyKeys);
            _formModel.Parameter = _parameters.SingleOrDefault(parameter => parameter.Id == selectedParameter) ?? _parameters.FirstOrDefault();
        }
        catch (OperationCanceledException) when (_disposeCts.IsCancellationRequested)
        {
            // The dialog closed while the read was in flight.
        }
        catch (Exception ex)
        {
            // Custom property keys are additive to KnownKeys, so the dialog is still usable without them.
            // Letting the exception escape OnInitializedAsync would tear down the circuit and take the whole
            // dashboard tab with it for what is a recoverable read failure.
            Logger.LogWarning(ex, "Error loading filter property keys.");
        }
        finally
        {
            // Property keys are read from the database on a background thread, so this can fault.
            // Clear the flag in a finally, otherwise the parameter combobox stays disabled with a
            // spinner for the lifetime of the dialog and the only recovery is reloading the page.
            _loadingPropertyKeys = false;
        }
    }

    private List<SelectViewModel<string>> CreateParameters(List<string> propertyKeys)
    {
        var knownFields = Content.KnownKeys.Select(p => new SelectViewModel<string> { Id = p, Name = FieldTelemetryFilter.ResolveFieldName(p) }).ToList();
        var customFields = propertyKeys
            .Append(Content.Filter is { Field: { } field } && !Content.KnownKeys.Contains(field, StringComparers.OtlpAttribute) ? field : null)
            .OfType<string>()
            .Distinct(StringComparers.OtlpAttribute)
            .Select(propertyKey => new SelectViewModel<string> { Id = propertyKey, Name = FieldTelemetryFilter.ResolveFieldName(propertyKey) })
            .ToList();

        return customFields.Count > 0
            ?
            [
                .. knownFields,
                new SelectViewModel<string> { Id = null, Name = "-" },
                .. customFields
            ]
            : knownFields;
    }

    private void UpdateSelectedParameter()
    {
        var fieldType = _formModel.Parameter?.Id is { } parameterName ? FieldTelemetryFilter.GetFieldType(parameterName) : FieldType.String;
        _formModel.ValueIsNumeric = fieldType is FieldType.Numeric;
        _formModel.ValueIsDate = fieldType is FieldType.Date;
        _filterConditions = fieldType switch
        {
            FieldType.Numeric => _numericFilterConditions,
            FieldType.Date => _dateFilterConditions,
            _ => _stringFilterConditions
        };
    }

    private SelectViewModel<FilterCondition> GetDefaultCondition()
    {
        var condition = (_formModel.ValueIsNumeric || _formModel.ValueIsDate) ? FilterCondition.GreaterThanOrEqual : FilterCondition.Contains;
        return _filterConditions.Single(c => c.Id == condition);
    }

    private void SetFormValue(string value)
    {
        if (_formModel.ValueIsNumeric)
        {
            _formModel.Value = null;
            _formModel.NumericValue = double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var numericValue) && double.IsFinite(numericValue)
                ? numericValue
                : null;
        }
        else
        {
            _formModel.Value = value;
            _formModel.NumericValue = null;
        }
    }

    private async Task<bool> UpdateParameterFieldValuesAsync()
    {
        var updateVersion = Interlocked.Increment(ref _fieldValuesUpdateVersion);

        if (_formModel.ValueIsNumeric || _formModel.ValueIsDate)
        {
            _allValues = null;
            _filteredValues = [];
            _loadingFieldValues = false;
            return true;
        }

        if (_formModel.Parameter?.Id is { } parameterName)
        {
            _loadingFieldValues = true;
            _allValues = null;
            _filteredValues = [];

            try
            {
                var fieldValues = await Content.GetFieldValuesAsync(parameterName, _disposeCts.Token);
                if (updateVersion != Volatile.Read(ref _fieldValuesUpdateVersion))
                {
                    return false;
                }

                _allValues = fieldValues
                    .Select(kvp => new FieldValue { Value = kvp.Key, Count = kvp.Value })
                    .OrderByDescending(v => v.Count)
                    .ThenBy(v => v.Value, StringComparers.OtlpFieldValue)
                    .Select(v => new SelectViewModel<FieldValue> { Id = v, Name = v.Value })
                    .ToList();
                _loadingFieldValues = false;
            }
            catch (OperationCanceledException) when (_disposeCts.IsCancellationRequested)
            {
                // The dialog closed while the read was in flight.
                return false;
            }
            catch (Exception ex)
            {
                // Field values only drive the value autocomplete, so the user can still type a value. Failing
                // the whole dialog for a recoverable read error would be a worse outcome.
                Logger.LogWarning(ex, "Error loading filter values for field '{FieldName}'.", parameterName);

                // Only the newest in-flight load owns the loading flag. A stale load clearing it here
                // would hide the spinner while a newer load is still running.
                if (updateVersion != Volatile.Read(ref _fieldValuesUpdateVersion))
                {
                    return false;
                }

                _loadingFieldValues = false;
            }
        }
        else
        {
            _allValues = null;
            _loadingFieldValues = false;
        }

        return true;
    }

    private async Task ParameterChangedAsync()
    {
        UpdateSelectedParameter();
        _formModel.Condition = GetDefaultCondition();
        SetFormValue("");
        if (!await UpdateParameterFieldValuesAsync())
        {
            return;
        }

        StateHasChanged();

        if (_formModel.ValueIsNumeric || _formModel.ValueIsDate)
        {
            return;
        }

        // Clearing the selected value and the combo box items together wasn't correctly clearing the selected value.
        // This is hacky, but adding a delay between the two operations puts the combo box in the right state.
        // Limitation of FluentUI: https://github.com/microsoft/fluentui-blazor/issues/2708
        await Task.Delay(100);
        ValueChanged();
    }

    private void ValueChanged()
    {
        if (_formModel.ValueIsNumeric || _formModel.ValueIsDate)
        {
            return;
        }

        // Limit to 1000 items to avoid the combo box have too many items and impacting UI perf.
        const int maxItems = 1000;

        if (_allValues != null)
        {
            IEnumerable<SelectViewModel<FieldValue>> newValues = _allValues;
            if (_formModel.Value is { Length: > 0 } value)
            {
                newValues = newValues.Where(vm => vm.Name.Contains(value, StringComparison.OrdinalIgnoreCase));
            }

            // If no values match the filter, don't apply the filter.
            // This improves user experience and fixes some combobox issues.
            // https://github.com/microsoft/fluentui-blazor/issues/4314#issuecomment-3577475233
            _filteredValues = newValues.Any() ? newValues.Take(maxItems).ToList() : _allValues.Take(maxItems).ToList();
        }
        else
        {
            _filteredValues = [];
        }
    }

    private void Cancel()
    {
        Dialog!.CancelAsync();
    }

    private void Enable()
    {
        Dialog!.CloseAsync(DialogResult.Ok(new FilterDialogResult { Filter = Content.Filter, Enable = true }));
    }

    private void Disable()
    {
        Dialog!.CloseAsync(DialogResult.Ok(new FilterDialogResult { Filter = Content.Filter, Disable = true }));
    }

    private void Delete()
    {
        Dialog!.CloseAsync(DialogResult.Ok(new FilterDialogResult { Filter = Content.Filter, Delete = true }));
    }

    private void Apply()
    {
        string value;
        if (_formModel.ValueIsNumeric)
        {
            value = _formModel.NumericValue!.Value.ToString("R", CultureInfo.InvariantCulture);
        }
        else
        {
            value = _formModel.Value!;
        }

        if (Content.Filter is { } filter)
        {
            filter.Field = _formModel.Parameter!.Id!;
            filter.Condition = _formModel.Condition!.Id;
            filter.Value = value;

            Dialog!.CloseAsync(DialogResult.Ok(new FilterDialogResult() { Filter = filter, Delete = false }));
        }
        else
        {
            filter = new FieldTelemetryFilter
            {
                Field = _formModel.Parameter!.Id!,
                Condition = _formModel.Condition!.Id,
                Value = value
            };

            Dialog!.CloseAsync(DialogResult.Ok(new FilterDialogResult() { Filter = filter, Add = true }));
        }
    }

    private async Task OpenDatePickerAsync()
    {
        _jsModule ??= await JS.InvokeAsync<IJSObjectReference>("import", "./Components/Dialogs/FilterDialog.razor.js");
        await _jsModule.InvokeVoidAsync("showPicker", _datePickerInput);
    }

    private void OnDateTimePickerChanged(ChangeEventArgs e)
    {
        // The datetime-local input returns a value in "YYYY-MM-DDThh:mm:ss" format (local time).
        if (e.Value is string dateStr &&
            DateTime.TryParse(dateStr, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var localDateTime))
        {
            _formModel.Value = FormatIsoDate(localDateTime);
        }
    }

    private static string FormatIsoDate(DateTime dateTime)
    {
        // Format as ISO 8601 without trailing zeros on fractional seconds.
        // e.g. "2024-01-15T09:30:00" or "2024-01-15T09:30:00.12"
        return dateTime.Ticks % TimeSpan.TicksPerSecond == 0
            ? dateTime.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture)
            : dateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.FFFFFFF", CultureInfo.InvariantCulture);
    }

    public async ValueTask DisposeAsync()
    {
        await _disposeCts.CancelAsync();
        _disposeCts.Dispose();
        await JSInteropHelpers.SafeDisposeAsync(_jsModule);
    }

    private sealed class FieldValue
    {
        public required string Value { get; init; }
        public required int Count { get; init; }
    }
}
