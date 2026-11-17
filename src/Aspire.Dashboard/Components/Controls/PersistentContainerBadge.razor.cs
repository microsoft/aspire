// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Model;
using Aspire.Dashboard.Resources;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Localization;

namespace Aspire.Dashboard.Components.Controls;

public partial class PersistentContainerBadge
{
    [Inject]
    public required IStringLocalizer<Columns> Loc { get; init; }

    [Parameter, EditorRequired]
    public required ResourceViewModel Resource { get; set; }
}
