// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using Aspire.Dashboard.Components.Controls;

using Aspire.Shared;

namespace Aspire.Dashboard.Model;

internal static class ResourceUrlHelpers
{
    public static List<DisplayedUrl> GetUrls(ResourceViewModel resource, bool includeInternalUrls = false, bool includeNonEndpointUrls = false)
    {
        var urls = new List<DisplayedUrl>(resource.Urls.Length);

        var index = 0;
        foreach (var url in resource.Urls)
        {
            if ((includeInternalUrls && url.IsInternal) || !url.IsInternal)
            {
                if (url.IsInactive)
                {
                    continue;
                }

                if (!includeNonEndpointUrls && string.IsNullOrEmpty(url.EndpointName))
                {
                    continue;
                }

                urls.Add(new DisplayedUrl
                {
                    Index = index,
                    Name = url.EndpointName ?? "-",
                    Address = url.Url.Host,
                    Port = url.Url.Port,
                    Url = !KnownUnsupportedUrlSchemes.IsUnsupportedScheme(url.Url.Scheme) ? url.Url.OriginalString : null,
                    SortOrder = url.DisplayProperties.SortOrder,
                    DisplayName = string.IsNullOrEmpty(url.DisplayProperties.DisplayName) ? null : url.DisplayProperties.DisplayName,
                    OriginalUrlString = url.Url.OriginalString,
                    Text = string.IsNullOrEmpty(url.DisplayProperties.DisplayName) ? url.Url.OriginalString : url.DisplayProperties.DisplayName
                });
                index++;
            }
        }

        // Make sure that URLs have a consistent ordering.
        // Order:
        // - https
        // - other urls
        // - endpoint name
        var orderedUrls = urls
            .OrderByDescending(e => e.SortOrder)
            .ThenByDescending(e => e.Url?.StartsWith("https") == true)
            .ThenByDescending(e => e.Url is not null)
            .ThenBy(e => e.Name, StringComparers.EndpointAnnotationName)
            .ToList();

        return orderedUrls;
    }
}

[DebuggerDisplay("Name = {Name}, Text = {Text}, Address = {Address}:{Port}, Url = {Url}, DisplayName = {DisplayName}, OriginalUrlString = {OriginalUrlString}, SortOrder = {SortOrder}")]
public sealed class DisplayedUrl : IPropertyGridItem
{
    public required int Index { get; set; }
    public required string Name { get; set; }
    public required string Text { get; set; }
    public string? Address { get; set; }
    public int? Port { get; set; }
    public string? Url { get; set; }
    public int SortOrder { get; set; }
    public string? DisplayName { get; set; }
    public required string OriginalUrlString { get; set; }

    /// <summary>
    /// Don't display a plain string value here. The URL will be displayed as a hyperlink
    /// in <see cref="ResourceDetails.RenderAddressValue(DisplayedUrl, string)"/> instead.
    /// </summary>
    string? IPropertyGridItem.Value => null;

    object IPropertyGridItem.Key => Index;

    public string? ValueToVisualize => Url ?? Text;

    public bool MatchesFilter(string filter)
        => Url?.Contains(filter, StringComparison.CurrentCultureIgnoreCase) == true ||
           Text.Contains(filter, StringComparison.CurrentCultureIgnoreCase) ||
           Name.Contains(filter, StringComparison.CurrentCultureIgnoreCase) ||
           DisplayName?.Contains(filter, StringComparison.CurrentCultureIgnoreCase) == true;
}
