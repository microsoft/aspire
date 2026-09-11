// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Components.Controls;
using Aspire.Dashboard.Components.Dialogs;
using Aspire.Dashboard.Components.Tests.Shared;
using Aspire.Dashboard.Model;
using Aspire.Dashboard.Model.Otlp;
using Bunit;
using Microsoft.AspNetCore.Components;
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

        Assert.Equal("aspire-input", Assert.Single(cut.FindComponents<FluentNumberInput<int?>>()).Instance.Class);
        Assert.DoesNotContain("fluent-combobox", cut.Markup);

        var conditionSelect = Assert.Single(cut.FindComponents<FluentSelect<SelectViewModel<FilterCondition>, SelectViewModel<FilterCondition>>>());
        Assert.Collection(conditionSelect.Instance.Items!,
            item => Assert.Equal(FilterCondition.Equals, item.Id),
            item => Assert.Equal(FilterCondition.NotEqual, item.Id),
            item => Assert.Equal(FilterCondition.GreaterThanOrEqual, item.Id),
            item => Assert.Equal(FilterCondition.GreaterThan, item.Id),
            item => Assert.Equal(FilterCondition.LessThanOrEqual, item.Id),
            item => Assert.Equal(FilterCondition.LessThan, item.Id));
    }

    [Theory]
    [InlineData("50", 50)]
    [InlineData("50.5", null)]
    public void Render_DurationFilter_DisplaysOnlyIntegerValues(string value, int? expectedValue)
    {
        SetupFilterDialogServices();

        var cut = RenderComponent<FilterDialog>(builder =>
        {
            builder.Add(p => p.Content, CreateContent(new FieldTelemetryFilter
            {
                Field = KnownTraceFields.DurationField,
                Condition = FilterCondition.GreaterThanOrEqual,
                Value = value
            }));
        });

        Assert.Equal(expectedValue, Assert.Single(cut.FindComponents<FluentNumberInput<int?>>()).Instance.Value);
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

        Assert.Empty(cut.FindComponents<FluentNumberInput<double?>>());
        Assert.Contains("fluent-dropdown", cut.Markup);
        Assert.Equal("request", Assert.IsType<FilterDialogFormModel>(cut.Instance.EditContext.Model).Value);

        var valueOption = cut.Find("fluent-option[text='request']");
        var countBadge = Assert.Single(valueOption.QuerySelectorAll(":scope > fluent-badge[slot='description']"));
        Assert.Same(countBadge, valueOption.LastElementChild);
        Assert.Single(countBadge.QuerySelectorAll("[data-filtercount='1']"));

        var parameterSelect = Assert.Single(cut.FindComponents<FluentSelect<SelectViewModel<string>, SelectViewModel<string>>>());
        Assert.Null(parameterSelect.Instance.OptionText!(null));
        Assert.False(parameterSelect.Instance.OptionDisabled!(null));

        var conditionSelect = Assert.Single(cut.FindComponents<FluentSelect<SelectViewModel<FilterCondition>, SelectViewModel<FilterCondition>>>());
        Assert.Null(conditionSelect.Instance.OptionText!(null));
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

        Assert.True(cut.FindComponent<FluentSelect<SelectViewModel<string>, SelectViewModel<string>>>().Instance.Disabled);
        Assert.Equal("20px", Assert.Single(cut.FindComponents<AspireProgressRing>()).Instance.Width);
        Assert.NotNull(cut.Find(".input-line-container .input-progress"));

        propertyKeys.SetResult(["custom.attribute"]);

        cut.WaitForAssertion(() =>
        {
            var parameterSelect = cut.FindComponent<FluentSelect<SelectViewModel<string>, SelectViewModel<string>>>();
            Assert.False(parameterSelect.Instance.Disabled);
            Assert.Empty(cut.FindComponents<AspireProgressRing>());
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

        var valueCombobox = cut.Find("fluent-dropdown[type='combobox']");
        Assert.True(valueCombobox.HasAttribute("disabled"));
        var progressRings = cut.FindComponents<AspireProgressRing>();
        Assert.NotEmpty(progressRings);
        Assert.All(progressRings, progressRing => Assert.Equal("20px", progressRing.Instance.Width));

        fieldValues.SetResult(new Dictionary<string, int> { ["request"] = 1 });

        cut.WaitForAssertion(() =>
        {
            var updatedValueCombobox = cut.Find("fluent-dropdown[type='combobox']");
            Assert.False(updatedValueCombobox.HasAttribute("disabled"));
            Assert.Empty(cut.FindComponents<AspireProgressRing>());
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
        var parameterSelect = cut.FindComponent<FluentSelect<SelectViewModel<string>, SelectViewModel<string>>>();
        var traceIdOption = parameterSelect.Instance.Items!.Single(item => item.Id == KnownTraceFields.TraceIdField);

        await parameterSelect.InvokeAsync(() => parameterSelect.Instance.ValueChanged.InvokeAsync(traceIdOption));

        Assert.Collection(loadedFields,
            field => Assert.Equal(KnownTraceFields.NameField, field),
            field => Assert.Equal(KnownTraceFields.TraceIdField, field));
        Assert.False(cut.Find("fluent-dropdown[type='combobox']").HasAttribute("disabled"));
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
        var parameterSelect = cut.FindComponent<FluentSelect<SelectViewModel<string>, SelectViewModel<string>>>();
        var traceIdOption = parameterSelect.Instance.Items!.Single(item => item.Id == KnownTraceFields.TraceIdField);
        var spanIdOption = parameterSelect.Instance.Items!.Single(item => item.Id == KnownTraceFields.SpanIdField);

        var firstChangeTask = parameterSelect.InvokeAsync(() => parameterSelect.Instance.ValueChanged.InvokeAsync(traceIdOption));
        await firstLoadStarted.Task.WaitAsync(DefaultWaitTimeout);
        var secondChangeTask = parameterSelect.InvokeAsync(() => parameterSelect.Instance.ValueChanged.InvokeAsync(spanIdOption));
        await secondLoadStarted.Task.WaitAsync(DefaultWaitTimeout);

        secondFieldValues.SetResult(new Dictionary<string, int> { ["latest-value"] = 1 });
        await secondChangeTask;
        firstFieldValues.SetResult(new Dictionary<string, int> { ["stale-value"] = 1 });
        await firstChangeTask;

        cut.WaitForAssertion(() =>
        {
            var options = cut.Find("fluent-dropdown[type='combobox']").QuerySelectorAll("fluent-option:not([freeform])");
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
        Assert.Single(cut.FindComponents<AspireProgressRing>());

        propertyKeys.SetException(new InvalidOperationException("Database read failed."));

        // A failed read must still clear the loading state. Otherwise the parameter select stays disabled behind a
        // spinner for the life of the dialog and the user cannot pick a different field.
        cut.WaitForAssertion(() =>
        {
            Assert.False(cut.FindComponent<FluentSelect<SelectViewModel<string>, SelectViewModel<string>>>().Instance.Disabled);
            Assert.Empty(cut.FindComponents<AspireProgressRing>());
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
        Assert.True(cut.Find("fluent-dropdown[type='combobox']").HasAttribute("disabled"));

        fieldValues.SetException(new InvalidOperationException("Database read failed."));

        cut.WaitForAssertion(() =>
        {
            Assert.False(cut.Find("fluent-dropdown[type='combobox']").HasAttribute("disabled"));
            Assert.Empty(cut.FindAll("fluent-dropdown[type='combobox'] + .aspire-progress-ring"));
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

    [Fact]
    public async Task Render_StringFilter_TypingFiltersAndHighlightsOptions()
    {
        SetupFilterDialogServices();

        var cut = RenderComponent<FilterDialog>(builder =>
        {
            builder.Add(p => p.Content, CreateContent(new FieldTelemetryFilter
            {
                Field = KnownTraceFields.NameField,
                Condition = FilterCondition.Contains,
                Value = ""
            }));
        });

        await cut.Find("fluent-dropdown[type='combobox']").TriggerEventAsync("ontextimmediate", new ChangeEventArgs { Value = "response" });

        var valueCombobox = cut.Find("fluent-dropdown[type='combobox']");
        var valueOption = Assert.Single(valueCombobox.QuerySelectorAll("fluent-option:not([freeform])"));
        Assert.Equal("response", valueOption.GetAttribute("text"));
        Assert.Equal("response", Assert.Single(valueOption.QuerySelectorAll("mark")).TextContent);
    }

    [Theory]
    [InlineData("", "custom value")]
    [InlineData("request", "res")]
    [InlineData("request", "")]
    public async Task Render_StringFilter_TypingThenLosingFocus_PreservesValue(string initialValue, string typedValue)
    {
        SetupFilterDialogServices();

        var cut = RenderComponent<FilterDialog>(builder =>
        {
            builder.Add(p => p.Content, CreateContent(new FieldTelemetryFilter
            {
                Field = KnownTraceFields.NameField,
                Condition = FilterCondition.Contains,
                Value = initialValue
            }));
        });

        Assert.Single(cut.FindAll("#filter-dialog-text-value fluent-option[freeform] output"));

        await cut.Find("#filter-dialog-text-value").FocusInAsync(new());
        await cut.Find("#filter-dialog-text-value").TriggerEventAsync("ontextimmediate", new ChangeEventArgs { Value = typedValue });
        await cut.Find("#filter-dialog-text-value").TriggerEventAsync("ondropdownchange", new DropdownEventArgs { SelectedOptions = "" });
        await cut.Find("#filter-dialog-text-value").FocusOutAsync(new());

        Assert.Equal(typedValue, Assert.IsType<FilterDialogFormModel>(cut.Instance.EditContext.Model).Value);
    }

    [Fact]
    public void Render_DateFilterWithInvalidValue_DisplaysSingleValidationMessage()
    {
        SetupFilterDialogServices();

        var cut = RenderComponent<FilterDialog>(builder =>
        {
            builder.Add(p => p.Content, CreateContent(new FieldTelemetryFilter
            {
                Field = KnownTraceFields.TimestampField,
                Condition = FilterCondition.GreaterThanOrEqual,
                Value = "not-a-date"
            }));
        });

        Assert.Equal("off", Assert.Single(cut.FindComponents<FluentTextInput>()).Instance.AutoComplete);

        cut.Find("form").Submit();

        var validationMessage = Assert.Single(cut.FindAll(".validation-message, .fluent-validation-message"));
        Assert.Equal("Invalid date format", validationMessage.TextContent.Trim());
    }

    [Fact]
    public void Render_DateFilter_DisplaysDatePickerButton()
    {
        SetupFilterDialogServices();

        var cut = RenderComponent<FilterDialog>(builder =>
        {
            builder.Add(p => p.Content, CreateContent(new FieldTelemetryFilter
            {
                Field = KnownTraceFields.TimestampField,
                Condition = FilterCondition.GreaterThanOrEqual,
                Value = "2024-01-15T09:30:00"
            }));
        });

        var datePickerButton = Assert.Single(cut.FindComponents<FluentButton>(), button => button.Instance.Title == "Pick date and time");
        Assert.True(datePickerButton.Instance.IconOnly);
        Assert.Equal(ButtonType.Button, datePickerButton.Instance.Type);
        Assert.Contains("aspire-icon-button", datePickerButton.Instance.Class);
    }

    [Fact]
    public void Render_StringFilterWithoutValue_MarksValueFieldInvalid()
    {
        SetupFilterDialogServices();

        var cut = RenderComponent<FilterDialog>(builder =>
        {
            builder.Add(p => p.Content, CreateContent(new FieldTelemetryFilter
            {
                Field = KnownTraceFields.NameField,
                Condition = FilterCondition.Contains,
                Value = ""
            }));
        });

        cut.Find("form").Submit();

        var validationMessage = Assert.Single(cut.FindAll(".fluent-validation-message"));
        Assert.Equal("A value is required.", validationMessage.TextContent.Trim());
        var valueField = Assert.Single(cut.FindAll("#filter-dialog-text-value-field"));
        Assert.Contains("invalid", valueField.ClassList);
        Assert.Equal("filter-dialog-text-value", valueField.QuerySelector(":scope > [slot='input']")?.Id);
    }

    [Fact]
    public async Task Render_StringFilterWithValidationError_TypingValueClearsError()
    {
        SetupFilterDialogServices();

        var cut = RenderComponent<FilterDialog>(builder =>
        {
            builder.Add(p => p.Content, CreateContent(new FieldTelemetryFilter
            {
                Field = KnownTraceFields.NameField,
                Condition = FilterCondition.Contains,
                Value = ""
            }));
        });

        cut.Find("form").Submit();
        Assert.Single(cut.FindAll(".fluent-validation-message"));

        await cut.Find("#filter-dialog-text-value").TriggerEventAsync("ontextimmediate", new ChangeEventArgs { Value = "response" });

        Assert.Empty(cut.FindAll(".fluent-validation-message"));
        Assert.Equal("my-3-o aspire-input", Assert.Single(cut.FindAll("#filter-dialog-text-value-field")).ClassName);
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
        JSInterop.SetupModule("./Components/Dialogs/FilterDialog.razor.js").SetupVoid("ensureComboboxControl", _ => true);
    }

    private static FilterDialogViewModel CreateContent(FieldTelemetryFilter filter)
    {
        return new FilterDialogViewModel
        {
            Filter = filter,
            KnownKeys = [KnownTraceFields.NameField, KnownTraceFields.DurationField],
            GetPropertyKeysAsync = static _ => Task.FromResult<List<string>>([]),
            GetFieldValuesAsync = static (field, _) => Task.FromResult(field == KnownTraceFields.NameField
                ? new Dictionary<string, int> { ["request"] = 1, ["response"] = 2 }
                : [])
        };
    }
}
