// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Concurrent;
using System.Threading.Channels;
using Aspire.Dashboard.Components.Controls;
using Aspire.Dashboard.Components.Layout;
using Aspire.Dashboard.Components.Pages;
using Aspire.Dashboard.Components.Resize;
using Aspire.Dashboard.Components.Tests.Shared;
using Aspire.Dashboard.Model;
using Aspire.Dashboard.Tests.Shared;
using Aspire.DashboardService.Proto.V1;
using Aspire.Tests.Shared.DashboardModel;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Routing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using IconVariant = Microsoft.FluentUI.AspNetCore.Components.IconVariant;

namespace Aspire.Dashboard.Components.Tests.Pages;

[UseCulture("en-US")]
public class ResourceOverviewParametersTests : DashboardTestContext
{
    [Theory]
    [InlineData(true, GroupOverview.Summary)]
    [InlineData(false, GroupOverview.Summary)]
    [InlineData(true, GroupOverview.Graph)]
    [InlineData(false, GroupOverview.Graph)]
    public async Task Parameters_ShowsOnlyParameterSummary(bool isDesktop, GroupOverview overview)
    {
        var cut = await RenderParametersAsync(
            [
                ModelTestHelpers.CreateResource("api", state: KnownResourceState.FailedToStart),
                ModelTestHelpers.CreateResource("first", resourceType: KnownResourceTypes.Parameter, state: KnownResourceState.Running),
                ModelTestHelpers.CreateResource("second", resourceType: KnownResourceTypes.Parameter, state: KnownResourceState.Running)
            ], isDesktop: isDesktop, overview: overview);
        var page = cut.FindComponent<ResourceOverview>();

        page.WaitForAssertion(() =>
        {
            Assert.Equal(["No parameters need attention"], page.FindAll("section h2").Select(element => element.TextContent));
            Assert.Equal("Parameter summary", page.Find(".home-hero").GetAttribute("aria-label"));
            Assert.Equal("Parameter states", page.Find(".home-health-bar").GetAttribute("aria-label"));
            Assert.Equal(["", "2", "Values set"], page.FindAll(".home-legend-item span").Select(element => element.TextContent));
            Assert.Equal("width: 100%", page.Find(".home-health-segment").GetAttribute("style"));
        });
        Assert.True(page.FindComponent<ApplicationSummary>().Instance.ParametersOnly);
        Assert.Empty(page.FindAll(".home-activity, .home-insights, .home-problems, .home-resource-graph-layout, .home-hero-sub"));
        Assert.Empty(cut.FindAll(".overview-header-actions, .resources-grid-container"));
        Assert.Empty(cut.FindComponents<Components.Pages.Resources>());
        Assert.Equal(overview, Services.GetRequiredService<DashboardViewPreferences>().GroupOverview);
    }

    [Theory]
    [InlineData(KnownResourceState.Running, 0, "No parameters found")]
    [InlineData(KnownResourceState.Running, 1, "No parameters need attention")]
    [InlineData(KnownResourceState.Running, 2, "No parameters need attention")]
    [InlineData(KnownResourceState.ValueMissing, 1, "1 parameter needs attention")]
    [InlineData(KnownResourceState.ValueMissing, 2, "2 of 2 parameters need attention")]
    [InlineData(KnownResourceState.FailedToStart, 1, "1 parameter needs attention")]
    [InlineData(KnownResourceState.FailedToStart, 2, "2 of 2 parameters need attention")]
    [InlineData(KnownResourceState.Starting, 1, "1 parameter is pending")]
    [InlineData(KnownResourceState.Starting, 2, "2 of 2 parameters are pending")]
    public async Task Parameters_UsesParameterStateAndCount(KnownResourceState state, int count, string expectedHeadline)
    {
        var resources = Enumerable.Range(0, count)
            .Select(index => ModelTestHelpers.CreateResource($"parameter-{index}", resourceType: KnownResourceTypes.Parameter, state: state))
            .Append(ModelTestHelpers.CreateResource("api", state: KnownResourceState.FailedToStart))
            .ToList();
        var cut = await RenderParametersAsync(resources);

        cut.WaitForAssertion(() => Assert.Equal(expectedHeadline, cut.Find(".home-hero-headline").TextContent));
    }

