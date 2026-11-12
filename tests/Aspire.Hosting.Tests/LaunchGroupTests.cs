// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIRELAUNCHGROUPS001 // Tests the experimental launch-group APIs.

using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Aspire.Dashboard.Model;
using Aspire.Hosting.Orchestrator;
using Aspire.Hosting.Pipelines;
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
            [
                nameof(LaunchGroupExtensions.WithLaunchGroups),
                nameof(LaunchGroupExtensions.WithOptionalLaunchGroupReference)
            ],
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

    [Theory]
    [InlineData("backend", false)]
    [InlineData("frontend", true)]
    public async Task LegacyProjectRespectsLaunchGroupSelection(string selectedGroup, bool explicitStart)
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        builder.Configuration["Aspire:LaunchGroup"] = selectedGroup;
#pragma warning disable CS0618 // Exercise the legacy ProjectResource path.
        var legacy = builder.AddProject<Projects.ServiceA>("legacy").WithLaunchGroups("backend");
#pragma warning restore CS0618
        builder.AddExecutable("frontend", "unused", ".").WithLaunchGroups("frontend");
        var rebuilder = Assert.Single(builder.Resources.OfType<ProjectRebuilderResource>());
        await using var app = builder.Build();

        await app.ExecuteBeforeStartHooksAsync(TestContext.Current.CancellationToken);

        Assert.Equal(explicitStart, legacy.Resource.HasAnnotationOfType<ExplicitStartupAnnotation>());
        Assert.Same(legacy.Resource, rebuilder.Parent);
        Assert.True(rebuilder.HasAnnotationOfType<ExplicitStartupAnnotation>());
    }

    [Fact]
    public async Task BeforeStartMembershipsAreIncludedInSelectionAndMetadata()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        builder.Configuration["Aspire:LaunchGroup"] = "frontend";
        var frontend = builder.AddExecutable("frontend", "unused", ".").WithLaunchGroups("frontend");
        var early = builder.AddExecutable("early", "unused", ".");
        var other = builder.AddExecutable("other", "unused", ".");
        builder.OnBeforeStart((_, _) =>
        {
            frontend.WithLaunchGroups("late-group");
            early.WithLaunchGroups("frontend");
            other.WithLaunchGroups("late-group");
            return Task.CompletedTask;
        });
        await using var app = builder.Build();

        await app.ExecuteBeforeStartHooksAsync(TestContext.Current.CancellationToken);

        var info = app.Services.GetRequiredService<LaunchGroupMetadataService>().GetInformation();
        var frontendInfo = Assert.Single(info.Resources, resource => resource.Name == "frontend");
        var earlyInfo = Assert.Single(info.Resources, resource => resource.Name == "early");
        var otherInfo = Assert.Single(info.Resources, resource => resource.Name == "other");
        Assert.Multiple(
            () => Assert.Equal(["frontend", "late-group"], info.Groups),
            () => Assert.Equal(["frontend", "late-group"], frontendInfo.Groups),
            () => Assert.Equal(["frontend"], earlyInfo.Groups),
            () => Assert.Equal(LaunchGroupExclusionReason.None, earlyInfo.ExclusionReason),
            () => Assert.False(early.Resource.HasAnnotationOfType<ExplicitStartupAnnotation>()),
            () => Assert.Equal(["late-group"], otherInfo.Groups),
            () => Assert.Equal(LaunchGroupExclusionReason.NotInSelectedGroup, otherInfo.ExclusionReason),
            () => Assert.True(other.Resource.HasAnnotationOfType<ExplicitStartupAnnotation>()));
    }

    [Fact]
    public async Task LateRegistrationsKeepSelectionAndMetadataConsistent()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        builder.Configuration["Aspire:LaunchGroup"] = "frontend";
        var frontend = builder.AddExecutable("frontend", "unused", ".").WithLaunchGroups("frontend");
        var early = builder.AddExecutable("early", "unused", ".");
        var late = new ExecutableResource("late", "unused", ".");
        late.Annotations.Add(new LaunchGroupAnnotation("late-group"));
        builder.Pipeline.WithFinalAction(WellKnownPipelineSteps.BeforeStart, context =>
        {
            frontend.WithLaunchGroups("late-group");
            early.WithLaunchGroups("frontend");
            context.Model.Resources.Add(late);
            return Task.CompletedTask;
        });
        await using var app = builder.Build();

        await app.ExecuteBeforeStartHooksAsync(TestContext.Current.CancellationToken);

        var info = app.Services.GetRequiredService<LaunchGroupMetadataService>().GetInformation();
        var frontendInfo = Assert.Single(info.Resources, resource => resource.Name == "frontend");
        var earlyInfo = Assert.Single(info.Resources, resource => resource.Name == "early");
        var lateInfo = Assert.Single(info.Resources, resource => resource.Name == "late");
        Assert.Multiple(
            () => Assert.Equal(["frontend", "late-group"], info.Groups),
            () => Assert.Equal(["frontend", "late-group"], frontendInfo.Groups),
            () => Assert.Equal(["frontend"], earlyInfo.Groups),
            () => Assert.Equal(LaunchGroupExclusionReason.None, earlyInfo.ExclusionReason),
            () => Assert.False(early.Resource.HasAnnotationOfType<ExplicitStartupAnnotation>()),
            () => Assert.Equal(["late-group"], lateInfo.Groups),
            () => Assert.Equal(LaunchGroupExclusionReason.NotInSelectedGroup, lateInfo.ExclusionReason),
            () => Assert.True(late.HasAnnotationOfType<ExplicitStartupAnnotation>()));
    }

    [Fact]
    public async Task SelectedGroupCanBeInferredFromFinalActionMembership()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        builder.Configuration["Aspire:LaunchGroup"] = "frontend";
        var frontend = builder.AddExecutable("frontend", "unused", ".");
        var other = builder.AddExecutable("other", "unused", ".");
        builder.Pipeline.WithFinalAction(WellKnownPipelineSteps.BeforeStart, _ =>
        {
            frontend.WithLaunchGroups("frontend");
            return Task.CompletedTask;
        });
        await using var app = builder.Build();

        await app.ExecuteBeforeStartHooksAsync(TestContext.Current.CancellationToken);

        var info = app.Services.GetRequiredService<LaunchGroupMetadataService>().GetInformation();
        var frontendInfo = Assert.Single(info.Resources, resource => resource.Name == "frontend");
        var otherInfo = Assert.Single(info.Resources, resource => resource.Name == "other");
        Assert.Multiple(
            () => Assert.Equal("frontend", info.SelectedGroup),
            () => Assert.Equal(["frontend"], info.Groups),
            () => Assert.Equal(["frontend"], frontendInfo.Groups),
            () => Assert.Equal(LaunchGroupExclusionReason.None, frontendInfo.ExclusionReason),
            () => Assert.False(frontend.Resource.HasAnnotationOfType<ExplicitStartupAnnotation>()),
            () => Assert.Equal(LaunchGroupExclusionReason.NotInSelectedGroup, otherInfo.ExclusionReason),
            () => Assert.True(other.Resource.HasAnnotationOfType<ExplicitStartupAnnotation>()));
    }

    [Fact]
    public async Task FinalActionRequiredDependencyFailsPreflightWithoutEvaluatingCallbacks()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        builder.Configuration["Aspire:LaunchGroup"] = "frontend";
        var frontend = builder.AddExecutable("frontend", "unused", ".").WithLaunchGroups("frontend");
        var backend = builder.AddExecutable("backend", "unused", ".").WithLaunchGroups("backend");
        var argumentCallbacks = 0;
        var environmentCallbacks = 0;
        var finalActionRan = false;
        frontend.WithArgs(_ => Interlocked.Increment(ref argumentCallbacks))
            .WithEnvironment(_ => Interlocked.Increment(ref environmentCallbacks));
        builder.Pipeline.WithFinalAction(WellKnownPipelineSteps.BeforeStart, _ =>
        {
            finalActionRan = true;
            frontend.WithReferenceRelationship(backend);
            return Task.CompletedTask;
        });
        await using var app = builder.Build();

        var exception = await Assert.ThrowsAsync<DistributedApplicationException>(
            () => app.ExecuteBeforeStartHooksAsync(TestContext.Current.CancellationToken));

        Assert.Multiple(
            () => Assert.True(finalActionRan),
            () => Assert.Equal(
                "Resource 'frontend' requires local resource 'backend', which is not included in launch group 'frontend'. " +
                "Dependency path: frontend -> backend. Edges: frontend --reference--> backend. " +
                "Add 'backend' to launch group 'frontend'. " +
                "If the reference from 'frontend' to 'backend' is optional, mark it with WithOptionalLaunchGroupReference.",
                exception.Message),
            () => Assert.Equal(0, argumentCallbacks),
            () => Assert.Equal(0, environmentCallbacks));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FinalActionExplicitStartIsPreservedForPreviouslyExcludedResource(bool becomesSelected)
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        builder.Configuration["Aspire:LaunchGroup"] = "frontend";
        builder.AddExecutable("frontend", "unused", ".").WithLaunchGroups("frontend");
        var backend = builder.AddExecutable("backend", "unused", ".").WithLaunchGroups("backend");
        var explicitStart = new ExplicitStartupAnnotation();
        builder.Pipeline.WithFinalAction(WellKnownPipelineSteps.BeforeStart, _ =>
        {
            backend.WithAnnotation(explicitStart);
            if (becomesSelected)
            {
                backend.WithLaunchGroups("frontend");
            }
            return Task.CompletedTask;
        });
        await using var app = builder.Build();

        await app.ExecuteBeforeStartHooksAsync(TestContext.Current.CancellationToken);

        var info = app.Services.GetRequiredService<LaunchGroupMetadataService>().GetInformation();
        var backendInfo = Assert.Single(info.Resources, resource => resource.Name == "backend");
        Assert.Multiple(
            () => Assert.Equal(becomesSelected ? new[] { "backend", "frontend" } : ["backend"], backendInfo.Groups),
            () => Assert.Equal(!becomesSelected ? LaunchGroupExclusionReason.NotInSelectedGroup : LaunchGroupExclusionReason.None, backendInfo.ExclusionReason),
            () => Assert.True(backendInfo.ExplicitStart),
            () => Assert.Contains(explicitStart, backend.Resource.Annotations));
    }

    [Fact]
    public async Task RepeatedBeforeStartRefreshesSelectionAndPreservesUserExplicitStart()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        builder.Configuration["Aspire:LaunchGroup"] = "frontend";
        builder.AddExecutable("frontend", "unused", ".").WithLaunchGroups("frontend");
        var promoted = builder.AddExecutable("promoted", "unused", ".").WithLaunchGroups("backend");
        var manual = builder.AddExecutable("manual", "unused", ".").WithLaunchGroups("backend");
        await using var app = builder.Build();

        await app.ExecuteBeforeStartHooksAsync(TestContext.Current.CancellationToken);
        var explicitStart = new ExplicitStartupAnnotation();
        promoted.WithLaunchGroups("frontend");
        manual.WithLaunchGroups("frontend").WithAnnotation(explicitStart);
        await app.ExecuteBeforeStartHooksAsync(TestContext.Current.CancellationToken);

        var information = app.Services.GetRequiredService<LaunchGroupMetadataService>().GetInformation();
        var promotedInfo = Assert.Single(information.Resources, resource => resource.Name == "promoted");
        var manualInfo = Assert.Single(information.Resources, resource => resource.Name == "manual");
        Assert.Multiple(
            () => Assert.False(promoted.Resource.HasAnnotationOfType<ExplicitStartupAnnotation>()),
            () => Assert.Equal(LaunchGroupExclusionReason.None, promotedInfo.ExclusionReason),
            () => Assert.False(promotedInfo.ExplicitStart),
            () => Assert.Same(explicitStart, Assert.Single(manual.Resource.Annotations.OfType<ExplicitStartupAnnotation>())),
            () => Assert.Equal(LaunchGroupExclusionReason.None, manualInfo.ExclusionReason),
            () => Assert.True(manualInfo.ExplicitStart));
    }

    [Fact]
    public async Task FailedRepeatedBeforeStartPreservesSelectionAndMetadata()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        builder.Configuration["Aspire:LaunchGroup"] = "frontend";
        var frontend = builder.AddExecutable("frontend", "unused", ".").WithLaunchGroups("frontend");
        var backend = builder.AddExecutable("backend", "unused", ".").WithLaunchGroups("backend");
        await using var app = builder.Build();

        await app.ExecuteBeforeStartHooksAsync(TestContext.Current.CancellationToken);
        var metadata = app.Services.GetRequiredService<LaunchGroupMetadataService>();
        var originalInformation = metadata.GetInformation();
        var originalAnnotation = Assert.Single(backend.Resource.Annotations.OfType<ExplicitStartupAnnotation>());
        frontend.WithReferenceRelationship(backend);

        var exception = await Assert.ThrowsAsync<DistributedApplicationException>(
            () => app.ExecuteBeforeStartHooksAsync(TestContext.Current.CancellationToken));

        Assert.Contains("frontend -> backend", exception.Message);
        Assert.Same(originalInformation, metadata.GetInformation());
        Assert.Same(originalAnnotation, Assert.Single(backend.Resource.Annotations.OfType<ExplicitStartupAnnotation>()));
        Assert.False(frontend.Resource.HasAnnotationOfType<ExplicitStartupAnnotation>());
    }

    [Fact]
    public async Task ClearingSelectionRemovesOnlySyntheticExplicitStart()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        builder.Configuration["Aspire:LaunchGroup"] = "frontend";
        builder.AddExecutable("frontend", "unused", ".").WithLaunchGroups("frontend");
        var automatic = builder.AddExecutable("automatic", "unused", ".").WithLaunchGroups("backend");
        var manual = builder.AddExecutable("manual", "unused", ".").WithLaunchGroups("backend");
        var explicitStart = new ExplicitStartupAnnotation();
        manual.WithAnnotation(explicitStart);
        await using var app = builder.Build();

        await app.ExecuteBeforeStartHooksAsync(TestContext.Current.CancellationToken);
        builder.Configuration["Aspire:LaunchGroup"] = null;
        await app.ExecuteBeforeStartHooksAsync(TestContext.Current.CancellationToken);

        var information = app.Services.GetRequiredService<LaunchGroupMetadataService>().GetInformation();
        var automaticInfo = Assert.Single(information.Resources, resource => resource.Name == "automatic");
        var manualInfo = Assert.Single(information.Resources, resource => resource.Name == "manual");
        Assert.Multiple(
            () => Assert.Null(information.SelectedGroup),
            () => Assert.False(automatic.Resource.HasAnnotationOfType<ExplicitStartupAnnotation>()),
            () => Assert.Equal(LaunchGroupExclusionReason.None, automaticInfo.ExclusionReason),
            () => Assert.False(automaticInfo.ExplicitStart),
            () => Assert.Same(explicitStart, Assert.Single(manual.Resource.Annotations.OfType<ExplicitStartupAnnotation>())),
            () => Assert.True(manualInfo.ExplicitStart));
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

        var information = app.Services.GetRequiredService<LaunchGroupMetadataService>().GetInformation();
        Assert.Null(information.SelectedGroup);
        Assert.Empty(information.Groups);
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
            resource.WithLaunchGroups("catalog");
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
            resource.WithLaunchGroups("catalog");
        }
        await using var app = builder.Build();

        await app.ExecuteBeforeStartHooksAsync(TestContext.Current.CancellationToken);

        Assert.Null(app.Services.GetRequiredService<LaunchGroupMetadataService>().GetInformationOrDefault());
        Assert.False(resource.Resource.HasAnnotationOfType<ExplicitStartupAnnotation>());
    }

    [Theory]
    [InlineData("\"local\"")]
    [InlineData("[null]")]
    [InlineData("[[\"local\"]]")]
    [InlineData("{\"name\":\"local\"}")]
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

        Assert.Null(app.Services.GetRequiredService<LaunchGroupMetadataService>().GetInformationOrDefault());
        Assert.False(resource.Resource.HasAnnotationOfType<ExplicitStartupAnnotation>());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingReferencedResourcesFailWithoutEvaluatingCallbacks(bool expression)
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        builder.Configuration["Aspire:LaunchGroups:0"] = "frontend";
        builder.Configuration["Aspire:LaunchGroups:1"] = "backend";
        builder.Configuration["Aspire:LaunchGroup"] = "frontend";
        var dependency = builder.AddExecutable("backend", "unused", ".")
            .WithHttpEndpoint().WithLaunchGroups("backend");
        var endpoint = dependency.GetEndpoint("http");
        object argument = expression ? ReferenceExpression.Create($"--server={endpoint}") : endpoint;
        var frontend = builder.AddExecutable("frontend", "unused", ".", new object[] { argument }).WithLaunchGroups("frontend");
        var callbacks = 0;
        frontend.WithArgs(context => { Interlocked.Increment(ref callbacks); });
        await using var app = builder.Build();

        var exception = await Assert.ThrowsAsync<DistributedApplicationException>(
            () => app.ExecuteBeforeStartHooksAsync(TestContext.Current.CancellationToken));

        Assert.Contains("frontend", exception.Message);
        Assert.Contains("backend", exception.Message);
        Assert.False(dependency.Resource.HasAnnotationOfType<ExplicitStartupAnnotation>());
        Assert.False(frontend.Resource.HasAnnotationOfType<ExplicitStartupAnnotation>());
        Assert.Equal(0, callbacks);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task CallbackOnlyReferencesCanBeDeclaredWithoutEvaluatingCallbacks(bool argumentCallback, bool declareRelationship)
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        builder.Configuration["Aspire:LaunchGroup"] = "frontend";
        var backend = builder.AddExecutable("backend", "unused", ".")
            .WithHttpEndpoint().WithLaunchGroups("backend");
        var frontend = builder.AddExecutable("frontend", "unused", ".").WithLaunchGroups("frontend");
        var callbacks = 0;
        if (argumentCallback)
        {
            frontend.WithArgs(context =>
            {
                Interlocked.Increment(ref callbacks);
                context.Args.Add(backend.GetEndpoint("http"));
            });
        }
        else
        {
            frontend.WithEnvironment(context =>
            {
                Interlocked.Increment(ref callbacks);
                context.EnvironmentVariables["BACKEND"] = backend.GetEndpoint("http");
            });
        }
        if (declareRelationship)
        {
            frontend.WithReferenceRelationship(backend);
        }
        await using var app = builder.Build();

        if (declareRelationship)
        {
            var exception = await Assert.ThrowsAsync<DistributedApplicationException>(
                () => app.ExecuteBeforeStartHooksAsync(TestContext.Current.CancellationToken));
            Assert.Contains("frontend", exception.Message);
            Assert.Contains("backend", exception.Message);
        }
        else
        {
            await app.ExecuteBeforeStartHooksAsync(TestContext.Current.CancellationToken);
            Assert.False(frontend.Resource.HasAnnotationOfType<ExplicitStartupAnnotation>());
            Assert.True(backend.Resource.HasAnnotationOfType<ExplicitStartupAnnotation>());
        }
        Assert.Equal(0, callbacks);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SelectedReferencesDoNotAddReadinessWaits(bool expression)
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        builder.Configuration["Aspire:LaunchGroup"] = "frontend";
        var backend = builder.AddExecutable("backend", "unused", ".")
            .WithHttpEndpoint().WithLaunchGroups("frontend");
        var endpoint = backend.GetEndpoint("http");
        object argument = expression ? ReferenceExpression.Create($"--server={endpoint}") : endpoint;
        var frontend = builder.AddExecutable("frontend", "unused", ".", new object[] { argument }).WithLaunchGroups("frontend");
        var callbacks = 0;
        frontend.WithArgs(context => Interlocked.Increment(ref callbacks));
        await using var app = builder.Build();

        await app.ExecuteBeforeStartHooksAsync(TestContext.Current.CancellationToken);

        var relationship = Assert.Single(frontend.Resource.Annotations.OfType<ResourceRelationshipAnnotation>());
        Assert.Same(backend.Resource, relationship.Resource);
        Assert.Equal(KnownRelationshipTypes.Reference, relationship.Type);
        Assert.Empty(frontend.Resource.Annotations.OfType<WaitAnnotation>());
        Assert.False(frontend.Resource.HasAnnotationOfType<ExplicitStartupAnnotation>());
        Assert.False(backend.Resource.HasAnnotationOfType<ExplicitStartupAnnotation>());
        Assert.Equal(0, callbacks);
    }

    [Fact]
    public async Task OptionalExecutionDependencyCanBeExcludedFromInitialSelection()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        builder.Configuration["Aspire:LaunchGroup"] = "frontend";
        var backend = builder.AddExecutable("backend", "unused", ".").WithLaunchGroups("backend");
        var frontend = builder.AddExecutable("frontend", "unused", ".")
            .WithLaunchGroups("frontend")
            .WithOptionalLaunchGroupReference(backend);
        var callbacks = 0;
        frontend.WithArgs(_ => Interlocked.Increment(ref callbacks));
        await using var app = builder.Build();

        await app.ExecuteBeforeStartHooksAsync(TestContext.Current.CancellationToken);

        Assert.False(frontend.Resource.HasAnnotationOfType<ExplicitStartupAnnotation>());
        Assert.True(backend.Resource.HasAnnotationOfType<ExplicitStartupAnnotation>());
        Assert.Contains(
            frontend.Resource.Annotations.OfType<ResourceRelationshipAnnotation>(),
            annotation => ReferenceEquals(annotation.Resource, backend.Resource) &&
                annotation.Type == KnownRelationshipTypes.Reference);
        Assert.Equal(0, callbacks);
    }

    [Fact]
    public async Task SelectedResourceRejectsExplicitlyStartedDependencyEvenWhenBothAreMembers()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        builder.Configuration["Aspire:LaunchGroup"] = "frontend";
        var backend = builder.AddExecutable("backend", "unused", ".")
            .WithLaunchGroups("frontend").WithExplicitStart();
        var frontend = builder.AddExecutable("frontend", "unused", ".")
            .WithLaunchGroups("frontend").WithReferenceRelationship(backend);
        await using var app = builder.Build();

        var exception = await Assert.ThrowsAsync<DistributedApplicationException>(
            () => app.ExecuteBeforeStartHooksAsync(TestContext.Current.CancellationToken));

        Assert.Contains("frontend", exception.Message);
        Assert.Contains("backend", exception.Message);
        Assert.Contains("explicit", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(backend.Resource.HasAnnotationOfType<ExplicitStartupAnnotation>());
        Assert.False(frontend.Resource.HasAnnotationOfType<ExplicitStartupAnnotation>());
    }

    [Theory]
    [InlineData(WaitType.WaitUntilHealthy)]
    [InlineData(WaitType.WaitUntilStarted)]
    [InlineData(WaitType.WaitForCompletion)]
    public async Task SelectedResourceRejectsMissingWaitDependency(WaitType waitType)
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        builder.Configuration["Aspire:LaunchGroup"] = "frontend";
        var backend = builder.AddExecutable("backend", "unused", ".").WithLaunchGroups("backend");
        var frontend = builder.AddExecutable("frontend", "unused", ".").WithLaunchGroups("frontend");
        switch (waitType)
        {
            case WaitType.WaitUntilHealthy:
                frontend.WaitFor(backend, WaitBehavior.StopOnResourceUnavailable);
                break;
            case WaitType.WaitUntilStarted:
                frontend.WaitForStart(backend);
                break;
            case WaitType.WaitForCompletion:
                frontend.WaitForCompletion(backend, exitCode: 42);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(waitType));
        }
        await using var app = builder.Build();

        var exception = await Assert.ThrowsAsync<DistributedApplicationException>(
            () => app.ExecuteBeforeStartHooksAsync(TestContext.Current.CancellationToken));

        Assert.Contains("frontend", exception.Message);
        Assert.Contains("backend", exception.Message);
    }

    [Fact]
    public async Task ConnectionStringReferencesAreValidatedTransitively()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        builder.Configuration["Aspire:LaunchGroup"] = "frontend";
        var database = builder.AddContainer("database", "image").WithLaunchGroups("backend")
            .WithEndpoint(name: "tcp", scheme: "tcp", targetPort: 1234);
        var connection = builder.AddConnectionString("connection", ReferenceExpression.Create($"{database.GetEndpoint("tcp")}"));
        var frontend = builder.AddExecutable("frontend", "unused", ".")
            .WithLaunchGroups("frontend").WithReference(connection, optional: true);
        await using var app = builder.Build();

        var exception = await Assert.ThrowsAsync<DistributedApplicationException>(
            () => app.ExecuteBeforeStartHooksAsync(TestContext.Current.CancellationToken));

        Assert.Contains("database", exception.Message);
        Assert.Contains("frontend", exception.Message);
        Assert.False(frontend.Resource.HasAnnotationOfType<ExplicitStartupAnnotation>());
        Assert.False(database.Resource.HasAnnotationOfType<ExplicitStartupAnnotation>());
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
        var catalog = builder.AddContainer("catalog", "image").WithLaunchGroups("catalog");
        var basket = builder.AddExecutable("basket", "unused", ".").WithLaunchGroups("basket");
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
        builder.AddExecutable("catalog", "unused", ".").WithLaunchGroups("catalog");
        var automatic = builder.AddExecutable("automatic", "unused", ".")
            .WithAnnotation(new ExcludeFromLaunchGroupSelectionAnnotation());
        var manual = builder.AddExecutable("manual", "unused", ".").WithExplicitStart()
            .WithAnnotation(new ExcludeFromLaunchGroupSelectionAnnotation());
        var differentGroup = builder.AddExecutable("different-group", "unused", ".").WithLaunchGroups("basket")
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
            .WithLaunchGroups("first")
            .WithLaunchGroups("FIRST", "second");
        await using var app = builder.Build();

        await app.ExecuteBeforeStartHooksAsync(TestContext.Current.CancellationToken);

        Assert.Equal(["first", "second"], resource.Resource.Annotations.OfType<LaunchGroupAnnotation>().Select(annotation => annotation.LaunchGroupName));
        Assert.False(resource.Resource.HasAnnotationOfType<ExplicitStartupAnnotation>());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GroupNamesUseOrdinalIgnoreCaseUnderTurkishCulture(bool declared)
    {
        var originalCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("tr-TR");
            using var builder = TestDistributedApplicationBuilder.Create();
            builder.Configuration["Aspire:LaunchGroup"] = "FILE";
            if (declared)
            {
                builder.Configuration["Aspire:LaunchGroups:0"] = "file";
                builder.Configuration["Aspire:LaunchGroups:1"] = "fıle";
            }
            var selected = builder.AddExecutable("selected", "unused", ".").WithLaunchGroups("file", "FILE");
            var other = builder.AddExecutable("other", "unused", ".").WithLaunchGroups("fıle");
            await using var app = builder.Build();

            await app.ExecuteBeforeStartHooksAsync(TestContext.Current.CancellationToken);

            Assert.False(selected.Resource.HasAnnotationOfType<ExplicitStartupAnnotation>());
            Assert.True(other.Resource.HasAnnotationOfType<ExplicitStartupAnnotation>());
            Assert.Equal(["file"], selected.Resource.Annotations.OfType<LaunchGroupAnnotation>().Select(annotation => annotation.LaunchGroupName));
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
        }
    }

    [Fact]
    public async Task SelectionRejectsMissingWaitReferenceOrConnectionStringDependencies()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        builder.Configuration["Aspire:LaunchGroup"] = "frontend";
        var database = builder.AddContainer("database", "image").WithLaunchGroups("backend");
        var child = builder.AddConnectionString("connection", ReferenceExpression.Create($"{database.GetEndpoint("tcp")}"));
        database.WithEndpoint(name: "tcp", scheme: "tcp", targetPort: 1234);
        var migration = builder.AddExecutable("migration", "unused", ".")
            .WithLaunchGroups("backend").WaitFor(database);
        var api = builder.AddExecutable("api", "unused", ".")
            .WithLaunchGroups("backend").WaitForCompletion(migration).WithReference(child);
        var frontend = builder.AddExecutable("frontend", "unused", ".")
            .WithLaunchGroups("frontend").WithReferenceRelationship(api);
        var unrelated = builder.AddContainer("unrelated", "image").WithLaunchGroups("backend");
        await using var app = builder.Build();

        var exception = await Assert.ThrowsAsync<DistributedApplicationException>(
            () => app.ExecuteBeforeStartHooksAsync(TestContext.Current.CancellationToken));

        foreach (var resource in new IResource[] { database.Resource, child.Resource, migration.Resource, api.Resource, unrelated.Resource })
        {
            Assert.False(resource.HasAnnotationOfType<ExplicitStartupAnnotation>());
        }
        Assert.False(frontend.Resource.HasAnnotationOfType<ExplicitStartupAnnotation>());
        Assert.Contains("frontend", exception.Message);
        Assert.Contains("api", exception.Message);
    }

    [Theory]
    [InlineData("catalog")]
    [InlineData("basket")]
    public async Task DisplayAndReferenceRelationshipsDoNotInheritMembershipOrSelectParents(string parentGroup)
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        builder.Configuration["Aspire:LaunchGroup"] = "catalog";
        var parent = builder.AddContainer("cache", "image").WithLaunchGroups(parentGroup);
        var child = builder.AddExecutable("admin", "unused", ".").WithParentRelationship(parent);
        var overrideChild = builder.AddExecutable("other-admin", "unused", ".")
            .WithParentRelationship(parent).WithLaunchGroups("catalog");
        var reference = builder.AddExecutable("reference", "unused", ".").WithReferenceRelationship(parent);
        await using var app = builder.Build();

        await app.ExecuteBeforeStartHooksAsync(TestContext.Current.CancellationToken);

        Assert.True(child.Resource.HasAnnotationOfType<ExplicitStartupAnnotation>());
        Assert.True(reference.Resource.HasAnnotationOfType<ExplicitStartupAnnotation>());
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
        var dependency = builder.AddContainer("cache", "image").WithLaunchGroups("basket");
        var explicitResource = builder.AddExecutable("explicit", "unused", ".")
            .WithLaunchGroups("catalog").WithExplicitStart().WaitFor(dependency);
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
        var resource = builder.AddExecutable("catalog", "unused", ".").WithLaunchGroups("catalog");
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
        var resource = builder.AddExecutable("catalog", "unused", ".").WithLaunchGroups("catalog");
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
        builder.AddExecutable("basket", "unused", ".").WithLaunchGroups("basket");
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
        var singleGroupException = Assert.ThrowsAny<ArgumentException>(() => resource.WithLaunchGroups([name!]));
        var multipleGroupsException = Assert.ThrowsAny<ArgumentException>(() => resource.WithLaunchGroups("valid", name!));

        Assert.Equal("launchGroupName", annotationException.ParamName);
        Assert.Equal("launchGroupNames", singleGroupException.ParamName);
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
        var resource = builder.AddContainer("catalog", "image").WithLaunchGroups("catalog");
        await using var app = builder.Build();

        app.Services.GetRequiredService<LaunchGroupService>().Configure(app.Services.GetRequiredService<DistributedApplicationModel>());

        Assert.False(resource.Resource.HasAnnotationOfType<ExplicitStartupAnnotation>());
    }

    [Fact]
    public async Task ReferenceCycleRejectsMissingMemberWithoutChangingSelection()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        builder.Configuration["Aspire:LaunchGroup"] = "selected";
        var first = builder.AddExecutable("first", "unused", ".").WithLaunchGroups("selected");
        var second = builder.AddExecutable("second", "unused", ".");
        first.WithReferenceRelationship(second);
        second.WithReferenceRelationship(first);

        await using var app = builder.Build();
        var exception = await Assert.ThrowsAsync<DistributedApplicationException>(
            () => app.ExecuteBeforeStartHooksAsync(TestContext.Current.CancellationToken));

        Assert.Contains("first", exception.Message);
        Assert.Contains("second", exception.Message);
        Assert.False(first.Resource.HasAnnotationOfType<ExplicitStartupAnnotation>());
        Assert.False(second.Resource.HasAnnotationOfType<ExplicitStartupAnnotation>());
    }

    [Fact]
    public async Task ReferenceCycleWithSelectedMembersSucceeds()
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        builder.Configuration["Aspire:LaunchGroup"] = "selected";
        var first = builder.AddExecutable("first", "unused", ".").WithLaunchGroups("selected");
        var second = builder.AddExecutable("second", "unused", ".").WithLaunchGroups("selected");
        first.WithReferenceRelationship(second);
        second.WithReferenceRelationship(first);
        await using var app = builder.Build();

        await app.ExecuteBeforeStartHooksAsync(TestContext.Current.CancellationToken);

        Assert.False(first.Resource.HasAnnotationOfType<ExplicitStartupAnnotation>());
        Assert.False(second.Resource.HasAnnotationOfType<ExplicitStartupAnnotation>());
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
        var dependency = builder.AddExecutable("dependency", "unused", ".").WithLaunchGroups("basket");
        var resource = builder.AddExecutable("worker", "unused", ".").WithLaunchGroups("basket").WaitFor(dependency);
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
