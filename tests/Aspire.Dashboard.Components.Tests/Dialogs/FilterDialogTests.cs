// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Components.Dialogs;
using Aspire.Dashboard.Components.Tests.Shared;
using Aspire.Dashboard.Model;
using Aspire.Dashboard.Model.Otlp;
using Bunit;
using Microsoft.FluentUI.AspNetCore.Components;
using Xunit;

namespace Aspire.Dashboard.Components.Tests.Dialogs;

public class FilterDialogTests : DashboardTestContext
{
    [Fact]
    public void Render_DurationFilter_UsesNumericInputAndNumericConditions()
    {
        SetupFilterDialogServices();

        var cut = RenderComponent<FilterDialog>(builder =>
        {
            builder.Add(p => p.Content, CreateContent(new FieldTelemetryFilter
            {
                Field = KnownTraceFields.DurationField,
                Condition = FilterCondition.GreaterThanOrEqual,
                Value = "50"
            }));
        });

        Assert.Single(cut.FindComponents<FluentNumberField<double?>>());
        Assert.DoesNotContain("fluent-combobox", cut.Markup);

        var conditionSelect = Assert.Single(cut.FindComponents<FluentSelect<SelectViewModel<FilterCondition>>>());
        Assert.Collection(conditionSelect.Instance.Items!,
            item => Assert.Equal(FilterCondition.Equals, item.Id),
            item => Assert.Equal(FilterCondition.NotEqual, item.Id),
            item => Assert.Equal(FilterCondition.GreaterThanOrEqual, item.Id),
            item => Assert.Equal(FilterCondition.GreaterThan, item.Id),
            item => Assert.Equal(FilterCondition.LessThanOrEqual, item.Id),
            item => Assert.Equal(FilterCondition.LessThan, item.Id));
    }

    [Fact]
    public void Render_StringFilter_UsesComboboxAndStringConditions()
    {
        SetupFilterDialogServices();

        var cut = RenderComponent<FilterDialog>(builder =>
        {
            builder.Add(p => p.Content, CreateContent(new FieldTelemetryFilter
            {
                Field = KnownTraceFields.NameField,
                Condition = FilterCondition.Contains,
                Value = "request"
            }));
        });

        Assert.Empty(cut.FindComponents<FluentNumberField<double?>>());
        Assert.Contains("fluent-combobox", cut.Markup);
        Assert.Equal(3, cut.FindAll(".filter-input-container > label").Count);
        Assert.Empty(cut.FindAll(".input-line-container label"));

        var conditionSelect = Assert.Single(cut.FindComponents<FluentSelect<SelectViewModel<FilterCondition>>>());
        Assert.Collection(conditionSelect.Instance.Items!,
            item => Assert.Equal(FilterCondition.Equals, item.Id),
            item => Assert.Equal(FilterCondition.Contains, item.Id),
            item => Assert.Equal(FilterCondition.NotEqual, item.Id),
            item => Assert.Equal(FilterCondition.NotContains, item.Id));
    }

    [Fact]
    public async Task Render_PropertyKeysLoading_DisablesParameterSelectAndDisplaysProgressRing()
    {
        SetupFilterDialogServices();
        var loadingStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var propertyKeys = new TaskCompletionSource<List<string>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var content = CreateContent(new FieldTelemetryFilter
        {
            Field = KnownTraceFields.NameField,
            Condition = FilterCondition.Contains,
            Value = "request"
        });
        content = new FilterDialogViewModel
        {
            Filter = content.Filter,
            KnownKeys = content.KnownKeys,
            GetPropertyKeysAsync = _ =>
            {
                loadingStarted.SetResult();
                return propertyKeys.Task;
            },
            GetFieldValuesAsync = content.GetFieldValuesAsync
        };

        var cut = RenderComponent<FilterDialog>(builder => builder.Add(p => p.Content, content));
        await loadingStarted.Task.WaitAsync(DefaultWaitTimeout);

        Assert.True(cut.FindComponent<FluentSelect<SelectViewModel<string>>>().Instance.Disabled);
        Assert.Single(cut.FindComponents<FluentProgressRing>());
        Assert.NotNull(cut.Find(".input-line-container .input-progress"));

        propertyKeys.SetResult(["custom.attribute"]);

        cut.WaitForAssertion(() =>
        {
            var parameterSelect = cut.FindComponent<FluentSelect<SelectViewModel<string>>>();
            Assert.False(parameterSelect.Instance.Disabled);
            Assert.Empty(cut.FindComponents<FluentProgressRing>());
            Assert.Contains(parameterSelect.Instance.Items!, item => item.Id == "custom.attribute");
        });
    }

