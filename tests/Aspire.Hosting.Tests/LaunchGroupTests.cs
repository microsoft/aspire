// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIRELAUNCHGROUPS001 // Tests the experimental launch-group APIs.

using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Aspire.Hosting.Orchestrator;
using Aspire.Hosting.Utils;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Aspire.Hosting.Tests;

[Trait("Partition", "2")]
public class LaunchGroupTests(ITestOutputHelper outputHelper)
{
    [Fact]
    public void PublicLaunchGroupApisUseSharedExperimentalDiagnostic()
    {
        var types = new[] { typeof(LaunchGroupExtensions), typeof(LaunchGroupAnnotation), typeof(ExcludeFromLaunchGroupSelectionAnnotation) };
        var methods = typeof(LaunchGroupExtensions).GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly);
        Assert.Equal(
            [nameof(LaunchGroupExtensions.WithLaunchGroup), nameof(LaunchGroupExtensions.WithLaunchGroups)],
            methods.Select(method => method.Name).Order(StringComparer.Ordinal));

        foreach (var member in types.Cast<MemberInfo>().Concat(methods))
        {
            var attribute = Assert.Single(member.GetCustomAttributes<ExperimentalAttribute>());
            Assert.Equal("ASPIRELAUNCHGROUPS001", attribute.DiagnosticId);
            Assert.Equal("https://aka.ms/aspire/diagnostics/{0}", attribute.UrlFormat);
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("CATALOG")]
    [InlineData("basket")]
    public async Task AppSettingsDefinitionsEnableLaunchGroupsWithoutBuilderApis(string? selection)
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var settings = JsonSerializer.Serialize(new
        {
            Aspire = new
            {
                LaunchGroups = new[] { "catalog", "basket" },
                LaunchGroup = selection
            }
        });
        await File.WriteAllTextAsync(Path.Combine(workspace.Path, "appsettings.json"),
            settings, TestContext.Current.CancellationToken);
        var builder = DistributedApplication.CreateBuilder(new DistributedApplicationOptions
        {
            ContentRootPath = workspace.Path,
            DisableDashboard = true
        });
        var catalog = builder.AddExecutable("catalog", "unused", ".")
            .WithAnnotation(new LaunchGroupAnnotation("catalog"));
        var basket = builder.AddExecutable("basket", "unused", ".")
            .WithAnnotation(new LaunchGroupAnnotation("basket"));
        var ungrouped = builder.AddExecutable("ungrouped", "unused", ".");
        var explicitResource = builder.AddExecutable("explicit", "unused", ".").WithExplicitStart();
        await using var app = builder.Build();

        await app.ExecuteBeforeStartHooksAsync(TestContext.Current.CancellationToken);

