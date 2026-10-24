// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Aspire.Hosting.Orchestrator;
using Aspire.Hosting.Dcp;
using Aspire.Hosting.Utils;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Aspire.Hosting.Tests;

[Trait("Partition", "2")]
public class LaunchGroupTests(ITestOutputHelper outputHelper)
{
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

        var service = Assert.Single(app.Services.GetServices<LaunchGroupService>());
        Assert.Equal(selection is not null, service.IsActive);
        Assert.Equal(selection == "basket", catalog.Resource.HasAnnotationOfType<ExplicitStartupAnnotation>());
        Assert.Equal(selection == "CATALOG", basket.Resource.HasAnnotationOfType<ExplicitStartupAnnotation>());
        Assert.False(ungrouped.Resource.HasAnnotationOfType<ExplicitStartupAnnotation>());
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

        Assert.True(app.Services.GetRequiredService<LaunchGroupService>().IsActive);
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
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task ExecutableConstructorArgumentsIncludeReferencedResources(bool expression, bool lateStart)
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        builder.DefineLaunchGroups("frontend", "backend", "empty");
        builder.Configuration["Aspire:LaunchGroup"] = lateStart ? "empty" : "frontend";
        var dependency = builder.AddExecutable("backend", "unused", ".")
            .WithHttpEndpoint().WithLaunchGroup("backend");
        var endpoint = dependency.GetEndpoint("http");
        object argument = expression ? ReferenceExpression.Create($"--server={endpoint}") : endpoint;
        var frontend = builder.AddExecutable("frontend", "unused", ".", new object[] { argument }).WithLaunchGroup("frontend");
        var callbacks = 0;
        frontend.WithArgs(context => { Interlocked.Increment(ref callbacks); });
        var started = new ConcurrentBag<string>();
        foreach (var resource in new[] { dependency, frontend })
        {
            resource.WithCommand(KnownResourceCommands.StartCommand, "Start", async context =>
            {
                started.Add(resource.Resource.Name);
                await context.Services.GetRequiredService<ResourceNotificationService>().PublishUpdateAsync(
                    resource.Resource, snapshot => snapshot with { State = KnownResourceStates.Running });
                return CommandResults.Success();
            });
        }
        await using var app = builder.Build();
        await app.ExecuteBeforeStartHooksAsync(TestContext.Current.CancellationToken);

