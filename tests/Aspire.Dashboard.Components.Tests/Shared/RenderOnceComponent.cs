// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;

namespace Aspire.Dashboard.Components.Tests.Shared;

// Model a routing boundary whose unchanged parameters do not cause its content to render again.
public sealed class RenderOnceComponent : ComponentBase
{
    [Parameter]
    public required RenderFragment ChildContent { get; set; }

    protected override bool ShouldRender() => false;

    protected override void BuildRenderTree(RenderTreeBuilder builder) => builder.AddContent(0, ChildContent);
}
