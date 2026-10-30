// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Shared;

namespace Aspire.Dashboard.Model.ResourceGraph;

/// <summary>
/// Exports the visible resource graph as a Mermaid flowchart.
/// </summary>
internal static class ResourceGraphMermaidExporter
{
    public static string Export(IEnumerable<ResourceDto> resources) =>
        MermaidGraphExporter.Export(resources, r => r.Name, r => r.DisplayName, r => r.ReferencedNames);
}
