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

        var conditionSelect = Assert.Single(cut.FindComponents<FluentSelect<SelectViewModel<FilterCondition>>>());
        Assert.Collection(conditionSelect.Instance.Items!,
            item => Assert.Equal(FilterCondition.Equals, item.Id),
            item => Assert.Equal(FilterCondition.Contains, item.Id),
            item => Assert.Equal(FilterCondition.NotEqual, item.Id),
            item => Assert.Equal(FilterCondition.NotContains, item.Id));
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
                ? new Dictionary<string, int> { ["request"] = 1 }
                : []
        };
    }
}
