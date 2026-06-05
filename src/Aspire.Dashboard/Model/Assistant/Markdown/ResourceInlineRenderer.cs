// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Encodings.Web;
using Aspire.Dashboard.Utils;
using Markdig.Renderers;
using Markdig.Renderers.Html;

namespace Aspire.Dashboard.Model.Assistant.Markdown;

public class ResourceInlineRenderer : HtmlObjectRenderer<ResourceInline>
{
    protected override void Write(HtmlRenderer renderer, ResourceInline inline)
    {
        var color = ColorGenerator.Instance.GetColorVariableByKey(inline.ResourceName);
        var encodedResourceName = HtmlEncoder.Default.Encode(inline.ResourceName);
        renderer.Write($@"<a href=""{DashboardUrls.ResourcesUrl(inline.Resource.Name)}"" class=""resource-name"" style=""border-left-color: {color};"">{encodedResourceName}</a>");
    }
}
