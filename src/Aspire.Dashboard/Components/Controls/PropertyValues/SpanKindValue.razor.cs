// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Components.Deck;
using Aspire.Dashboard.Model;
using Aspire.Dashboard.Otlp.Model;
using Microsoft.AspNetCore.Components;

namespace Aspire.Dashboard.Components.Controls.PropertyValues;

public partial class SpanKindValue
{
    private DeckIconName? _icon;

    [Parameter, EditorRequired]
    public required string Value { get; set; }

    [Parameter, EditorRequired]
    public required string HighlightText { get; set; }

    [Parameter, EditorRequired]
    public required OtlpSpan Span { get; set; }

    protected override void OnParametersSet()
    {
        _icon = TraceHelpers.TryGetSpanIcon(Span);
    }
}
