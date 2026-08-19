// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

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

        Assert.Single(cut.FindComponents<FluentDialogBody>());
        Assert.Single(cut.FindComponents<FluentNumberInput<double?>>());
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
        Assert.DoesNotContain("TODO: Restore Immediate/ImmediateDelay", cut.Markup);

        var valueOption = cut.Find("fluent-option[text='request']");
        var countBadge = Assert.Single(valueOption.QuerySelectorAll("fluent-badge:not([slot])"));
        Assert.Same(countBadge, valueOption.LastElementChild);
        Assert.Single(countBadge.QuerySelectorAll("[data-filtercount='1']"));

        Assert.Contains(JSInterop.Invocations, invocation =>
            invocation.Identifier == "Microsoft.FluentUI.Blazor.Components.Select.Initialize" &&
            invocation.Arguments.Count == 2 &&
            Equals(invocation.Arguments[1], "request"));

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

        await cut.Find("fluent-dropdown[type='combobox']").InputAsync(new ChangeEventArgs { Value = "response" });

        var valueCombobox = cut.Find("fluent-dropdown[type='combobox']");
        var valueOption = Assert.Single(valueCombobox.QuerySelectorAll("fluent-option"));
        Assert.Equal("response", valueOption.GetAttribute("text"));
        Assert.Equal("response", Assert.Single(valueOption.QuerySelectorAll("mark")).TextContent);
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
            PropertyKeys = [],
            GetFieldValues = field => field == KnownTraceFields.NameField
                ? new Dictionary<string, int> { ["request"] = 1, ["response"] = 2 }
                : []
        };
    }
}
