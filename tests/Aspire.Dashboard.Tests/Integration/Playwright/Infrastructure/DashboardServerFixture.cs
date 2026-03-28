// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Reflection;
using Aspire.Dashboard.Configuration;
using Aspire.Hosting;
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
                builder.Services.AddSingleton<IDashboardClient, MockDashboardClient>();
            });

        await DashboardApp.StartAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await DashboardApp.DisposeAsync();
        await PlaywrightFixture.DisposeAsync();
    }
}
