// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Dashboard.Extensions;

internal static class ClipboardExtensions
{
    public static Dictionary<string, object> GetClipboardCopyAdditionalAttributes(string? text, string? precopy, string? postcopy, params (string Attribute, object Value)[] additionalAttributes)
    {
        // No onclick attribute is added here. The CSP restricts inline scripts, including onclick.
        // Instead, a click event listener is added to the document and clicking the button is bubbled up to the event.
        // The document click listener looks for a button element and these attributes.
        var attributes = new Dictionary<string, object>(StringComparers.HtmlAttribute)
        {
            { "data-text", text ?? string.Empty },
            { "data-precopy", precopy ?? string.Empty },
            { "data-postcopy", postcopy ?? string.Empty },
            { "data-copybutton", "true" }
        };

        foreach (var (attribute, value) in additionalAttributes)
        {
            attributes.Add(attribute, value);
        }

        return attributes;
    }
}
