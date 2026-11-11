// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Dashboard.Components.Controls;
using Aspire.Dashboard.Components.Tests.Shared;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.AspNetCore.Components.Sections;
using Xunit;

namespace Aspire.Dashboard.Components.Tests.Controls;

public class StableSectionContentTests : DashboardTestContext
{
    [Fact]
    public void Render_OwnerRerenders_KeepsContentInstancesAndShowsLatestContent()
    {
        var instances = new List<Probe>();

        var cut = Render<Owner>(builder => builder
            .Add(p => p.Instances, instances)
            .Add(p => p.Label, "first"));

        Assert.Single(instances);
        Assert.Equal("first", cut.Find(".probe").TextContent);

        cut.Render(parameters => parameters.Add(p => p.Label, "second"));
        cut.Render(parameters => parameters.Add(p => p.Label, "third"));

        // A plain SectionContent would have created a new probe for each render of the owner.
        Assert.Single(instances);
        Assert.Equal("third", cut.Find(".probe").TextContent);
    }

    private sealed class Owner : ComponentBase
    {
        [Parameter]
        public required List<Probe> Instances { get; set; }

        [Parameter]
        public string Label { get; set; } = string.Empty;

        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
            builder.OpenComponent<SectionOutlet>(0);
            builder.AddComponentParameter(1, nameof(SectionOutlet.SectionName), "test-section");
            builder.CloseComponent();

            builder.OpenComponent<StableSectionContent>(2);
            builder.AddComponentParameter(3, nameof(StableSectionContent.SectionName), "test-section");
            builder.AddComponentParameter(4, nameof(StableSectionContent.ChildContent), (RenderFragment)(content =>
            {
                content.OpenComponent<Probe>(0);
                content.AddComponentParameter(1, nameof(Probe.Instances), Instances);
                content.AddComponentParameter(2, nameof(Probe.Label), Label);
                content.CloseComponent();
            }));
            builder.CloseComponent();
        }
    }

    private sealed class Probe : ComponentBase
    {
        [Parameter]
        public required List<Probe> Instances { get; set; }

        [Parameter]
        public string Label { get; set; } = string.Empty;

        protected override void OnInitialized() => Instances.Add(this);

        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
            builder.OpenElement(0, "span");
            builder.AddAttribute(1, "class", "probe");
            builder.AddContent(2, Label);
            builder.CloseElement();
        }
    }
}
