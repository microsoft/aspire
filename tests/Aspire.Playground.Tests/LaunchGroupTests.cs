// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIRELAUNCHGROUPS001 // Tests TestShop launch-group scenarios.

using System.Runtime.CompilerServices;
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SamplesIntegrationTests;
using Xunit;

namespace Aspire.Playground.Tests;

public class LaunchGroupTests(ITestOutputHelper output)
{
    [Fact]
    public async Task TestShopStructuralAndRelationshipResourcesUseExpectedLaunchGroups()
    {
        var builder = await DistributedApplicationTestFactory.CreateAsync(typeof(Projects.TestShop_AppHost), output);
        var postgres = Assert.Single(builder.Resources, resource => resource.Name == "postgres");
        AssertLaunchGroups(postgres, "catalog", "frontend");

        var database = Assert.Single(builder.Resources, resource => resource.Name == "catalogdb");
        Assert.Empty(database.Annotations.OfType<LaunchGroupAnnotation>());
        Assert.Same(postgres, Assert.IsAssignableFrom<IResourceWithParent>(database).Parent);

        var pgAdmin = Assert.Single(builder.Resources, resource => resource.Name == "pgadmin");
        AssertLaunchGroups(pgAdmin, "catalog");
        Assert.IsNotAssignableFrom<IResourceWithParent>(pgAdmin);

        foreach (var name in GetDashboardDevelopmentResourceNames(builder.Resources))
        {
            AssertLaunchGroups(GetResource(builder, name), "basket");
        }

        await using var app = await builder.BuildAsync();
    }

    [Fact]
    public async Task TestShopSharedResourcesDeclareExpectedLaunchGroups()
    {
        var builder = await DistributedApplicationTestFactory.CreateAsync(typeof(Projects.TestShop_AppHost), output);
        AssertLaunchGroups(GetResource(builder, "catalogdbapp"), "catalog", "frontend");
        AssertLaunchGroups(GetResource(builder, "catalogservice"), "catalog", "frontend");
        AssertLaunchGroups(GetResource(builder, "basketcache"), "basket", "frontend");
        AssertLaunchGroups(GetResource(builder, "basketservice"), "basket", "frontend");
        AssertLaunchGroups(GetResource(builder, "messaging"), "basket", "orders", "frontend");
        AssertLaunchGroups(GetResource(builder, "frontend"), "frontend");
        AssertLaunchGroups(GetResource(builder, "apigateway"), "frontend");
        AssertLaunchGroups(GetResource(builder, "orderprocessor"), "orders");
        AssertLaunchGroups(GetResource(builder, "storefront-promotion"), "frontend");
        AssertLaunchGroups(GetResource(builder, "low-stock-threshold"), "catalog", "frontend");
        AssertLaunchGroups(GetResource(builder, "checkout-signing-key"), "basket", "frontend");

        await using var app = await builder.BuildAsync();
    }

    [Fact]
    public async Task TestShopExplicitStartResourceKeepsItsLaunchGroupMembership()
    {
        var builder = await DistributedApplicationTestFactory.CreateAsync(typeof(Projects.TestShop_AppHost), output);
        var activityGenerator = GetResource(builder, "activity-generator");

        AssertLaunchGroups(activityGenerator, "frontend");
        Assert.True(activityGenerator.HasAnnotationOfType<ExplicitStartupAnnotation>());

        await using var app = await builder.BuildAsync();
    }

    [Fact]
    public async Task TestShopLaunchGroupsCompletePreflightAndApplyExpectedDecisions()
    {
        await AssertLaunchGroupSelectionAsync(
            "catalog",
            ["catalogdb", "catalogservice"],
            ["basketservice", "orderprocessor"]);
        await AssertLaunchGroupSelectionAsync(
            "basket",
            ["rediscommander", "redisinsight", "basketservice", "messaging"],
            ["catalogservice", "orderprocessor"]);
        await AssertLaunchGroupSelectionAsync(
            "frontend",
            ["catalogdb", "basketservice", "frontend"],
            ["pgadmin", "orderprocessor"]);
        await AssertLaunchGroupSelectionAsync(
            "orders",
            ["messaging", "orderprocessor"],
            ["catalogservice", "frontend"]);
    }

