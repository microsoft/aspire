// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIRELAUNCHGROUPS001 // Tests the experimental launch-group APIs.

using Aspire.Hosting.Backchannel;
using Aspire.Hosting.Orchestrator;
using Aspire.Hosting.Utils;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;

namespace Aspire.Hosting.Tests;

[Trait("Partition", "2")]
public class LaunchGroupMetadataTests
{
    [Fact]
    public async Task MetadataReportsEffectiveMembershipAndBothReasonsWithoutEvaluatingCallbacks()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        builder.Configuration["Aspire:LaunchGroups:0"] = "frontend";
        builder.Configuration["Aspire:LaunchGroups:1"] = "backend";
        builder.Configuration["Aspire:LaunchGroups:2"] = "empty";
        builder.Configuration["Aspire:LaunchGroup"] = "FRONTEND";
        var callbacks = 0;
        builder.AddExecutable("frontend", "unused", ".")
            .WithLaunchGroup("Frontend")
            .WithEnvironment(_ => callbacks++);
        var server = builder.AddPostgres("server").WithLaunchGroup("frontend");
        server.AddDatabase("database");
        builder.AddExecutable("explicit", "unused", ".")
            .WithLaunchGroup("backend")
            .WithExplicitStart();
        builder.AddExecutable("helper", "unused", ".")
            .WithAnnotation(new ExcludeFromLaunchGroupSelectionAnnotation());
        await using var app = builder.Build();

        await app.ExecuteBeforeStartHooksAsync(TestContext.Current.CancellationToken);
        var info = app.Services.GetRequiredService<LaunchGroupMetadataService>().GetInformation();

        Assert.Equal("FRONTEND", info.SelectedGroup);
        Assert.Equal(["backend", "empty", "frontend"], info.Groups);
        Assert.Equal(["database", "explicit", "frontend", "helper", "server"], info.Resources.Select(r => r.Name));
        var database = Assert.Single(info.Resources, r => r.Name == "database");
        Assert.Equal(["frontend"], database.Groups);
        Assert.Equal("server", database.InheritedFrom);
        Assert.False(database.ExplicitStart);
        Assert.Equal(LaunchGroupExclusionReason.None, database.ExclusionReason);
        var explicitResource = Assert.Single(info.Resources, r => r.Name == "explicit");
        Assert.True(explicitResource.ExplicitStart);
        Assert.Equal(LaunchGroupExclusionReason.NotInSelectedGroup, explicitResource.ExclusionReason);
        var helper = Assert.Single(info.Resources, r => r.Name == "helper");
        Assert.Equal(LaunchGroupExclusionReason.ExemptFromSelection, helper.ExclusionReason);
        Assert.Equal(LaunchGroupBuildType.NotCoordinated, helper.BuildType);
        Assert.Equal(0, callbacks);
    }

    [Fact]
    public async Task AppHostWithoutGroupsStillReportsSupportedEmptyMetadata()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        builder.AddExecutable("automatic", "unused", ".");
        builder.AddExecutable("explicit", "unused", ".").WithExplicitStart();
        await using var app = builder.Build();

        await app.ExecuteBeforeStartHooksAsync(TestContext.Current.CancellationToken);
        var info = app.Services.GetRequiredService<LaunchGroupMetadataService>().GetInformation();

        Assert.Null(info.SelectedGroup);
        Assert.Empty(info.Groups);
        Assert.Equal(["automatic", "explicit"], info.Resources.Select(r => r.Name));
        Assert.False(info.Resources[0].ExplicitStart);
        Assert.True(info.Resources[1].ExplicitStart);
        Assert.All(info.Resources, resource => Assert.Equal(LaunchGroupExclusionReason.None, resource.ExclusionReason));
    }

    [Fact]
    public async Task StartupSummarySeparatesSelectionReasonsWithoutLoggingValuesOrHiddenResources()
    {
        var sink = new TestSink();
        using var builder = TestDistributedApplicationBuilder.Create();
        builder.Services.AddSingleton<ILogger<LaunchGroupMetadataService>>(
            new TestLogger<LaunchGroupMetadataService>(new TestLoggerFactory(sink, enabled: true)));
        builder.Configuration["Aspire:LaunchGroup"] = "frontend";
        builder.AddExecutable("frontend", "unused", ".")
            .WithLaunchGroup("frontend")
            .WithArgs("argument-secret")
            .WithEnvironment("PASSWORD", "environment-secret");
        builder.AddExecutable("backend", "unused", ".").WithLaunchGroup("backend");
        builder.AddExecutable("manual", "unused", ".").WithLaunchGroup("backend").WithExplicitStart();
        builder.AddExecutable("hidden", "unused", ".")
            .WithAnnotation(new HiddenAnnotation(HiddenBehavior.Always));
        await using var app = builder.Build();

        await app.ExecuteBeforeStartHooksAsync(TestContext.Current.CancellationToken);

        Assert.Equal(
            [
                "Launch group 'frontend' (visible resources): eligible for automatic startup: frontend; excluded by group: backend, manual; explicit start: manual.",
                "Coordinated builds: initial: (none); eager for persistent resources: (none); shared with initial builds: (none); deferred: (none). AppHost project-reference builds may also run eagerly."
            ],
            sink.Writes.Select(write => write.Message));
    }
}
