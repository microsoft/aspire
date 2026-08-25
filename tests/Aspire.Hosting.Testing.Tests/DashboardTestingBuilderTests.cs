// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREINTERACTION001

using Aspire.Hosting.Dashboard;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;
using TestingResources = Aspire.Hosting.Testing.Properties.Resources;

namespace Aspire.Hosting.Testing.Tests;

public class DashboardTestingBuilderTests
{
    private const string AspNetCoreUrls = "ASPNETCORE_URLS";
    private const string DashboardOtlpGrpcEndpointUrl = "ASPIRE_DASHBOARD_OTLP_ENDPOINT_URL";
    private const string DashboardOtlpHttpEndpointUrl = "ASPIRE_DASHBOARD_OTLP_HTTP_ENDPOINT_URL";
    private const string DashboardUnsecuredAllowAnonymous = "ASPIRE_DASHBOARD_UNSECURED_ALLOW_ANONYMOUS";
    private const string InteractivityEnabled = "ASPIRE_INTERACTIVITY_ENABLED";
    private const string ResourceServiceEndpointUrl = "ASPIRE_RESOURCE_SERVICE_ENDPOINT_URL";
    private const string DashboardFrontendBrowserToken = "ASPIRE_DASHBOARD_FRONTEND_BROWSERTOKEN";
    private const string AppHostBrowserToken = "AppHost:BrowserToken";
    private const string DashboardResourceServiceApiKey = "ASPIRE_DASHBOARD_RESOURCESERVICE_APIKEY";
    private const string AppHostResourceServiceAuthMode = "AppHost:ResourceService:AuthMode";
    private const string AppHostResourceServiceApiKey = "AppHost:ResourceService:ApiKey";

    [Fact]
    public void DashboardIsDisabledByDefault()
    {
        var options = new DistributedApplicationTestingBuilderOptions();

        Assert.False(options.EnableDashboard);

        using var builder = DistributedApplicationTestingBuilder.Create();
        Assert.Null(builder.Services.FirstOrDefault(descriptor => descriptor.ServiceType == typeof(DashboardServiceHost)));
    }

    [Theory]
    [InlineData(CreationSurface.Generic)]
    [InlineData(CreationSurface.Type)]
    [InlineData(CreationSurface.AdHoc)]
    public async Task DashboardCanBeEnabledAtBuilderCreation(CreationSurface creationSurface)
    {
        await using var builder = await CreateDashboardBuilderAsync(creationSurface, []);

        Assert.Single(builder.Services, descriptor => descriptor.ServiceType == typeof(DashboardServiceHost));
        AssertDashboardTestingDefaults(builder);

        await using var app = await builder.BuildAsync();
    }

    [Theory]
    [InlineData(CreationSurface.Generic)]
    [InlineData(CreationSurface.Type)]
    [InlineData(CreationSurface.AdHoc)]
    public async Task DashboardTestingDefaultsOverrideCreationConfiguration(CreationSurface creationSurface)
    {
        string[] args =
        [
            "--DcpPublisher:RandomizePorts=false",
            $"--{AspNetCoreUrls}=http://127.0.0.1:12345",
            $"--{DashboardOtlpGrpcEndpointUrl}=http://127.0.0.1:12346",
            $"--{DashboardOtlpHttpEndpointUrl}=http://127.0.0.1:12347",
            $"--{ResourceServiceEndpointUrl}=http://127.0.0.1:12348",
            $"--{DashboardUnsecuredAllowAnonymous}=true",
            $"--{InteractivityEnabled}=true"
        ];

        await using var builder = await CreateDashboardBuilderAsync(creationSurface, args);

        AssertDashboardTestingDefaults(builder);
        Assert.Equal(nameof(ResourceServiceAuthMode.ApiKey), builder.Configuration[AppHostResourceServiceAuthMode]);
    }

    [Theory]
    [InlineData(CreationSurface.Generic)]
    [InlineData(CreationSurface.Type)]
    [InlineData(CreationSurface.AdHoc)]
    public async Task DashboardTestingGeneratesAFreshBrowserTokenPerApplication(CreationSurface creationSurface)
    {
        const string SharedToken = "shared-browser-token";
        string[] args = [$"--{DashboardFrontendBrowserToken}={SharedToken}"];

        await using var first = await CreateDashboardBuilderAsync(creationSurface, args);
        await using var second = await CreateDashboardBuilderAsync(creationSurface, args);

        var firstToken = first.Configuration[AppHostBrowserToken];
        var secondToken = second.Configuration[AppHostBrowserToken];

        Assert.NotEmpty(firstToken!);
        Assert.NotEmpty(secondToken!);
        Assert.NotEqual(SharedToken, firstToken);
        Assert.NotEqual(SharedToken, secondToken);
        Assert.NotEqual(firstToken, secondToken);
    }

    [Fact]
    public async Task DashboardTestingDefaultsAreNonInteractiveAndFailFast()
    {
        var builder = DistributedApplicationTestingBuilder.Create(CreateDashboardOptions(), []);
        await using var app = await builder.BuildAsync();

        Assert.False(app.Services.GetRequiredService<IInteractionService>().IsAvailable);
        Assert.Equal(
            WaitBehavior.StopOnResourceUnavailable,
            app.Services.GetRequiredService<IOptions<ResourceNotificationServiceOptions>>().Value.DefaultWaitBehavior);
    }