    [Fact]
    public async Task Parameters_LiveChangesUpdateSummary()
    {
        var changes = new ConcurrentBag<Channel<IReadOnlyList<ResourceViewModelChange>>>();
        var cut = await RenderParametersAsync(
            [
                ModelTestHelpers.CreateResource("first", resourceType: KnownResourceTypes.Parameter, state: KnownResourceState.ValueMissing),
                ModelTestHelpers.CreateResource("second", resourceType: KnownResourceTypes.Parameter, state: KnownResourceState.Running)
            ], changes: changes);
        cut.WaitForAssertion(() => Assert.Equal("1 of 2 parameters need attention", cut.Find(".home-hero-headline").TextContent));
        Assert.Equal(["width: 50%", "width: 50%"], cut.FindAll(".home-health-segment").Select(segment => segment.GetAttribute("style")));

        await PublishChangesAsync(new(ResourceViewModelChangeType.Upsert,
            ModelTestHelpers.CreateResource("first", resourceType: KnownResourceTypes.Parameter, state: KnownResourceState.Running)));
        cut.WaitForAssertion(() => Assert.Equal("No parameters need attention", cut.Find(".home-hero-headline").TextContent));

        await PublishChangesAsync(new(ResourceViewModelChangeType.Upsert,
            ModelTestHelpers.CreateResource("third", resourceType: KnownResourceTypes.Parameter, state: KnownResourceState.ValueMissing)));
        cut.WaitForAssertion(() => Assert.Equal("1 of 3 parameters need attention", cut.Find(".home-hero-headline").TextContent));

        await PublishChangesAsync(new(ResourceViewModelChangeType.Delete,
            ModelTestHelpers.CreateResource("third", resourceType: KnownResourceTypes.Parameter)));
        cut.WaitForAssertion(() =>
        {
            Assert.Equal("No parameters need attention", cut.Find(".home-hero-headline").TextContent);
            Assert.Equal(["first", "second"], cut.FindComponent<ApplicationSummary>().Instance.ResourceNames);
        });

        async Task PublishChangesAsync(ResourceViewModelChange change)
        {
            // Each test subscription has its own channel; broadcast updates to both the layout and summary.
            foreach (var channel in changes)
            {
                await channel.Writer.WriteAsync([change]);
            }
        }
    }

    [Fact]
    public async Task Parameters_SelectionScopesSummaryAndPreservesValueViews()
    {
        var cut = await RenderParametersAsync(
            [
                ModelTestHelpers.CreateResource("first", resourceType: KnownResourceTypes.Parameter, state: KnownResourceState.Running),
                ModelTestHelpers.CreateResource("second", resourceType: KnownResourceTypes.Parameter, state: KnownResourceState.ValueMissing),
                ModelTestHelpers.CreateResource("third", resourceType: KnownResourceTypes.Parameter, state: KnownResourceState.ValueMissing)
            ]);
        cut.WaitForAssertion(() => Assert.Equal("2 of 3 parameters need attention", cut.Find(".home-hero-headline").TextContent));

        cut.Find(".resource-row-link[aria-label='first']").Click();
        cut.WaitForAssertion(() => Assert.Equal("first", cut.FindComponent<ParameterOverview>().Instance.Resource.Name));
        Assert.Empty(cut.FindComponents<ApplicationSummary>());
        Assert.Empty(cut.FindAll(".overview-grid"));

        cut.Find(".resource-row-link[aria-label='second']").Click(new Microsoft.AspNetCore.Components.Web.MouseEventArgs { CtrlKey = true });
        cut.WaitForAssertion(() => Assert.Equal("1 of 2 parameters need attention", cut.Find(".home-hero-headline").TextContent));
        Assert.Equal(["first", "second"], cut.FindComponent<ApplicationSummary>().Instance.ResourceNames);

        cut.Find(".resource-row-link[aria-label='second']").Click();
        cut.WaitForAssertion(() => Assert.Equal(["second"], cut.FindComponent<MissingParametersPanel>().Instance.Parameters.Select(parameter => parameter.Name)));
        Assert.Empty(cut.FindComponents<ApplicationSummary>());

        cut.Find(".resource-row-all a").Click();
        cut.WaitForAssertion(() => Assert.Equal("2 of 3 parameters need attention", cut.Find(".home-hero-headline").TextContent));
        Assert.Empty(cut.FindComponent<ResourcesLayout>().Instance.SelectedResourceNames);
        Assert.Equal("http://localhost/parameters", Services.GetRequiredService<NavigationManager>().Uri);
    }

