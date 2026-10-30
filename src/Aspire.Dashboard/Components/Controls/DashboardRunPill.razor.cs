// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Model;
using Aspire.Dashboard.Utils;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Localization;
using LayoutResources = Aspire.Dashboard.Resources.Layout;

namespace Aspire.Dashboard.Components.Controls;

public partial class DashboardRunPill : ComponentBase
{
    private string RunText => FormatHelpers.FormatDateTime(TimeProvider, SelectedRunStartedAtUtc.UtcDateTime);
    private string Title => Loc[nameof(LayoutResources.DashboardRunSelectTitle)];
    private string AccessibleLabel => Loc[nameof(LayoutResources.DashboardRunSelectAccessibleLabel), RunText];

    [Parameter]
    public DateTimeOffset SelectedRunStartedAtUtc { get; set; }

    [Parameter]
    public EventCallback OnClick { get; set; }

    [Inject]
    public required IStringLocalizer<LayoutResources> Loc { get; init; }

    [Inject]
    public required BrowserTimeProvider TimeProvider { get; init; }
}
