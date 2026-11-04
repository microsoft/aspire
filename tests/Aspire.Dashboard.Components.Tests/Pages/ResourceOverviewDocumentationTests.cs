// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Immutable;
using System.Threading.Channels;
using Aspire.Dashboard.Components.Controls;
using Aspire.Dashboard.Components.Layout;
using Aspire.Dashboard.Components.Pages;
using Aspire.Dashboard.Components.Resize;
using Aspire.Dashboard.Components.Tests.Shared;
using Aspire.Dashboard.Model;
using Aspire.Dashboard.Tests.Shared;
using Aspire.Tests.Shared.DashboardModel;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Aspire.Dashboard.Components.Tests.Pages;

[UseCulture("en-US")]
public class ResourceOverviewDocumentationTests : DashboardTestContext
{
    [Fact]
    public void TrueEmptyStates_ShowDocumentationForAllMappedPanels()
    {
        var cut = RenderOverview(ModelTestHelpers.CreateResource("empty", resourceType: KnownResourceTypes.Project));

        cut.WaitForAssertion(() =>
        {
            var links = cut.FindAll(".empty-state-documentation-link");
            Assert.Equal(10, links.Count);
            Assert.Equal(
                Enum.GetValues<EmptyStateDocumentationPanel>()
                    .Select(EmptyStateDocumentationLinks.GetArticle)
                    .Select(article => article.Url)
                    .Order(),
                links.Select(link => link.GetAttribute("href")).Order());
        });
    }

    [Fact]
    public void LoadingConsole_DoesNotShowDocumentation()
    {
        var consoleChannel = Channel.CreateUnbounded<IReadOnlyList<ResourceLogLine>>();
        var cut = RenderOverview(
            ModelTestHelpers.CreateResource("loading", resourceType: KnownResourceTypes.Project),
            consoleChannel);

        var consoleCard = cut.Find("[aria-labelledby='overview-console-title']");
        Assert.Empty(consoleCard.QuerySelectorAll(".empty-state-documentation-link"));
    }

    [Fact]
    public async Task FilteredEnvironmentVariables_NoResultsDoesNotShowDocumentation()
    {
        var environment = ImmutableArray.Create(new EnvironmentVariableViewModel("ASPNETCORE_ENVIRONMENT", "Development", fromSpec: true));
        var cut = RenderOverview(ModelTestHelpers.CreateResource(
            "filtered",
            resourceType: KnownResourceTypes.Project,
            environment: environment));
        var environmentCard = cut.Find("[aria-labelledby='overview-environment-title']");

        var filter = Assert.Single(
            cut.FindComponents<Microsoft.FluentUI.AspNetCore.Components.FluentTextInput>(),
            component => component.Instance.Class?.Contains("overview-card-filter", StringComparison.Ordinal) == true);
        await filter.InvokeAsync(() => filter.Instance.ValueChanged.InvokeAsync("missing"));

        cut.WaitForAssertion(() =>
        {
            Assert.Contains("No environment variables match the filter", environmentCard.TextContent);
            Assert.Empty(environmentCard.QuerySelectorAll(".empty-state-documentation-link"));
        });
    }

    [Fact]
    public void NonEmptyPanel_KeepsContentAndDoesNotShowDocumentation()
    {
        var endpoint = new UrlViewModel(
            "https",
            new Uri("https://localhost:5001"),
            isInternal: false,
            isInactive: false,
            UrlDisplayPropertiesViewModel.Empty);
        var cut = RenderOverview(ModelTestHelpers.CreateResource(
            "endpoint",
            resourceType: KnownResourceTypes.Project,
            urls: [endpoint]));
        var endpointCard = cut.Find("[aria-labelledby='overview-endpoints-title']");

        var link = endpointCard.QuerySelector("a.overview-list-value");
        Assert.NotNull(link);
        Assert.Equal("https://localhost:5001", link.GetAttribute("href"));
        Assert.Empty(endpointCard.QuerySelectorAll(".empty-state-documentation-link"));
    }

    private IRenderedComponent<ResourcesLayout> RenderOverview(
        ResourceViewModel resource,
        Channel<IReadOnlyList<ResourceLogLine>>? consoleChannel = null)
    {
        var viewport = new ViewportInformation(IsDesktop: true, IsUltraLowHeight: false, IsUltraLowWidth: false);
        var client = new TestDashboardClient(
            isEnabled: true,
            initialResources: [resource],
            consoleLogsChannelProvider: _ => consoleChannel ?? CreateCompletedConsoleChannel(),
            resourceChannelProvider: Channel.CreateUnbounded<IReadOnlyList<ResourceViewModelChange>>);
        ResourceSetupHelpers.SetupResourceOverview(this, viewport, client);
        Services.GetRequiredService<NavigationManager>().NavigateTo($"/resources/{resource.Name}");

        return Render<ResourcesLayout>(parameters => parameters
            .AddCascadingValue(viewport)
            .Add(p => p.Body, builder =>
            {
                builder.OpenComponent<ResourceOverview>(0);
                builder.AddAttribute(1, nameof(ResourceOverview.ResourceName), resource.Name);
                builder.CloseComponent();
            }));
    }

    private static Channel<IReadOnlyList<ResourceLogLine>> CreateCompletedConsoleChannel()
    {
        var channel = Channel.CreateUnbounded<IReadOnlyList<ResourceLogLine>>();
        channel.Writer.Complete();
        return channel;
    }
}
