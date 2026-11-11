// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Model;
using Aspire.Dashboard.Resources;
using Aspire.Dashboard.Utils;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Localization;

namespace Aspire.Dashboard.Components;

public partial class UrlsColumnDisplay
{
    private const int MaxRenderedOverflowItems = 20;

    // Credentials (redis://:password@host), fragments (#access_token=...) and query values (?token=...) can carry
    // secrets, so the address is shown without the first two and with the values masked, as the resource overview does
    // (see DashboardUIHelpers.MaskUrlForDisplay).
    internal static string GetTooltipText(DisplayedUrl displayedUrl)
    {
        return DashboardUIHelpers.MaskUrlForDisplay(displayedUrl.Url ?? displayedUrl.OriginalUrlString);
    }

    // A URL's display name is shown as is. Without one, the text is the address, masked like the tooltip.
    internal static string GetDisplayText(DisplayedUrl displayedUrl)
    {
        return string.IsNullOrEmpty(displayedUrl.DisplayName) ? DashboardUIHelpers.MaskUrlForDisplay(displayedUrl.Text) : displayedUrl.Text;
    }

    [Parameter, EditorRequired]
    public required ResourceViewModel Resource { get; set; }

    [Parameter, EditorRequired]
    public required bool HasMultipleReplicas { get; set; }

    [Parameter, EditorRequired]
    public required IList<DisplayedUrl> DisplayedUrls { get; set; }

    /// <summary>
    /// Gets or sets whether only the first URL is shown, followed by a "+N" button that lists the others. Without it,
    /// as many URLs as fit are shown. The resource list uses it so a resource with many URLs doesn't need a wider
    /// column than the others.
    /// </summary>
    [Parameter]
    public bool ShowFirstUrlOnly { get; set; }

    [Inject]
    public required IStringLocalizer<Columns> Loc { get; init; }

    private bool _popoverVisible;

    // The "+N" button's ID, which is also the popover's anchor name. It's unique per instance rather than per resource:
    // a resource with several tags is listed once in each tag group, so an ID built from its name would repeat, and the
    // popover could anchor to another group's copy of the button instead of the one that was clicked.
    private readonly string _moreButtonId = $"urls-more-{Interlocked.Increment(ref s_nextMoreButtonId)}";

    private static int s_nextMoreButtonId;
}