    [Fact]
    public async Task DashboardTestingOptionsCannotBeNull()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(
            () => DistributedApplicationTestingBuilder.CreateAsync<Projects.TestingAppHost1_AppHost>(null!, []));
        await Assert.ThrowsAsync<ArgumentNullException>(
            () => DistributedApplicationTestingBuilder.CreateAsync(typeof(Projects.TestingAppHost1_AppHost), null!, []));
        Assert.Throws<ArgumentNullException>(() => DistributedApplicationTestingBuilder.Create(null!, []));
    }

    [Fact]
    public async Task ExistingDefaultCallsRemainUnambiguous()
    {
        Assert.Throws<ArgumentNullException>(() => DistributedApplicationTestingBuilder.Create(default!));

        await using var genericBuilder =
            await DistributedApplicationTestingBuilder.CreateAsync<Projects.TestingAppHost1_AppHost>(default);
        await using var typeBuilder =
            await DistributedApplicationTestingBuilder.CreateAsync(typeof(Projects.TestingAppHost1_AppHost), default);

        Assert.Null(genericBuilder.Services.FirstOrDefault(descriptor => descriptor.ServiceType == typeof(DashboardServiceHost)));
        Assert.Null(typeBuilder.Services.FirstOrDefault(descriptor => descriptor.ServiceType == typeof(DashboardServiceHost)));

        await using var genericOptionsBuilder =
            await DistributedApplicationTestingBuilder.CreateAsync<Projects.TestingAppHost1_AppHost>(
                CreateDashboardOptions(),
                [],
                default);
        await using var typeOptionsBuilder =
            await DistributedApplicationTestingBuilder.CreateAsync(
                typeof(Projects.TestingAppHost1_AppHost),
                CreateDashboardOptions(),
                [],
                default);

        Assert.Single(genericOptionsBuilder.Services, descriptor => descriptor.ServiceType == typeof(DashboardServiceHost));
        Assert.Single(typeOptionsBuilder.Services, descriptor => descriptor.ServiceType == typeof(DashboardServiceHost));
    }

    [Theory]
    [InlineData(CreationSurface.Generic)]
    [InlineData(CreationSurface.Type)]
    [InlineData(CreationSurface.AdHoc)]
    public async Task DashboardTestingIsRejectedInPublishMode(CreationSurface creationSurface)
    {
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => CreatePublishBuilderAsync(creationSurface));

        Assert.Equal(TestingResources.DashboardTestingPublishModeExceptionMessage, exception.Message);
    }

    [Theory]
    [InlineData(CreationSurface.Generic)]
    [InlineData(CreationSurface.Type)]
    public async Task DashboardTestingPublishModeFailureStopsAppHostEntryPoint(CreationSurface creationSurface)
    {
        using var probe = TestingAppHostEntryPointProbe.Create();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => CreatePublishBuilderAsync(creationSurface, $"--entry-point-exit-probe={probe.Id}"));

        Assert.Equal(TestingResources.DashboardTestingPublishModeExceptionMessage, exception.Message);
        await probe.Exited.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task DashboardEnabledThroughConfigureBuilderKeepsCallerConfiguration()
    {
        await using var builder = await DistributedApplicationTestingBuilder.CreateAsync<Projects.TestingAppHost1_AppHost>(
            [$"--{DashboardUnsecuredAllowAnonymous}=true", $"--{AspNetCoreUrls}=http://127.0.0.1:12345"],
            (options, _) => options.DisableDashboard = false);

        Assert.Equal("true", builder.Configuration[DashboardUnsecuredAllowAnonymous]);
        Assert.Equal("http://127.0.0.1:12345", builder.Configuration[AspNetCoreUrls]);
    }

    [Theory]
    [InlineData(CreationSurface.Generic, "--clear-apphost-browser-token")]
    [InlineData(CreationSurface.Generic, "--null-apphost-browser-token")]
    [InlineData(CreationSurface.Type, "--clear-apphost-browser-token")]
    [InlineData(CreationSurface.Type, "--null-apphost-browser-token")]
    public async Task DashboardTestingRestoresTheBrowserTokenAnAppHostCleared(
        CreationSurface creationSurface,
        string appHostFlag)
    {
        await using var builder = await CreateDashboardBuilderAsync(creationSurface, [appHostFlag]);
        await using var app = await builder.BuildAsync();

        var dashboardOptions = app.Services.GetRequiredService<IOptions<DashboardOptions>>().Value;
        Assert.False(string.IsNullOrEmpty(dashboardOptions.DashboardToken));
    }

    [Theory]
    [InlineData(CreationSurface.Generic)]
    [InlineData(CreationSurface.Type)]
    public async Task DashboardTestingLeavesTheBrowserTokenToTheCallersBuilder(CreationSurface creationSurface)
    {
        await using var builder = await CreateDashboardBuilderAsync(creationSurface, []);
        builder.Configuration[AppHostBrowserToken] = "";
        await using var app = await builder.BuildAsync();

        var dashboardOptions = app.Services.GetRequiredService<IOptions<DashboardOptions>>().Value;
        Assert.True(string.IsNullOrEmpty(dashboardOptions.DashboardToken));
    }

    [Theory]
    [InlineData(CreationSurface.Generic, "--unsecure-apphost-resource-service")]
    [InlineData(CreationSurface.Generic, "--clear-apphost-resource-service-key")]
    [InlineData(CreationSurface.Type, "--unsecure-apphost-resource-service")]
    [InlineData(CreationSurface.Type, "--clear-apphost-resource-service-key")]
    public async Task DashboardTestingRestoresResourceServiceAuthenticationAnAppHostDowngraded(
        CreationSurface creationSurface,
        string appHostFlag)
    {
        await using var builder = await CreateDashboardBuilderAsync(creationSurface, [appHostFlag]);

        Assert.Equal(nameof(ResourceServiceAuthMode.ApiKey), builder.Configuration[AppHostResourceServiceAuthMode]);
        Assert.False(string.IsNullOrEmpty(builder.Configuration[AppHostResourceServiceApiKey]));
    }

    [Theory]
    [InlineData(CreationSurface.Generic)]
    [InlineData(CreationSurface.Type)]
    public async Task DashboardTestingGeneratesAFreshResourceServiceApiKeyPerApplication(CreationSurface creationSurface)
    {
        const string SharedKey = "shared-resource-service-key";
        string[] args = [$"--{DashboardResourceServiceApiKey}={SharedKey}"];

        await using var first = await CreateDashboardBuilderAsync(creationSurface, args);
        await using var second = await CreateDashboardBuilderAsync(creationSurface, args);

        var firstKey = first.Configuration[AppHostResourceServiceApiKey];
        var secondKey = second.Configuration[AppHostResourceServiceApiKey];

        Assert.False(string.IsNullOrEmpty(firstKey));
        Assert.False(string.IsNullOrEmpty(secondKey));
        Assert.NotEqual(SharedKey, firstKey);
        Assert.NotEqual(SharedKey, secondKey);
        Assert.NotEqual(firstKey, secondKey);
    }

    private static DistributedApplicationTestingBuilderOptions CreateDashboardOptions()
    {
        return new()
        {
            EnableDashboard = true
        };
    }

    private static void AssertDashboardTestingDefaults(IDistributedApplicationTestingBuilder builder)
    {
        Assert.Equal("true", builder.Configuration["DcpPublisher:RandomizePorts"]);
        Assert.Equal(string.Empty, builder.Configuration[AspNetCoreUrls]);
        Assert.Equal(string.Empty, builder.Configuration[DashboardOtlpGrpcEndpointUrl]);
        Assert.Equal(string.Empty, builder.Configuration[DashboardOtlpHttpEndpointUrl]);
        Assert.Equal("http://127.0.0.1:0", builder.Configuration[ResourceServiceEndpointUrl]);
        Assert.Equal("false", builder.Configuration[DashboardUnsecuredAllowAnonymous]);
        Assert.Equal("false", builder.Configuration[InteractivityEnabled]);

        var browserToken = builder.Configuration[DashboardFrontendBrowserToken];
        Assert.NotEmpty(browserToken!);
        Assert.Equal(browserToken, builder.Configuration[AppHostBrowserToken]);
    }

    private static async Task<IDistributedApplicationTestingBuilder> CreateDashboardBuilderAsync(
        CreationSurface creationSurface,
        string[] args)
    {
        var options = CreateDashboardOptions();

        return creationSurface switch
        {
            CreationSurface.Generic => await DistributedApplicationTestingBuilder.CreateAsync<Projects.TestingAppHost1_AppHost>(options, args),
            CreationSurface.Type => await DistributedApplicationTestingBuilder.CreateAsync(typeof(Projects.TestingAppHost1_AppHost), options, args),
            CreationSurface.AdHoc => DistributedApplicationTestingBuilder.Create(options, args),
            _ => throw new ArgumentOutOfRangeException(nameof(creationSurface))
        };
    }

    private static async Task CreatePublishBuilderAsync(CreationSurface creationSurface, params string[] additionalArgs)
    {
        var options = CreateDashboardOptions();
        string[] args = [.. additionalArgs, "--publisher", "manifest"];

        var builder = creationSurface switch
        {
            CreationSurface.Generic => await DistributedApplicationTestingBuilder.CreateAsync<Projects.TestingAppHost1_AppHost>(
                options,
                args),
            CreationSurface.Type => await DistributedApplicationTestingBuilder.CreateAsync(
                typeof(Projects.TestingAppHost1_AppHost),
                options,
                args),
            CreationSurface.AdHoc => DistributedApplicationTestingBuilder.Create(options, args),
            _ => throw new ArgumentOutOfRangeException(nameof(creationSurface))
        };

        await builder.DisposeAsync();
    }

    public enum CreationSurface
    {
        Generic,
        Type,
        AdHoc
    }
}