    [Fact]
    public async Task Render_FieldValuesLoading_DisablesValueComboboxAndDisplaysProgressRing()
    {
        SetupFilterDialogServices();
        var loadingStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var fieldValues = new TaskCompletionSource<Dictionary<string, int>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var content = CreateContent(new FieldTelemetryFilter
        {
            Field = KnownTraceFields.NameField,
            Condition = FilterCondition.Contains,
            Value = "request"
        });
        content = new FilterDialogViewModel
        {
            Filter = content.Filter,
            KnownKeys = content.KnownKeys,
            GetPropertyKeysAsync = content.GetPropertyKeysAsync,
            GetFieldValuesAsync = (_, _) =>
            {
                loadingStarted.SetResult();
                return fieldValues.Task;
            }
        };

        var cut = RenderComponent<FilterDialog>(builder => builder.Add(p => p.Content, content));
        await loadingStarted.Task.WaitAsync(DefaultWaitTimeout);

        Assert.True(cut.Find("fluent-combobox").HasAttribute("disabled"));
        Assert.Single(cut.FindAll("fluent-combobox + fluent-progress-ring"));

        fieldValues.SetResult(new Dictionary<string, int> { ["request"] = 1 });

        cut.WaitForAssertion(() =>
        {
            Assert.False(cut.Find("fluent-combobox").HasAttribute("disabled"));
            Assert.Empty(cut.FindAll("fluent-combobox + fluent-progress-ring"));
        });
    }

    [Fact]
    public async Task Render_ChangingStringFieldAfterValuesLoad_LoadsNewValues()
    {
        SetupFilterDialogServices();
        var loadedFields = new List<string>();
        var content = new FilterDialogViewModel
        {
            Filter = new FieldTelemetryFilter
            {
                Field = KnownTraceFields.NameField,
                Condition = FilterCondition.Contains,
                Value = "request"
            },
            KnownKeys = [KnownTraceFields.NameField, KnownTraceFields.TraceIdField],
            GetPropertyKeysAsync = static _ => Task.FromResult<List<string>>([]),
            GetFieldValuesAsync = (field, _) =>
            {
                loadedFields.Add(field);
                return Task.FromResult<Dictionary<string, int>>([]);
            }
        };

        var cut = RenderComponent<FilterDialog>(builder => builder.Add(p => p.Content, content));
        var parameterSelect = cut.FindComponent<FluentSelect<SelectViewModel<string>>>();
        var traceIdOption = parameterSelect.Instance.Items!.Single(item => item.Id == KnownTraceFields.TraceIdField);

        await parameterSelect.InvokeAsync(() => parameterSelect.Instance.SelectedOptionChanged.InvokeAsync(traceIdOption));

        Assert.Collection(loadedFields,
            field => Assert.Equal(KnownTraceFields.NameField, field),
            field => Assert.Equal(KnownTraceFields.TraceIdField, field));
        Assert.False(cut.Find("fluent-combobox").HasAttribute("disabled"));
    }

