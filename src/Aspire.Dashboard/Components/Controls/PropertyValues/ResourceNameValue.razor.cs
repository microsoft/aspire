// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Model;
using Microsoft.AspNetCore.Components;

namespace Aspire.Dashboard.Components.Controls.PropertyValues;

public partial class ResourceNameValue
{
    [Parameter, EditorRequired]
    public required string Value { get; set; }

    [Parameter]
    public string? HighlightText { get; set; }

    [Parameter, EditorRequired]
    public required ResourceViewModel Resource { get; set; }

    // Colors are keyed by the target name so a resource matches its telemetry, whatever label it displays.
    [Parameter, EditorRequired]
    public required Func<ResourceViewModel, string> GetColorKey { get; set; }
}
