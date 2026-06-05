// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;

namespace Aspire.Hosting.ApplicationModel;

/// <summary>
/// A URL that should be displayed for a resource.
/// </summary>
[DebuggerDisplay("Url = {Url}, DisplayText = {DisplayText}")]
[AspireDto]
public sealed class ResourceUrlAnnotation : IResourceAnnotation
{
    /// <summary>
    /// The URL. When rendered as a link this will be used as the link target.
    /// </summary>
    public required string Url { get; set; }

    /// <summary>
    /// The name of the URL. When rendered as a link this will be used as the linked text.
    /// </summary>
    public string? DisplayText { get; set; }

    /// <summary>
    /// The endpoint associated with this URL. Can be <c>null</c> if this URL is not associated with an endpoint.
    /// </summary>
    public EndpointReference? Endpoint { get; init; }

    /// <summary>
    /// The display order the URL. Higher values mean sort higher in the list.
    /// </summary>
    public int? DisplayOrder;

    /// <summary>
    /// Locations where this URL should be shown on the dashboard. Defaults to <see cref="UrlDisplayLocation.SummaryAndDetails"/>.
    /// </summary>
    public UrlDisplayLocation DisplayLocation { get; set; } = UrlDisplayLocation.SummaryAndDetails;

    internal bool IsInternal => DisplayLocation == UrlDisplayLocation.DetailsOnly;

    internal ResourceUrlAnnotation WithEndpoint(EndpointReference endpoint)
    {
        return new()
        {
            Url = Url,
            DisplayText = DisplayText,
            Endpoint = endpoint,
            DisplayOrder = DisplayOrder,
            DisplayLocation = DisplayLocation
        };
    }
}

/// <summary>
/// Specifies where the URL should be displayed.
/// </summary>
public enum UrlDisplayLocation
{
    /// <summary>
    /// Show the URL in locations where either the resource summary or resource details are being displayed.
    /// </summary>
    SummaryAndDetails,
    /// <summary>
    /// Show the URL in locations where the full details of the resource are being displayed.
    /// </summary>
    DetailsOnly
}
