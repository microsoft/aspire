// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREEXTENSION001, ASPIREPROJECTS001, ASPIREPERSISTENCE001

using System.Runtime.CompilerServices;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Dotnet.Watch;
using Aspire.Hosting.Tests.Utils;
using Aspire.Hosting.Utils;
using Microsoft.Extensions.DependencyInjection;

namespace Aspire.Hosting.Dotnet.Tests;

// Which projects `aspire run --watch` watches, opts out or rejects (docs/plans/project-v2-csharpprogram-watch.md, section 5).
public class DotnetWatchEligibilityTests(ITestOutputHelper outputHelper)
{
    private static readonly string[] s_watchConfig =
        ["AppHost:Run:WatchEnabled=true", $"{DotnetWatchCoordinator.ProxyPathConfigurationKey}=/opt/aspire/aspire.dll", $"{DotnetWatchCoordinator.ProxyProtocolConfigurationKey}={DotnetWatchCoordinator.RequiredProxyProtocol}"];

    [Fact]
    public void WithWatchReplacesEarlierChoice()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        using var builder = TestDistributedApplicationBuilder.Create();

        var service = AddProject(builder, workspace, "service").WithWatch(false).WithWatch();

        var annotation = Assert.Single(service.Resource.Annotations.OfType<DotnetProjectWatchAnnotation>());
        Assert.True(annotation.Enabled);
    }

    [Fact]
    public async Task OptedOutProjectRunsWithoutProxyAndOthersStayWatched()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        using var builder = TestDistributedApplicationBuilder.Create(s_watchConfig);

        var watched = AddProject(builder, workspace, "watched");
        var plain = AddProject(builder, workspace, "plain").WithWatch(false);

        using var app = builder.Build();
        await ExecuteBeforeStartHooksAsync(app, CancellationToken.None);

        var server = Assert.Single(Model(app).Resources.OfType<DotnetWatchServerResource>());
        Assert.Contains(DotnetWatchCoordinator.ProxyCommandName, await ArgumentEvaluator.GetArgumentListAsync(watched.Resource));
        var plainArgs = await ArgumentEvaluator.GetArgumentListAsync(plain.Resource);
        Assert.DoesNotContain(DotnetWatchCoordinator.ProxyCommandName, plainArgs);
        Assert.Equal("run", plainArgs[0]);

        // The opted-out project still waits for the watcher's initial build so the two builds don't race.
        Assert.Contains(plain.Resource.Annotations.OfType<WaitAnnotation>(), w => w.Resource == server);
    }

    [Fact]
    public async Task AllProjectsOptedOutRemovesServer()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        using var builder = TestDistributedApplicationBuilder.Create(s_watchConfig);

        var plain = AddProject(builder, workspace, "plain").WithWatch(false);

        using var app = builder.Build();
        await ExecuteBeforeStartHooksAsync(app, CancellationToken.None);

        Assert.DoesNotContain(Model(app).Resources, r => r is DotnetWatchServerResource);
        Assert.DoesNotContain(plain.Resource.Annotations.OfType<WaitAnnotation>(), w => w.Resource is DotnetWatchServerResource);
        Assert.DoesNotContain(DotnetWatchCoordinator.ProxyCommandName, await ArgumentEvaluator.GetArgumentListAsync(plain.Resource));
    }

    [Fact]
    public async Task ProjectAwaitedForCompletionIsExcluded()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        using var builder = TestDistributedApplicationBuilder.Create(s_watchConfig);

        var migrations = AddProject(builder, workspace, "migrations");
        var api = AddProject(builder, workspace, "api").WaitForCompletion(migrations);

        Assert.Equal(DotnetWatchCoordinator.WatchDecisionKind.Excluded,
            DotnetWatchCoordinator.Evaluate(migrations.Resource, Metadata(migrations), builder.Resources).Kind);

        using var app = builder.Build();
        await ExecuteBeforeStartHooksAsync(app, CancellationToken.None);

        Assert.DoesNotContain(DotnetWatchCoordinator.ProxyCommandName, await ArgumentEvaluator.GetArgumentListAsync(migrations.Resource));
        Assert.Contains(DotnetWatchCoordinator.ProxyCommandName, await ArgumentEvaluator.GetArgumentListAsync(api.Resource));
    }

    [Fact]
    public async Task ExplicitWatchOnProjectAwaitedForCompletionFails()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        using var builder = TestDistributedApplicationBuilder.Create(s_watchConfig);

        var migrations = AddProject(builder, workspace, "migrations").WithWatch(true);
        AddProject(builder, workspace, "api").WaitForCompletion(migrations);

        var ex = await AssertBeforeStartFailsAsync(builder);
        Assert.Contains("'migrations'", ex.Message);
        Assert.Contains("'api'", ex.Message);
        Assert.Contains("WaitForCompletion", ex.Message);
    }

    [Fact]
    public async Task FileBasedAppFailsUnlessOptedOut()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var scriptPath = Path.Combine(workspace.Path, "ticker.cs");
        File.WriteAllText(scriptPath, "Console.WriteLine(1);");

        using (var builder = TestDistributedApplicationBuilder.Create(s_watchConfig))
        {
            AddProject(builder, workspace, "service");
            builder.AddDotnetProject("ticker", scriptPath);

            var ex = await AssertBeforeStartFailsAsync(builder);
            Assert.Contains("'ticker' is a file-based C# app", ex.Message);
            Assert.Contains("WithWatch(false)", ex.Message);
        }

        using (var builder = TestDistributedApplicationBuilder.Create(s_watchConfig))
        {
            var service = AddProject(builder, workspace, "service");
            var ticker = builder.AddDotnetProject("ticker", scriptPath).WithWatch(false);

            using var app = builder.Build();
            await ExecuteBeforeStartHooksAsync(app, CancellationToken.None);

            Assert.Contains(DotnetWatchCoordinator.ProxyCommandName, await ArgumentEvaluator.GetArgumentListAsync(service.Resource));
            var tickerArgs = await ArgumentEvaluator.GetArgumentListAsync(ticker.Resource);
            Assert.DoesNotContain(DotnetWatchCoordinator.ProxyCommandName, tickerArgs);
            Assert.Contains(scriptPath, tickerArgs);
        }
    }

    [Theory]
    [InlineData(PersistenceMode.Persistent)]
    [InlineData(PersistenceMode.ParentProcess)]
    public async Task PersistentProjectFails(PersistenceMode mode)
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        using var builder = TestDistributedApplicationBuilder.Create(s_watchConfig);

        AddProject(builder, workspace, "service").WithAnnotation(new PersistenceAnnotation { Mode = mode });

        var ex = await AssertBeforeStartFailsAsync(builder);
        Assert.Contains("'service' has a persistent lifetime", ex.Message);
    }

    [Fact]
    public void PersistenceFollowsSourceResourceAndContainerLifetime()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        using var builder = TestDistributedApplicationBuilder.Create(s_watchConfig);

        var db = builder.AddContainer("db", "postgres").WithLifetime(ContainerLifetime.Persistent);
        var service = AddProject(builder, workspace, "service")
            .WithAnnotation(new PersistenceAnnotation { Mode = PersistenceMode.Resource, SourceResource = db.Resource });
        var session = AddProject(builder, workspace, "session")
            .WithAnnotation(new PersistenceAnnotation { Mode = PersistenceMode.Session });

        Assert.Equal(DotnetWatchCoordinator.WatchDecisionKind.Rejected,
            DotnetWatchCoordinator.Evaluate(service.Resource, Metadata(service), builder.Resources).Kind);
        Assert.Equal(DotnetWatchCoordinator.WatchDecisionKind.Watched,
            DotnetWatchCoordinator.Evaluate(session.Resource, Metadata(session), builder.Resources).Kind);
    }

    [Fact]
    public async Task CustomLaunchFailsUnlessOptedOut()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);

        using (var builder = TestDistributedApplicationBuilder.Create(s_watchConfig))
        {
            AddProject(builder, workspace, "gateway").WithLaunchToolArgs(ctx => ctx.Args.Add("gateway"));
            AddProject(builder, workspace, "maui").WithAnnotation(new ProjectLaunchArgsOverrideAnnotation(["build", "-t:Run"]));

            var ex = await AssertBeforeStartFailsAsync(builder);
            Assert.Contains("'gateway' uses a custom launch", ex.Message);
            Assert.Contains("'maui' uses a custom launch", ex.Message);
        }

        using (var builder = TestDistributedApplicationBuilder.Create(s_watchConfig))
        {
            var gateway = AddProject(builder, workspace, "gateway").WithLaunchToolArgs(ctx => ctx.Args.Add("gateway")).WithWatch(false);

            using var app = builder.Build();
            await ExecuteBeforeStartHooksAsync(app, CancellationToken.None);

            Assert.Contains("gateway", await ArgumentEvaluator.GetArgumentListAsync(gateway.Resource));
            Assert.DoesNotContain(DotnetWatchCoordinator.ProxyCommandName, await ArgumentEvaluator.GetArgumentListAsync(gateway.Resource));
            Assert.DoesNotContain(Model(app).Resources, r => r is DotnetWatchServerResource);
        }
    }

    [Fact]
    public async Task UnderIdeNoProjectIsWatchedAndRunSucceeds()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        using var builder = TestDistributedApplicationBuilder.Create([.. s_watchConfig, "DEBUG_SESSION_PORT=12345"]);

        var service = AddProject(builder, workspace, "service");

        using var app = builder.Build();
        await ExecuteBeforeStartHooksAsync(app, CancellationToken.None);

        Assert.DoesNotContain(Model(app).Resources, r => r is DotnetWatchServerResource);
        Assert.DoesNotContain(DotnetWatchCoordinator.ProxyCommandName, await ArgumentEvaluator.GetArgumentListAsync(service.Resource));
    }

    private static IResourceBuilder<DotnetProjectResource> AddProject(IDistributedApplicationBuilder builder, TemporaryWorkspace workspace, string name)
    {
        var path = Path.Combine(workspace.Path, name, $"{name}.csproj");
        DotnetWatchCoordinatorTests.CreateProjectFile(path);
        return builder.AddDotnetProject(name, Path.GetDirectoryName(path)!);
    }

    private static IProjectMetadata Metadata(IResourceBuilder<DotnetProjectResource> resource) =>
        resource.Resource.Annotations.OfType<IProjectMetadata>().Single();

    private static DistributedApplicationModel Model(DistributedApplication app) =>
        app.Services.GetRequiredService<DistributedApplicationModel>();

    private static async Task<DistributedApplicationException> AssertBeforeStartFailsAsync(IDistributedApplicationBuilder builder)
    {
        using var app = builder.Build();
        return await Assert.ThrowsAsync<DistributedApplicationException>(() => ExecuteBeforeStartHooksAsync(app, CancellationToken.None));
    }

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "ExecuteBeforeStartHooksAsync")]
    private static extern Task ExecuteBeforeStartHooksAsync(DistributedApplication app, CancellationToken cancellationToken);
}
