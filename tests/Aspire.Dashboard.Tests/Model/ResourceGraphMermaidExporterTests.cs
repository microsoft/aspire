// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Model.ResourceGraph;
using VerifyXunit;
using Xunit;

namespace Aspire.Dashboard.Tests.Model;

public class ResourceGraphMermaidExporterTests
{
    [Fact]
    public Task Export_ResourcesAndConnections()
    {
        var resources = new[]
        {
            CreateResource("web", "web", "api-2", "api-1", "api-1", "filtered"),
            CreateResource("api-2", "api (2)", "cache"),
            CreateResource("isolated", "isolated"),
            CreateResource("cache", "cache"),
            CreateResource("api-1", "api (1)", "cache", "web")
        };

        var diagram = ResourceGraphMermaidExporter.Export(resources);

        Assert.Equal(diagram, ResourceGraphMermaidExporter.Export(resources.Reverse()));
        return Verifier.Verify(diagram, "mmd").UseDirectory("Snapshots");
    }

    [Fact]
    public Task Export_Empty()
    {
        return Verifier.Verify(ResourceGraphMermaidExporter.Export([]), "mmd").UseDirectory("Snapshots");
    }

    [Fact]
    public Task Export_EscapesLabelsAndUsesSafeIdentifiers()
    {
        var resources = new[]
        {
            CreateResource("end", "end", "unsafe\" --> injected"),
            CreateResource("unsafe\" --> injected", "api \"quoted\" [brackets] (parentheses) #quot; & <script> \\ \r\nnext"),
            CreateResource("replica-1", "`**same label**`"),
            CreateResource("replica-2", "`**same label**`")
        };

        return Verifier.Verify(ResourceGraphMermaidExporter.Export(resources), "mmd").UseDirectory("Snapshots");
    }

    private static ResourceDto CreateResource(string name, string displayName, params string[] referencedNames)
    {
        return new ResourceDto
        {
            Name = name,
            DisplayName = displayName,
            ResourceType = "Project",
            Uid = name,
            ResourceIcon = new IconDto { Path = "", Color = "", Tooltip = "" },
            StateIcon = new IconDto { Path = "", Color = "", Tooltip = "" },
            EndpointUrl = null,
            EndpointText = null,
            ReferencedNames = [.. referencedNames]
        };
    }
}
