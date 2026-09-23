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
    // Keep tests sharing this fixture sequential through browser-context disposal so a previous
    // Blazor circuit cannot continue using fixture services after the next test starts.
    internal SemaphoreSlim TestGate { get; } = new(1, 1);

    public Dictionary<string, string?> Configuration { get; }

    public DashboardWebApplication DashboardApp { get; private set; } = null!;

    // Can't have multiple fixtures when one is generic. Workaround by nesting playwright fixture.
    public PlaywrightFixture PlaywrightFixture { get; }

    protected virtual IReadOnlyList<ResourceViewModel>? Resources => null;

    protected virtual void ConfigureServices(IServiceCollection services)
    {
    }

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

        DashboardApp = CreateDashboardApp(Configuration, Resources, ConfigureServices);

        await DashboardApp.StartAsync();

        if (Resources is not null)
        {
            var writer = DashboardApp.Services.GetRequiredService<IResourceRepositoryWriter>();
            await writer.ReplaceResourcesAsync(Resources.Select(CreateResource).ToList());
        }
    }

    internal static DashboardWebApplication CreateDashboardApp(
        IReadOnlyDictionary<string, string?> configuration,
        IReadOnlyList<ResourceViewModel>? resources = null,
        Action<IServiceCollection>? configureServices = null)
    {
        const string aspireDashboardAssemblyName = "Aspire.Dashboard";
        var currentAssemblyName = Assembly.GetExecutingAssembly().GetName().Name!;
        var currentAssemblyDirectory = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!;
        var aspireAssemblyDirectory = currentAssemblyDirectory.Replace(currentAssemblyName, aspireDashboardAssemblyName);

        var config = new ConfigurationManager().AddInMemoryCollection(configuration).Build();

        // Add services to the container.
        return new DashboardWebApplication(
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
                var dashboardClient = new MockDashboardClient(resources);
                builder.Services.AddSingleton<IDashboardClient>(dashboardClient);
                builder.Services.AddSingleton<IRepositoryFactory>(
                    services => new MockRepositoryFactory(services, dashboardClient));
                configureServices?.Invoke(builder.Services);
            });
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
        await PlaywrightFixture.DisposeAsync();
        await DashboardApp.DisposeAsync();
    }
}