        Assert.Single(app.Services.GetServices<LaunchGroupService>());
        Assert.Equal(selection == "basket", catalog.Resource.HasAnnotationOfType<ExplicitStartupAnnotation>());
        Assert.Equal(selection == "CATALOG", basket.Resource.HasAnnotationOfType<ExplicitStartupAnnotation>());
        Assert.Equal(selection is not null, ungrouped.Resource.HasAnnotationOfType<ExplicitStartupAnnotation>());
        Assert.True(explicitResource.Resource.HasAnnotationOfType<ExplicitStartupAnnotation>());
    }

    [Fact]
    public async Task ConfigurationDefinitionsAddedAfterBuilderCreationEnableLaunchGroups()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var resource = builder.AddExecutable("worker", "unused", ".")
            .WithAnnotation(new LaunchGroupAnnotation("background"));
        builder.Configuration["Aspire:LaunchGroups:0"] = "background";
        builder.Configuration["Aspire:LaunchGroups:1"] = "empty";
        builder.Configuration["Aspire:LaunchGroup"] = "empty";
        await using var app = builder.Build();

        await app.ExecuteBeforeStartHooksAsync(TestContext.Current.CancellationToken);

        Assert.True(resource.Resource.HasAnnotationOfType<ExplicitStartupAnnotation>());
    }

    [Fact]
    public async Task ConfigurationOnlyInvalidSelectionFailsBeforeStarting()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        builder.Configuration["Aspire:LaunchGroups:0"] = "local";
        builder.Configuration["Aspire:LaunchGroup"] = "missing";
        await using var app = builder.Build();

        var exception = await Assert.ThrowsAsync<DistributedApplicationException>(
            () => app.ExecuteBeforeStartHooksAsync(TestContext.Current.CancellationToken));

        Assert.Equal("Launch group 'missing' is invalid. Select one of: local.", exception.Message);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SelectionWithoutDefinitionsOrMembershipsFailsBeforeStarting(bool commandLine)
    {
        using var builder = TestDistributedApplicationBuilder.Create(
            commandLine ? ["--Aspire:LaunchGroup=catalog"] : []);
        if (!commandLine)
        {
            builder.Configuration["Aspire:LaunchGroup"] = "catalog";
        }
        var resource = builder.AddExecutable("worker", "unused", ".");
        await using var app = builder.Build();

        var exception = await Assert.ThrowsAsync<DistributedApplicationException>(
            () => app.ExecuteBeforeStartHooksAsync(TestContext.Current.CancellationToken));

        Assert.Equal("Launch group 'catalog' is invalid. No launch groups have been defined.", exception.Message);
        Assert.False(resource.Resource.HasAnnotationOfType<ExplicitStartupAnnotation>());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task NoSelectionWithoutDefinitionsPreservesFullRun(string? selection)
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        builder.Configuration["Aspire:LaunchGroup"] = selection;
        var resource = builder.AddExecutable("worker", "unused", ".");
        await using var app = builder.Build();

        await app.ExecuteBeforeStartHooksAsync(TestContext.Current.CancellationToken);

        Assert.Null(app.Services.GetService<LaunchGroupService>());
        Assert.False(resource.Resource.HasAnnotationOfType<ExplicitStartupAnnotation>());
    }

    [Theory]
    [InlineData("[\"catalog\", \"basket\"]", null, false)]
    [InlineData("[\"catalog\", \"basket\"]", null, true)]
    [InlineData("{\"name\": \"catalog\"}", null, false)]
    [InlineData("{\"name\": \"catalog\"}", null, true)]
    [InlineData("[\"catalog\", \"basket\"]", "catalog", false)]
    [InlineData("[\"catalog\", \"basket\"]", "catalog", true)]
    [InlineData("{\"name\": \"catalog\"}", "catalog", false)]
    [InlineData("{\"name\": \"catalog\"}", "catalog", true)]
    public async Task NonScalarSelectionsFailBeforeStarting(string selection, string? scalarValue, bool useMembershipApi)
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        using var json = new MemoryStream(Encoding.UTF8.GetBytes(
            $$$"""{"Aspire": {"LaunchGroup": {{{selection}}}}}"""));
        builder.Configuration.AddJsonStream(json);
        if (scalarValue is not null)
        {
            builder.Configuration["Aspire:LaunchGroup"] = scalarValue;
        }
        var resource = builder.AddExecutable("catalog", "unused", ".");
        if (useMembershipApi)
        {
            resource.WithLaunchGroup("catalog");
        }
        await using var app = builder.Build();

        var exception = await Assert.ThrowsAsync<DistributedApplicationException>(
            () => app.ExecuteBeforeStartHooksAsync(TestContext.Current.CancellationToken));

        Assert.Equal("Aspire:LaunchGroup must be a single group name.", exception.Message);
        Assert.Single(app.Services.GetServices<LaunchGroupService>());
        Assert.False(resource.Resource.HasAnnotationOfType<ExplicitStartupAnnotation>());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PublishingIgnoresNonScalarSelection(bool useMembershipApi)
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        builder.Configuration["Aspire:LaunchGroup:0"] = "catalog";
        var resource = builder.AddExecutable("catalog", "unused", ".");
        if (useMembershipApi)
        {
            resource.WithLaunchGroup("catalog");
        }
        await using var app = builder.Build();

        await app.ExecuteBeforeStartHooksAsync(TestContext.Current.CancellationToken);

        Assert.Equal(useMembershipApi, app.Services.GetService<LaunchGroupService>() is not null);
        Assert.False(resource.Resource.HasAnnotationOfType<ExplicitStartupAnnotation>());
    }

    [Theory]
    [InlineData("\"local\"")]
    [InlineData("[null]")]
    [InlineData("[[\"local\"]]")]
    public async Task InvalidConfigurationDefinitionsFailBeforeStarting(string definitions)
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        using var json = new MemoryStream(Encoding.UTF8.GetBytes(
            $$$"""{"Aspire": {"LaunchGroups": {{{definitions}}}}}"""));
        builder.Configuration.AddJsonStream(json);
        await using var app = builder.Build();

        await Assert.ThrowsAsync<DistributedApplicationException>(
            () => app.ExecuteBeforeStartHooksAsync(TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PublishingIgnoresConfigurationOnlyLaunchGroups(bool defineGroups)
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        if (defineGroups)
        {
            builder.Configuration["Aspire:LaunchGroups:0"] = "local";
        }
        builder.Configuration["Aspire:LaunchGroup"] = "missing";
        var resource = builder.AddExecutable("worker", "unused", ".");
        await using var app = builder.Build();

        await app.ExecuteBeforeStartHooksAsync(TestContext.Current.CancellationToken);

        Assert.Null(app.Services.GetService<LaunchGroupService>());
        Assert.False(resource.Resource.HasAnnotationOfType<ExplicitStartupAnnotation>());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReferencedResourcesAreNotImplicitlySelected(bool expression)
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        builder.Configuration["Aspire:LaunchGroups:0"] = "frontend";
        builder.Configuration["Aspire:LaunchGroups:1"] = "backend";
        builder.Configuration["Aspire:LaunchGroup"] = "frontend";
        var dependency = builder.AddExecutable("backend", "unused", ".")
            .WithHttpEndpoint().WithLaunchGroup("backend");
        var endpoint = dependency.GetEndpoint("http");
        object argument = expression ? ReferenceExpression.Create($"--server={endpoint}") : endpoint;
        var frontend = builder.AddExecutable("frontend", "unused", ".", new object[] { argument }).WithLaunchGroup("frontend");
        var callbacks = 0;
        frontend.WithArgs(context => { Interlocked.Increment(ref callbacks); });
        await using var app = builder.Build();
        await app.ExecuteBeforeStartHooksAsync(TestContext.Current.CancellationToken);

        Assert.True(dependency.Resource.HasAnnotationOfType<ExplicitStartupAnnotation>());
        Assert.False(frontend.Resource.HasAnnotationOfType<ExplicitStartupAnnotation>());
        Assert.Equal(0, callbacks);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("catalog")]
    [InlineData("CATALOG")]
    [InlineData("basket")]
    public async Task SelectionStartsOnlyDirectMembersAndPreservesFullRuns(string? selection)
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        builder.Configuration["Aspire:LaunchGroup"] = selection;
        var catalog = builder.AddContainer("catalog", "image").WithLaunchGroup("catalog");
        var basket = builder.AddExecutable("basket", "unused", ".").WithLaunchGroup("basket");
        var shared = builder.AddExecutable("shared", "unused", ".").WithLaunchGroups("basket", "catalog");
        var ungrouped = builder.AddExecutable("ungrouped", "unused", ".");
        await using var app = builder.Build();

        await app.ExecuteBeforeStartHooksAsync(TestContext.Current.CancellationToken);

        Assert.Equal(selection == "basket", catalog.Resource.HasAnnotationOfType<ExplicitStartupAnnotation>());
        Assert.Equal(selection is "catalog" or "CATALOG", basket.Resource.HasAnnotationOfType<ExplicitStartupAnnotation>());
        Assert.False(shared.Resource.HasAnnotationOfType<ExplicitStartupAnnotation>());
        Assert.Equal(!string.IsNullOrEmpty(selection), ungrouped.Resource.HasAnnotationOfType<ExplicitStartupAnnotation>());
    }

    [Fact]
    public async Task SelectionDoesNotModifyOptedOutResources()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        builder.Configuration["Aspire:LaunchGroup"] = "catalog";
        builder.AddExecutable("catalog", "unused", ".").WithLaunchGroup("catalog");
        var automatic = builder.AddExecutable("automatic", "unused", ".")
            .WithAnnotation(new ExcludeFromLaunchGroupSelectionAnnotation());
        var manual = builder.AddExecutable("manual", "unused", ".").WithExplicitStart()
            .WithAnnotation(new ExcludeFromLaunchGroupSelectionAnnotation());
        var differentGroup = builder.AddExecutable("different-group", "unused", ".").WithLaunchGroup("basket")
            .WithAnnotation(new ExcludeFromLaunchGroupSelectionAnnotation());
        var ungrouped = builder.AddExecutable("ungrouped", "unused", ".");
        await using var app = builder.Build();
        var originalAnnotations = new[] { automatic.Resource, manual.Resource, differentGroup.Resource }
            .ToDictionary(resource => resource, resource => resource.Annotations.ToArray());

        app.Services.GetRequiredService<LaunchGroupService>()
            .Configure(app.Services.GetRequiredService<DistributedApplicationModel>());

        foreach (var (resource, annotations) in originalAnnotations)
        {
            Assert.Equal(annotations, resource.Annotations);
        }
        Assert.True(ungrouped.Resource.HasAnnotationOfType<ExplicitStartupAnnotation>());
    }

    [Fact]
    public async Task MembershipIsAdditiveAndImplicitlyDeclaresNames()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        builder.Configuration["Aspire:LaunchGroup"] = "second";
        var resource = builder.AddExecutable("worker", "unused", ".")
            .WithLaunchGroup("first")
            .WithLaunchGroups("FIRST", "second");
        await using var app = builder.Build();

        await app.ExecuteBeforeStartHooksAsync(TestContext.Current.CancellationToken);

        Assert.Equal(["first", "second"], resource.Resource.Annotations.OfType<LaunchGroupAnnotation>().Select(annotation => annotation.LaunchGroupName));
        Assert.False(resource.Resource.HasAnnotationOfType<ExplicitStartupAnnotation>());
    }

    [Fact]
    public async Task SelectionDoesNotIncludeWaitReferenceOrConnectionStringDependencies()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        builder.Configuration["Aspire:LaunchGroup"] = "frontend";
        var database = builder.AddContainer("database", "image").WithLaunchGroup("backend");
        var child = builder.AddConnectionString("connection", ReferenceExpression.Create($"{database.GetEndpoint("tcp")}"));
        database.WithEndpoint(name: "tcp", scheme: "tcp", targetPort: 1234);
        var migration = builder.AddExecutable("migration", "unused", ".")
            .WithLaunchGroup("backend").WaitFor(database);
        var api = builder.AddExecutable("api", "unused", ".")
            .WithLaunchGroup("backend").WaitForCompletion(migration).WithReference(child);
        var frontend = builder.AddExecutable("frontend", "unused", ".")
            .WithLaunchGroup("frontend").WithReferenceRelationship(api);
        var unrelated = builder.AddContainer("unrelated", "image").WithLaunchGroup("backend");
        await using var app = builder.Build();

        await app.ExecuteBeforeStartHooksAsync(TestContext.Current.CancellationToken);

        foreach (var resource in new IResource[] { database.Resource, child.Resource, migration.Resource, api.Resource, unrelated.Resource })
        {
            Assert.True(resource.HasAnnotationOfType<ExplicitStartupAnnotation>());
        }
        Assert.False(frontend.Resource.HasAnnotationOfType<ExplicitStartupAnnotation>());
    }

    [Theory]
    [InlineData("catalog")]
    [InlineData("basket")]
    public async Task ChildrenRequireTheirOwnMembershipAndDoNotSelectTheirParents(string parentGroup)
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        builder.Configuration["Aspire:LaunchGroup"] = "catalog";
        var parent = builder.AddContainer("cache", "image").WithLaunchGroup(parentGroup);
        var child = builder.AddExecutable("admin", "unused", ".").WithParentRelationship(parent);
        var overrideChild = builder.AddExecutable("other-admin", "unused", ".")
            .WithParentRelationship(parent).WithLaunchGroup("catalog");
        await using var app = builder.Build();

        await app.ExecuteBeforeStartHooksAsync(TestContext.Current.CancellationToken);

        Assert.True(child.Resource.HasAnnotationOfType<ExplicitStartupAnnotation>());
        Assert.False(overrideChild.Resource.HasAnnotationOfType<ExplicitStartupAnnotation>());
        Assert.Equal(parentGroup != "catalog", parent.Resource.HasAnnotationOfType<ExplicitStartupAnnotation>());
    }

    [Fact]
    public async Task ExplicitStartIsPreservedAndDoesNotSelectItsDependencies()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        builder.Configuration["Aspire:LaunchGroup"] = "catalog";
        builder.Configuration["Aspire:LaunchGroups:0"] = "catalog";
        builder.Configuration["Aspire:LaunchGroups:1"] = "basket";
        var dependency = builder.AddContainer("cache", "image").WithLaunchGroup("basket");
        var explicitResource = builder.AddExecutable("explicit", "unused", ".")
            .WithLaunchGroup("catalog").WithExplicitStart().WaitFor(dependency);
        await using var app = builder.Build();

        await app.ExecuteBeforeStartHooksAsync(TestContext.Current.CancellationToken);

        Assert.True(explicitResource.Resource.HasAnnotationOfType<ExplicitStartupAnnotation>());
        Assert.True(dependency.Resource.HasAnnotationOfType<ExplicitStartupAnnotation>());
    }

    [Fact]
    public async Task ConfigurationDefinitionsAllowGroupsWithoutMembers()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        builder.Configuration["Aspire:LaunchGroups:0"] = "catalog";
        builder.Configuration["Aspire:LaunchGroup"] = "BASKET";
        builder.Configuration["Aspire:LaunchGroups:1"] = "basket";
        var resource = builder.AddExecutable("catalog", "unused", ".").WithLaunchGroup("catalog");
        await using var app = builder.Build();

        await app.ExecuteBeforeStartHooksAsync(TestContext.Current.CancellationToken);

        Assert.True(resource.Resource.HasAnnotationOfType<ExplicitStartupAnnotation>());
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("catalog,basket")]
    [InlineData("catalog;basket")]
    [InlineData(" catalog ")]
    public async Task InvalidSelectionsFailBeforeStarting(string selection)
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        builder.Configuration["Aspire:LaunchGroup"] = selection;
        builder.Configuration["Aspire:LaunchGroups:0"] = "catalog";
        builder.Configuration["Aspire:LaunchGroups:1"] = "basket";
        var resource = builder.AddExecutable("catalog", "unused", ".").WithLaunchGroup("catalog");
        await using var app = builder.Build();

        await Assert.ThrowsAsync<DistributedApplicationException>(
            () => app.ExecuteBeforeStartHooksAsync(TestContext.Current.CancellationToken));

        Assert.False(resource.Resource.HasAnnotationOfType<ExplicitStartupAnnotation>());
    }

    [Fact]
    public async Task UndefinedMembershipFailsEvenWithoutSelection()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        builder.Configuration["Aspire:LaunchGroups:0"] = "catalog";
        builder.AddExecutable("basket", "unused", ".").WithLaunchGroup("basket");
        await using var app = builder.Build();

        await Assert.ThrowsAsync<DistributedApplicationException>(
            () => app.ExecuteBeforeStartHooksAsync(TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData(" catalog")]
    [InlineData("catalog ")]
    [InlineData("catalog,basket")]
    [InlineData("catalog;basket")]
    public void InvalidNamesAreRejectedWithPublicParameterNames(string? name)
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var resource = builder.AddExecutable("worker", "unused", ".");

        var annotationException = Assert.ThrowsAny<ArgumentException>(() => new LaunchGroupAnnotation(name!));
        var singleGroupException = Assert.ThrowsAny<ArgumentException>(() => resource.WithLaunchGroup(name!));
        var multipleGroupsException = Assert.ThrowsAny<ArgumentException>(() => resource.WithLaunchGroups("valid", name!));

        Assert.Equal("launchGroupName", annotationException.ParamName);
        Assert.Equal("launchGroupName", singleGroupException.ParamName);
        Assert.Equal("launchGroupNames", multipleGroupsException.ParamName);
        Assert.All(new[] { annotationException, singleGroupException, multipleGroupsException },
            exception => Assert.Equal(name is null ? typeof(ArgumentNullException) : typeof(ArgumentException), exception.GetType()));
        Assert.Empty(resource.Resource.Annotations.OfType<LaunchGroupAnnotation>());
    }

    [Fact]
    public async Task PublishingIgnoresLaunchGroupSelection()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        builder.Configuration["Aspire:LaunchGroup"] = "invalid";
        var resource = builder.AddContainer("catalog", "image").WithLaunchGroup("catalog");
        await using var app = builder.Build();

        app.Services.GetRequiredService<LaunchGroupService>().Configure(app.Services.GetRequiredService<DistributedApplicationModel>());

        Assert.False(resource.Resource.HasAnnotationOfType<ExplicitStartupAnnotation>());
    }

    [Fact]
    public async Task ReferenceCyclesDoNotChangeSelection()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        builder.Configuration["Aspire:LaunchGroup"] = "selected";
        var first = builder.AddExecutable("first", "unused", ".").WithLaunchGroup("selected");
        var second = builder.AddExecutable("second", "unused", ".");
        first.WithReferenceRelationship(second);
        second.WithReferenceRelationship(first);

        await using var app = builder.Build();
        await app.ExecuteBeforeStartHooksAsync(TestContext.Current.CancellationToken);

        Assert.False(first.Resource.HasAnnotationOfType<ExplicitStartupAnnotation>());
        Assert.True(second.Resource.HasAnnotationOfType<ExplicitStartupAnnotation>());
    }

    [Theory]
    [InlineData("NotStarted")]
    [InlineData("Exited")]
    [InlineData("Running")]
    public async Task StartingExcludedResourceDoesNotStartDependenciesOrPrepareSnapshots(string dependencyState)
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        builder.Configuration["Aspire:LaunchGroup"] = "catalog";
        builder.Configuration["Aspire:LaunchGroups:0"] = "catalog";
        builder.Configuration["Aspire:LaunchGroups:1"] = "basket";
        var started = new ConcurrentQueue<string>();
        var dependency = builder.AddExecutable("dependency", "unused", ".").WithLaunchGroup("basket");
        var resource = builder.AddExecutable("worker", "unused", ".").WithLaunchGroup("basket").WaitFor(dependency);
        foreach (var member in new[] { dependency, resource })
        {
            member.WithCommand(KnownResourceCommands.StartCommand, "Start", _ =>
            {
                started.Enqueue(member.Resource.Name);
                return Task.FromResult(CommandResults.Success());
            });
        }
        await using var app = builder.Build();

        await app.ExecuteBeforeStartHooksAsync(TestContext.Current.CancellationToken);

        await app.ResourceNotifications.PublishUpdateAsync(dependency.Resource, snapshot => snapshot with { State = dependencyState });
        await app.ResourceNotifications.PublishUpdateAsync(resource.Resource, snapshot => snapshot with { State = KnownResourceStates.NotStarted });
        Assert.True(app.ResourceNotifications.TryGetCurrentState(dependency.Resource.Name, out var dependencyBefore));
        Assert.True(app.ResourceNotifications.TryGetCurrentState(resource.Resource.Name, out var resourceBefore));

        var result = await app.ResourceCommands.ExecuteCommandAsync(resource.Resource, KnownResourceCommands.StartCommand, TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.Message);
        Assert.Equal(["worker"], started);
        Assert.True(app.ResourceNotifications.TryGetCurrentState(dependency.Resource.Name, out var dependencyAfter));
        Assert.True(app.ResourceNotifications.TryGetCurrentState(resource.Resource.Name, out var resourceAfter));
        Assert.Same(dependencyBefore.Snapshot, dependencyAfter.Snapshot);
        Assert.Same(resourceBefore.Snapshot, resourceAfter.Snapshot);
    }
}
