// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.AspNetCore.Components;

namespace Aspire.Dashboard.Components.Controls;

public partial class EmptyStateDocumentation
{
    [Parameter, EditorRequired]
    public required string Message { get; set; }

    [Parameter, EditorRequired]
    public EmptyStateDocumentationPanel Panel { get; set; }

    internal EmptyStateDocumentationArticle Article => EmptyStateDocumentationLinks.GetArticle(Panel);
}