        Assert.Equal(lateStart, dependency.Resource.HasAnnotationOfType<ExplicitStartupAnnotation>());
        Assert.Equal(lateStart, frontend.Resource.HasAnnotationOfType<ExplicitStartupAnnotation>());
        if (lateStart)
        {
            foreach (var resource in new[] { dependency, frontend })
            {
                await app.ResourceNotifications.PublishUpdateAsync(resource.Resource, snapshot => snapshot with { State = KnownResourceStates.NotStarted });
            }
            var result = await app.ResourceCommands.ExecuteCommandAsync(frontend.Resource, KnownResourceCommands.StartCommand, TestContext.Current.CancellationToken);
            Assert.True(result.Success, result.Message);
            Assert.Equal(["backend", "frontend"], started.Order(StringComparer.Ordinal));
        }
        Assert.Equal(0, callbacks);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("catalog")]
    [InlineData("CATALOG")]
    [InlineData("basket")]
    public async Task SelectionPreservesFullRunsAndUngroupedResources(string? selection)
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        builder.Configuration["Aspire:LaunchGroup"] = selection;
        builder.DefineLaunchGroups("catalog", "basket");
        var catalog = builder.AddContainer("catalog", "image").WithLaunchGroup("catalog");
        var basket = builder.AddExecutable("basket", "unused", ".").WithLaunchGroup("basket");
        var shared = builder.AddExecutable("shared", "unused", ".").WithLaunchGroups("basket", "catalog");
        var ungrouped = builder.AddExecutable("ungrouped", "unused", ".");
        await using var app = builder.Build();

        await app.ExecuteBeforeStartHooksAsync(TestContext.Current.CancellationToken);

        Assert.Equal(selection == "basket", catalog.Resource.HasAnnotationOfType<ExplicitStartupAnnotation>());
        Assert.Equal(selection is "catalog" or "CATALOG", basket.Resource.HasAnnotationOfType<ExplicitStartupAnnotation>());
        Assert.False(shared.Resource.HasAnnotationOfType<ExplicitStartupAnnotation>());
        Assert.False(ungrouped.Resource.HasAnnotationOfType<ExplicitStartupAnnotation>());
    }

    [Fact]
    public void DefineLaunchGroupsRequiresNames()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var services = builder.Services.ToArray();

        Assert.Throws<ArgumentException>("launchGroupNames", () => builder.DefineLaunchGroups());
        Assert.Throws<ArgumentException>("launchGroupNames", () => builder.DefineLaunchGroups([]));
        Assert.Throws<ArgumentNullException>("launchGroupNames", () => builder.DefineLaunchGroups(null!));
        Assert.Equal(services, builder.Services);
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
    public async Task IncludesTransitiveWaitReferenceAndParentDependencies()
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

        foreach (var resource in new IResource[] { database.Resource, migration.Resource, api.Resource, frontend.Resource })
        {
            Assert.False(resource.HasAnnotationOfType<ExplicitStartupAnnotation>());
        }
        Assert.True(unrelated.Resource.HasAnnotationOfType<ExplicitStartupAnnotation>());
    }

    [Fact]
    public async Task UngroupedChildrenInheritMembershipAndKeepTheirOwnLifecycle()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        builder.Configuration["Aspire:LaunchGroup"] = "catalog";
        builder.DefineLaunchGroups("catalog", "basket");
        var parent = builder.AddContainer("cache", "image").WithLaunchGroup("basket");
        var child = builder.AddExecutable("admin", "unused", ".").WithParentRelationship(parent);
        var overrideChild = builder.AddExecutable("other-admin", "unused", ".")
            .WithParentRelationship(parent).WithLaunchGroup("catalog");
        await using var app = builder.Build();

        await app.ExecuteBeforeStartHooksAsync(TestContext.Current.CancellationToken);

        Assert.True(child.Resource.HasAnnotationOfType<ExplicitStartupAnnotation>());
        Assert.False(overrideChild.Resource.HasAnnotationOfType<ExplicitStartupAnnotation>());
        Assert.False(parent.Resource.HasAnnotationOfType<ExplicitStartupAnnotation>());
    }

    [Fact]
    public async Task ExplicitStartIsPreservedAndDoesNotSelectItsDependencies()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        builder.Configuration["Aspire:LaunchGroup"] = "catalog";
        builder.DefineLaunchGroups("catalog", "basket");
        var dependency = builder.AddContainer("cache", "image").WithLaunchGroup("basket");
        var explicitResource = builder.AddExecutable("explicit", "unused", ".").WithExplicitStart().WaitFor(dependency);
        await using var app = builder.Build();

        await app.ExecuteBeforeStartHooksAsync(TestContext.Current.CancellationToken);

        Assert.True(explicitResource.Resource.HasAnnotationOfType<ExplicitStartupAnnotation>());
        Assert.True(dependency.Resource.HasAnnotationOfType<ExplicitStartupAnnotation>());
    }

    [Fact]
    public async Task ConfigurationAndCodeDeclarationsAreCombined()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        builder.Configuration["Aspire:LaunchGroups:0"] = "catalog";
        builder.Configuration["Aspire:LaunchGroup"] = "BASKET";
        builder.DefineLaunchGroups("basket").DefineLaunchGroups("BASKET");
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
        builder.DefineLaunchGroups("catalog", "basket");
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
        builder.DefineLaunchGroups("catalog");
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
        var declarationException = Assert.ThrowsAny<ArgumentException>(() => builder.DefineLaunchGroups("valid", name!));
        var singleGroupException = Assert.ThrowsAny<ArgumentException>(() => resource.WithLaunchGroup(name!));
        var multipleGroupsException = Assert.ThrowsAny<ArgumentException>(() => resource.WithLaunchGroups("valid", name!));

        Assert.Equal("launchGroupName", annotationException.ParamName);
        Assert.Equal("launchGroupNames", declarationException.ParamName);
        Assert.Equal("launchGroupName", singleGroupException.ParamName);
        Assert.Equal("launchGroupNames", multipleGroupsException.ParamName);
        Assert.All(new[] { annotationException, declarationException, singleGroupException, multipleGroupsException },
            exception => Assert.Equal(name is null ? typeof(ArgumentNullException) : typeof(ArgumentException), exception.GetType()));
        Assert.Empty(resource.Resource.Annotations.OfType<LaunchGroupAnnotation>());
    }

    [Fact]
    public async Task PublishingIgnoresLaunchGroupSelection()
    {
        using var builder = TestDistributedApplicationBuilder.Create(DistributedApplicationOperation.Publish);
        builder.Configuration["Aspire:LaunchGroup"] = "invalid";
        builder.DefineLaunchGroups("catalog");
        var resource = builder.AddContainer("catalog", "image").WithLaunchGroup("catalog");
        await using var app = builder.Build();

        app.Services.GetRequiredService<LaunchGroupService>().Configure(app.Services.GetRequiredService<DistributedApplicationModel>());

        Assert.False(resource.Resource.HasAnnotationOfType<ExplicitStartupAnnotation>());
    }

    [Fact]
    public void ReferenceCyclesHaveAFiniteClosure()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var first = builder.AddExecutable("first", "unused", ".");
        var second = builder.AddExecutable("second", "unused", ".");
        first.WithReferenceRelationship(second);
        second.WithReferenceRelationship(first);

        Assert.Equal([second.Resource, first.Resource], LaunchGroupService.GetClosure([first.Resource]));
    }

    [Fact]
    public async Task PartialRunsDeferPersistentExplicitResourceConfiguration()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        builder.Configuration["Aspire:LaunchGroup"] = "catalog";
        builder.DefineLaunchGroups("catalog", "basket");
#pragma warning disable ASPIREPERSISTENCE001
        var resource = builder.AddExecutable("worker", "unused", ".")
            .WithPersistentLifetime().WithLaunchGroup("basket");
#pragma warning restore ASPIREPERSISTENCE001
        await using var app = builder.Build();

        await app.ExecuteBeforeStartHooksAsync(TestContext.Current.CancellationToken);

        Assert.True(DcpModelUtilities.ShouldDeferCreateForExplicitStart(resource.Resource, start: false));
        Assert.False(DcpModelUtilities.ShouldDeferCreateForExplicitStart(resource.Resource, start: true));
    }
}
