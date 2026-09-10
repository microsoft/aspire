// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Reflection;
using Aspire.Dashboard.Configuration;
using Aspire.Dashboard.Model;
using Aspire.Hosting;
using Aspire.DashboardService.Proto.V1;
using Google.Protobuf.WellKnownTypes;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Aspire.Dashboard.Tests.Integration.Playwright.Infrastructure;

public class DashboardServerFixture : IAsyncLifetime
{
    public Dictionary<string, string?> Configuration { get; }

    public DashboardWebApplication DashboardApp { get; private set; } = null!;

    // Can't have multiple fixtures when one is generic. Workaround by nesting playwright fixture.
    public PlaywrightFixture PlaywrightFixture { get; }

    protected virtual IReadOnlyList<ResourceViewModel>? Resources => null;

    public DashboardServerFixture()
    {
        PlaywrightFixture = new PlaywrightFixture();

        Configuration = new Dictionary<string, string?>
        {
            [DashboardConfigNames.DashboardFrontendUrlName.ConfigKey] = "http://127.0.0.1:0",
            [DashboardConfigNames.DashboardOtlpHttpUrlName.ConfigKey] = "http://127.0.0.1:0",
            [DashboardConfigNames.DashboardOtlpAuthModeName.ConfigKey] = nameof(OtlpAuthMode.Unsecured),
            [DashboardConfigNames.DashboardFrontendAuthModeName.ConfigKey] = nameof(FrontendAuthMode.Unsecured)
        };
    }

    public async ValueTask InitializeAsync()
    {
        await PlaywrightFixture.InitializeAsync();

        const string aspireDashboardAssemblyName = "Aspire.Dashboard";
        var currentAssemblyName = Assembly.GetExecutingAssembly().GetName().Name!;
        var currentAssemblyDirectory = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!;
        var aspireAssemblyDirectory = currentAssemblyDirectory.Replace(currentAssemblyName, aspireDashboardAssemblyName);

        var config = new ConfigurationManager().AddInMemoryCollection(Configuration).Build();

        // Add services to the container.
        DashboardApp = new DashboardWebApplication(
            options: new WebApplicationOptions
            {
                EnvironmentName = "Development",
                ContentRootPath = aspireAssemblyDirectory,
                WebRootPath = Path.Combine(aspireAssemblyDirectory, "wwwroot"),
                ApplicationName = aspireDashboardAssemblyName,
            },
            preConfigureBuilder: builder =>
            {
                builder.Configuration.AddConfiguration(config);
                builder.Services.AddSingleton<IDashboardClient>(new MockDashboardClient(Resources));
            });

        await DashboardApp.StartAsync();

        if (Resources is not null)
        {
            var writer = DashboardApp.Services.GetRequiredService<IResourceRepositoryWriter>();
            await writer.ReplaceResourcesAsync(Resources.Select(CreateResource).ToList());
        }
    }

    private static Resource CreateResource(ResourceViewModel resource)
    {
        var result = new Resource
        {
            Name = resource.Name,
            DisplayName = resource.DisplayName,
            ResourceType = resource.ResourceType,
            Uid = resource.Uid,
            State = resource.State ?? string.Empty,
            StateStyle = resource.StateStyle ?? string.Empty
        };

        if (resource.CreationTimeStamp is { } creationTimeStamp)
        {
            result.CreatedAt = Timestamp.FromDateTime(creationTimeStamp.ToUniversalTime());
        }
        if (resource.StartTimeStamp is { } startTimeStamp)
        {
            result.StartedAt = Timestamp.FromDateTime(startTimeStamp.ToUniversalTime());
        }
        if (resource.StopTimeStamp is { } stopTimeStamp)
        {
            result.StoppedAt = Timestamp.FromDateTime(stopTimeStamp.ToUniversalTime());
        }

        result.Urls.AddRange(resource.Urls.Select(url => new Url
        {
            EndpointName = url.EndpointName ?? string.Empty,
            FullUrl = url.Url.AbsoluteUri,
            IsInternal = url.IsInternal,
            IsInactive = url.IsInactive,
            DisplayProperties = new UrlDisplayProperties
            {
                DisplayName = url.DisplayProperties.DisplayName,
                SortOrder = url.DisplayProperties.SortOrder
            }
        }));

        return result;
    }

    public async ValueTask DisposeAsync()
    {
        await DashboardApp.DisposeAsync();
        await PlaywrightFixture.DisposeAsync();
    }
}