    [Fact]
    public async Task Render_FieldValueReadsCompleteOutOfOrder_LatestValuesDisplayed()
    {
        SetupFilterDialogServices();
        var firstLoadStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondLoadStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstFieldValues = new TaskCompletionSource<Dictionary<string, int>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondFieldValues = new TaskCompletionSource<Dictionary<string, int>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var content = new FilterDialogViewModel
        {
            Filter = new FieldTelemetryFilter
            {
                Field = KnownTraceFields.NameField,
                Condition = FilterCondition.Contains,
                Value = "request"
            },
            KnownKeys = [KnownTraceFields.NameField, KnownTraceFields.TraceIdField, KnownTraceFields.SpanIdField],
            GetPropertyKeysAsync = static _ => Task.FromResult<List<string>>([]),
            GetFieldValuesAsync = (field, _) => field switch
            {
                KnownTraceFields.NameField => Task.FromResult<Dictionary<string, int>>([]),
                KnownTraceFields.TraceIdField => StartLoad(firstLoadStarted, firstFieldValues),
                KnownTraceFields.SpanIdField => StartLoad(secondLoadStarted, secondFieldValues),
                _ => throw new InvalidOperationException($"Unexpected field '{field}'.")
            }
        };

        var cut = RenderComponent<FilterDialog>(builder => builder.Add(p => p.Content, content));
        var parameterSelect = cut.FindComponent<FluentSelect<SelectViewModel<string>>>();
        var traceIdOption = parameterSelect.Instance.Items!.Single(item => item.Id == KnownTraceFields.TraceIdField);
        var spanIdOption = parameterSelect.Instance.Items!.Single(item => item.Id == KnownTraceFields.SpanIdField);

        var firstChangeTask = parameterSelect.InvokeAsync(() => parameterSelect.Instance.SelectedOptionChanged.InvokeAsync(traceIdOption));
        await firstLoadStarted.Task.WaitAsync(DefaultWaitTimeout);
        var secondChangeTask = parameterSelect.InvokeAsync(() => parameterSelect.Instance.SelectedOptionChanged.InvokeAsync(spanIdOption));
        await secondLoadStarted.Task.WaitAsync(DefaultWaitTimeout);

        secondFieldValues.SetResult(new Dictionary<string, int> { ["latest-value"] = 1 });
        await secondChangeTask;
        firstFieldValues.SetResult(new Dictionary<string, int> { ["stale-value"] = 1 });
        await firstChangeTask;

        cut.WaitForAssertion(() =>
        {
            var options = cut.Find("fluent-combobox").QuerySelectorAll("fluent-option");
            var option = Assert.Single(options);
            Assert.Contains("latest-value", option.TextContent, StringComparison.Ordinal);
        });

        static Task<Dictionary<string, int>> StartLoad(
            TaskCompletionSource loadStarted,
            TaskCompletionSource<Dictionary<string, int>> fieldValues)
        {
            loadStarted.SetResult();
            return fieldValues.Task;
        }
    }

    [Fact]
    public async Task Render_PropertyKeysReadFails_ClearsLoadingState()
    {
        SetupFilterDialogServices();
        var loadingStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var propertyKeys = new TaskCompletionSource<List<string>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var content = CreateContent(new FieldTelemetryFilter
        {
            Field = KnownTraceFields.NameField,
            Condition = FilterCondition.Contains,
            Value = "request"
        });
        content = new FilterDialogViewModel
        {
            Filter = content.Filter,
            KnownKeys = content.KnownKeys,
            GetPropertyKeysAsync = _ =>
            {
                loadingStarted.SetResult();
                return propertyKeys.Task;
            },
            GetFieldValuesAsync = content.GetFieldValuesAsync
        };

        var cut = RenderComponent<FilterDialog>(builder => builder.Add(p => p.Content, content));
        await loadingStarted.Task.WaitAsync(DefaultWaitTimeout);
        Assert.Single(cut.FindComponents<FluentProgressRing>());

        propertyKeys.SetException(new InvalidOperationException("Database read failed."));

        // A failed read must still clear the loading state. Otherwise the parameter select stays disabled behind a
        // spinner for the life of the dialog and the user cannot pick a different field.
        cut.WaitForAssertion(() =>
        {
            Assert.False(cut.FindComponent<FluentSelect<SelectViewModel<string>>>().Instance.Disabled);
            Assert.Empty(cut.FindComponents<FluentProgressRing>());
        });
    }