    private static async Task AssertLaunchGroupSelectionAsync(
        string launchGroup,
        string[] selectedResourceNames,
        string[] excludedResourceNames)
    {
        await using var factory = new PreflightOnlyDistributedApplicationFactory(
            typeof(Projects.TestShop_AppHost),
            launchGroup);
        var startTask = factory.StartAsync(TestContext.Current.CancellationToken);
        var applicationTask = factory.Application;
        var completedTask = await Task.WhenAny(applicationTask, startTask)
            .WaitAsync(TestContext.Current.CancellationToken);
        if (ReferenceEquals(completedTask, startTask))
        {
            await startTask;
            throw new InvalidOperationException("The TestShop AppHost started before its application could be inspected.");
        }

        var app = await applicationTask;
        await ExecuteBeforeStartHooksAsync(app, TestContext.Current.CancellationToken);
        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        var omittedResourceNames = s_dashboardDevelopmentResourceNames
            .Except(GetDashboardDevelopmentResourceNames(model.Resources))
            .ToArray();
        selectedResourceNames = selectedResourceNames.Except(omittedResourceNames).ToArray();
        excludedResourceNames = excludedResourceNames.Except(omittedResourceNames).ToArray();

        Assert.Multiple(
            () => Assert.All(selectedResourceNames, name =>
                Assert.False(
                    GetResource(model, name).HasAnnotationOfType<ExplicitStartupAnnotation>(),
                    $"Resource '{name}' should be selected by launch group '{launchGroup}'.")),
            () => Assert.All(excludedResourceNames, name =>
                Assert.True(
                    GetResource(model, name).HasAnnotationOfType<ExplicitStartupAnnotation>(),
                    $"Resource '{name}' should be excluded by launch group '{launchGroup}'.")));
    }

    // TestShop adds the Redis tools inside `#if !SKIP_DASHBOARD_REFERENCE`. CI builds playground AppHosts with
    // SkipDashboardProjectReference=true (see playground/Directory.Build.targets), so they exist only in local builds.
    // Both tools come from the same block, so either both exist or neither does.
    private static readonly string[] s_dashboardDevelopmentResourceNames = ["rediscommander", "redisinsight"];

    private static string[] GetDashboardDevelopmentResourceNames(IEnumerable<IResource> resources)
    {
        var resourceList = resources.ToList();
        var presentNames = s_dashboardDevelopmentResourceNames
            .Where(name => resourceList.Any(resource => resource.Name == name))
            .ToArray();

        Assert.True(
            presentNames.Length is 0 || presentNames.Length == s_dashboardDevelopmentResourceNames.Length,
            $"Expected all or none of the dashboard-development resources, found: {string.Join(", ", presentNames)}.");
        return presentNames;
    }

    private static IResource GetResource(IDistributedApplicationTestingBuilder builder, string name) =>
        Assert.Single(builder.Resources, resource => resource.Name == name);

    private static IResource GetResource(DistributedApplicationModel model, string name) =>
        Assert.Single(model.Resources, resource => resource.Name == name);

    private static void AssertLaunchGroups(IResource resource, params string[] expected)
    {
        var actual = resource.Annotations
            .OfType<LaunchGroupAnnotation>()
            .Select(annotation => annotation.LaunchGroupName)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        Assert.Equal(expected.Order(StringComparer.OrdinalIgnoreCase).ToArray(), actual);
    }

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "ExecuteBeforeStartHooksAsync")]
    private static extern Task ExecuteBeforeStartHooksAsync(DistributedApplication app, CancellationToken cancellationToken);

    private sealed class PreflightOnlyDistributedApplicationFactory(Type entryPoint, string launchGroup)
        : DistributedApplicationFactory(entryPoint)
    {
        private readonly TaskCompletionSource<DistributedApplication> _application =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly ManualResetEventSlim _continueAfterBuild = new();
        private ConfigurationManager? _configuration;

        public Task<DistributedApplication> Application => _application.Task;

        protected override void OnBuilderCreated(DistributedApplicationBuilder applicationBuilder)
        {
            base.OnBuilderCreated(applicationBuilder);
            _configuration = applicationBuilder.Configuration;
            _configuration["Aspire:LaunchGroup"] = launchGroup;
        }

        protected override void OnBuilt(DistributedApplication application)
        {
            base.OnBuilt(application);
            _application.TrySetResult(application);
            _continueAfterBuild.Wait();
        }

        public override async ValueTask DisposeAsync()
        {
            // The test invokes the before-start hooks directly while the entry point is paused after Build().
            // Switch to inspect mode before releasing it so the entry point does not run the hooks again.
            _configuration?["AppHost:Operation"] = "inspect";

            var disposeTask = default(ValueTask);
            try
            {
                // Base disposal cancels host startup before the entry point is allowed to continue to Run().
                disposeTask = base.DisposeAsync();
            }
            finally
            {
                _continueAfterBuild.Set();
            }

            await disposeTask.ConfigureAwait(false);
            _continueAfterBuild.Dispose();
        }
    }
}