    [Fact]
    public async Task Parameters_DoesNotChangeResourceGraphPreference()
    {
        JSInterop.SetupModule("/js/app-resourcegraph.js").Mode = JSRuntimeMode.Loose;
        var cut = await RenderParametersAsync(
            [
                ModelTestHelpers.CreateResource("api", state: KnownResourceState.Running),
                ModelTestHelpers.CreateResource("parameter", resourceType: KnownResourceTypes.Parameter, state: KnownResourceState.Running)
            ], overview: GroupOverview.Graph);
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll(".home-hero")));
        var parametersSummary = cut.FindComponent<ApplicationSummary>().Instance;

        Services.GetRequiredService<NavigationManager>().NavigateTo("/resources");

        cut.WaitForAssertion(() =>
        {
            Assert.False(cut.FindComponent<ApplicationSummary>().Instance.ParametersOnly);
            Assert.False(cut.Find(".home-resource-graph-layout").HasAttribute("hidden"));
        });
        Assert.NotSame(parametersSummary, cut.FindComponent<ApplicationSummary>().Instance);
        Assert.Empty(cut.FindAll(".home-hero"));
        Assert.Equal(GroupOverview.Graph, Services.GetRequiredService<DashboardViewPreferences>().GroupOverview);
        Assert.Single(JSInterop.Invocations, invocation => invocation.Identifier == "initializeResourcesGraph");
    }

    [Theory]
    [InlineData("/resources", true, 1)]
    [InlineData("/resources", true, 2)]
    [InlineData("/resources", false, 1)]
    [InlineData("/resources", false, 2)]
    [InlineData("/parameters", true, 1)]
    [InlineData("/parameters", true, 2)]
    [InlineData("/parameters", false, 1)]
    [InlineData("/parameters", false, 2)]
    public async Task MissingParameterLink_OpensValueEditors(string initialUrl, bool isDesktop, int missingCount)
    {
        var missingNames = Enumerable.Range(0, missingCount).Select(index => $"missing-{index}").ToArray();
        var command = new CommandViewModel(CommandViewModel.SetParameterCommand, CommandViewModelState.Enabled,
            displayName: "Set value", displayDescription: string.Empty, confirmationMessage: string.Empty,
            argumentInputs: [new InteractionInput { Name = "Value", Label = "Value", InputType = InputType.Text, Required = true }],
            isHighlighted: false, iconName: string.Empty, iconVariant: IconVariant.Regular);
        var resources = missingNames
            .Select(name => ModelTestHelpers.CreateResource(name, resourceType: KnownResourceTypes.Parameter,
                state: KnownResourceState.ValueMissing, commands: [command]))
            .Append(ModelTestHelpers.CreateResource("api", state: KnownResourceState.Running))
            .Append(ModelTestHelpers.CreateResource("set", resourceType: KnownResourceTypes.Parameter, state: KnownResourceState.Running))
            .ToList();
        var changes = new ConcurrentBag<Channel<IReadOnlyList<ResourceViewModelChange>>>();
        var cut = await RenderParametersAsync(resources, isDesktop: isDesktop, changes: changes);
        var navigation = Services.GetRequiredService<NavigationManager>();
        navigation.NavigateTo(initialUrl);
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll(".missing-parameters-link")));
        var link = cut.Find(".missing-parameters-link");
        var expectedUrl = "/parameters?reviewMissing=true" +
            string.Concat(missingNames.Select(name => $"&resource={name}"));
        Assert.Equal(expectedUrl, link.GetAttribute("href"));

        navigation.NavigateTo(expectedUrl);

        cut.WaitForAssertion(() =>
        {
            Assert.Equal(missingNames, cut.FindComponent<MissingParametersPanel>().Instance.Parameters.Select(parameter => parameter.Name));
            Assert.Equal(missingCount, cut.FindAll(".missing-parameter-field").Count);
        });
        Assert.Equal(ResourceTab.Parameters, cut.FindComponent<ResourcesLayout>().Instance.CurrentTab);
        Assert.Empty(cut.FindAll(".home-activity, .home-insights, .home-problems, .home-resource-graph-layout"));

        var updates = missingNames.Select(name => new ResourceViewModelChange(ResourceViewModelChangeType.Upsert,
            ModelTestHelpers.CreateResource(name, resourceType: KnownResourceTypes.Parameter, state: KnownResourceState.Running))).ToArray();
        // A new summary subscribes after leaving the editor. Keep the fake's initial snapshot current too.
        foreach (var (_, resource) in updates)
        {
            resources[resources.FindIndex(original => original.Name == resource.Name)] = resource;
        }
        foreach (var channel in changes)
        {
            await channel.Writer.WriteAsync(updates);
        }
        cut.WaitForAssertion(() => Assert.Equal("All values are set", cut.Find(".missing-parameters-complete span").TextContent));
        Assert.Equal(missingNames, cut.FindComponent<MissingParametersPanel>().Instance.Parameters.Select(parameter => parameter.Name));

        cut.Find(".resource-row-all a").Click();
        cut.WaitForAssertion(() => Assert.Equal("No parameters need attention", cut.Find(".home-hero-headline").TextContent));
        Assert.Equal("http://localhost/parameters", navigation.Uri);
        Assert.Empty(cut.FindAll(".missing-parameters-link"));
    }

    private async Task<IRenderedComponent<Router>> RenderParametersAsync(IList<ResourceViewModel> resources,
        bool isDesktop = true, GroupOverview overview = GroupOverview.Summary,
        ConcurrentBag<Channel<IReadOnlyList<ResourceViewModelChange>>>? changes = null)
    {
        var viewport = new ViewportInformation(IsDesktop: isDesktop, IsUltraLowHeight: false, IsUltraLowWidth: !isDesktop);
        ResourceSetupHelpers.SetupResourceOverview(this, viewport, new TestDashboardClient(isEnabled: true,
            initialResources: resources, resourceChannelProvider: () =>
            {
                var channel = Channel.CreateUnbounded<IReadOnlyList<ResourceViewModelChange>>();
                changes?.Add(channel);
                return channel;
            }));
        var preferences = Services.GetRequiredService<DashboardViewPreferences>();
        await preferences.EnsureLoadedAsync();
        await preferences.SetGroupOverviewAsync(overview);
        Services.GetRequiredService<NavigationManager>().NavigateTo("/parameters");

        return Render<Router>(builder => builder.AddCascadingValue(viewport)
            .Add(parameter => parameter.AppAssembly, typeof(ResourceOverview).Assembly)
            .Add(parameter => parameter.Found, routeData => content =>
            {
                content.OpenComponent<ResourcesLayout>(0);
                content.AddAttribute(1, nameof(ResourcesLayout.Body), (RenderFragment)(body =>
                {
                    body.OpenComponent(0, routeData.PageType);
                    foreach (var parameter in routeData.RouteValues)
                    {
                        body.AddAttribute(1, parameter.Key, parameter.Value);
                    }
                    body.CloseComponent();
                }));
                content.CloseComponent();
            }));
    }
}