    [Fact]
    public async Task Render_FieldValuesReadFails_ClearsLoadingState()
    {
        SetupFilterDialogServices();
        var loadingStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var fieldValues = new TaskCompletionSource<Dictionary<string, int>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var content = CreateContent(new FieldTelemetryFilter
        {
            Field = KnownTraceFields.NameField,
            Condition = FilterCondition.Contains,
            Value = "request"
        });
        content = new FilterDialogViewModel
        {
            Filter = content.Filter,
            KnownKeys = content.KnownKeys,
            GetPropertyKeysAsync = content.GetPropertyKeysAsync,
            GetFieldValuesAsync = (_, _) =>
            {
                loadingStarted.SetResult();
                return fieldValues.Task;
            }
        };

        var cut = RenderComponent<FilterDialog>(builder => builder.Add(p => p.Content, content));
        await loadingStarted.Task.WaitAsync(DefaultWaitTimeout);
        Assert.True(cut.Find("fluent-combobox").HasAttribute("disabled"));

        fieldValues.SetException(new InvalidOperationException("Database read failed."));

        cut.WaitForAssertion(() =>
        {
            Assert.False(cut.Find("fluent-combobox").HasAttribute("disabled"));
            Assert.Empty(cut.FindAll("fluent-combobox + fluent-progress-ring"));
        });
    }

    [Fact]
    public async Task DisposeAsync_InFlightRead_CancelsToken()
    {
        SetupFilterDialogServices();
        var loadingStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var propertyKeys = new TaskCompletionSource<List<string>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var readCancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var content = CreateContent(new FieldTelemetryFilter
        {
            Field = KnownTraceFields.NameField,
            Condition = FilterCondition.Contains,
            Value = "request"
        });
        content = new FilterDialogViewModel
        {
            Filter = content.Filter,
            KnownKeys = content.KnownKeys,
            GetPropertyKeysAsync = cancellationToken =>
            {
                cancellationToken.Register(() => readCancelled.TrySetResult());
                loadingStarted.SetResult();
                return propertyKeys.Task;
            },
            GetFieldValuesAsync = content.GetFieldValuesAsync
        };

        var cut = RenderComponent<FilterDialog>(builder => builder.Add(p => p.Content, content));
        await loadingStarted.Task.WaitAsync(DefaultWaitTimeout);

        // Telemetry reads run against SQLite on the thread pool. Closing the dialog must cancel them so a scan
        // started for a dialog nobody is looking at does not keep running.
        await cut.Instance.DisposeAsync();

        await readCancelled.Task.WaitAsync(DefaultWaitTimeout);

        // Complete the read so the component's initialization task does not stay pending past the test.
        propertyKeys.SetResult([]);
    }

    private void SetupFilterDialogServices()
    {
        FluentUISetupHelpers.AddCommonDashboardServices(this);
        FluentUISetupHelpers.SetupFluentUIComponents(this);
        FluentUISetupHelpers.SetupFluentInputLabel(this);
        FluentUISetupHelpers.SetupFluentTextField(this);
        FluentUISetupHelpers.SetupFluentButton(this);
        FluentUISetupHelpers.SetupFluentList(this);
        FluentUISetupHelpers.SetupFluentCombobox(this);
    }

    private static FilterDialogViewModel CreateContent(FieldTelemetryFilter filter)
    {
        return new FilterDialogViewModel
        {
            Filter = filter,
            KnownKeys = [KnownTraceFields.NameField, KnownTraceFields.DurationField],
            GetPropertyKeysAsync = static _ => Task.FromResult<List<string>>([]),
            GetFieldValuesAsync = static (field, _) => Task.FromResult(field == KnownTraceFields.NameField
                ? new Dictionary<string, int> { ["request"] = 1 }
                : [])
        };
    }
}
