// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Localization;

namespace Aspire.Dashboard.Components.Pages;

public partial class Testing
{
    [Parameter]
    public string? ResourceName { get; set; }

    [Inject]
    public required IStringLocalizer<Dashboard.Resources.Layout> Loc { get; init; }
}
