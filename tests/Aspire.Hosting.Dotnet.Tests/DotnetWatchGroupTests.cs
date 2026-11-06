// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

extern alias HostingDotnet;

#pragma warning disable ASPIREEXTENSION001, ASPIREWATCH001

using System.IO.Pipes;
using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Dotnet.Watch;
using Aspire.Hosting.Tests.Utils;
using Aspire.Hosting.Utils;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using DotnetWatchStatus = HostingDotnet::Aspire.Shared.DotnetWatchStatus;
using DotnetWatchJsonContext = HostingDotnet::Aspire.Shared.DotnetWatchJsonContext;
using DotnetWatchConfiguration = HostingDotnet::Aspire.Shared.DotnetWatchConfiguration;
using DotnetWatchProtocol = HostingDotnet::Aspire.Shared.DotnetWatchProtocol;
using TimestampParser = HostingDotnet::Aspire.Shared.ConsoleLogs.TimestampParser;

namespace Aspire.Hosting.Dotnet.Tests;

public class DotnetWatchGroupTests(ITestOutputHelper output)
{
    public static TheoryData<string, bool> InvalidProtocolCases
    {
        get
        {
            var cases = new TheoryData<string, bool>();
            foreach (var frame in new[] { "invalid-json", "null-status", "null-projects", "empty-project",
                "unknown-event", "missing-build-result", "missing-exit-code", "oversized-record" })
            {
                cases.Add(frame, false);
                cases.Add(frame, true);
            }
            return cases;
        }
    }

    [Fact]
    public async Task ServerIsHiddenAndUsesTheSelectedSdkAndExactCliProxy()
    {
        using var workspace = TemporaryWorkspace.Create(output);
        using var builder = TestDistributedApplicationBuilder.Create(output);
        var (registration, root) = DotnetWatchGroupTestServices.AddResource(builder, workspace.Path);
        using var app = builder.Build();
        await using var group = DotnetWatchGroupTestServices.CreateGroup(builder, app.Services, registration, root);

        group.Configure(app.Services.GetRequiredService<DistributedApplicationModel>());

        var server = Assert.Single(builder.Resources.OfType<ExecutableResource>(), resource => resource.Name == "__dotnet-watch-1");
        Assert.Single(server.Annotations.OfType<HiddenAnnotation>());
        Assert.Same(ManifestPublishingCallbackAnnotation.Ignore, Assert.Single(server.Annotations.OfType<ManifestPublishingCallbackAnnotation>()));
        var arguments = await ArgumentEvaluator.GetArgumentListAsync(server, app.Services);
        Assert.Equal("/exact/aspire", server.Command);
        Assert.Equal(
            ["/exact/aspire.dll", DotnetWatchConfiguration.ServerCommand, root.Project.DotnetPath, "exec"],
            arguments.Take(4));
        Assert.Equal(root.Project.SdkDirectory, arguments[7]);
        Assert.Equal(registration.Metadata.ProjectPath, arguments[^1]);
        Assert.Equal(
            ["/exact/aspire.dll", "--internal-dotnet-watch-resource", "--application-option", "two words"],
            await ArgumentEvaluator.GetArgumentListAsync(registration.Resource, app.Services));
        Assert.Equal("/exact/aspire", registration.Resource.Annotations.OfType<ExecutableAnnotation>().Last().Command);
        var environment = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(registration.Resource, serviceProvider: app.Services);
        var inventory = Assert.IsType<string[]>(JsonSerializer.Deserialize(
            environment[DotnetWatchConfiguration.EnvironmentNames], DotnetWatchJsonContext.Default.StringArray));
        Assert.Equal(["APP_SETTING"], inventory);
        Assert.Equal("true", environment[DotnetWatchConfiguration.ExcludeProfile]);
        var serverEnvironment = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(server, serviceProvider: app.Services);
        Assert.Equal("", serverEnvironment[DotnetWatchConfiguration.HostControlPipe]);
        Assert.Equal("", serverEnvironment[DotnetWatchConfiguration.HostSession]);
    }

    [Fact]
    public async Task PrestartInfrastructureFailureRestoresTheOrdinaryLaunchAndCommands()
    {
        using var workspace = TemporaryWorkspace.Create(output);
        using var builder = TestDistributedApplicationBuilder.Create(output);
        var (registration, root) = DotnetWatchGroupTestServices.AddResource(builder, workspace.Path);
        using var app = builder.Build();
        await using var group = DotnetWatchGroupTestServices.CreateGroup(builder, app.Services, registration, root);
        group.Configure(app.Services.GetRequiredService<DistributedApplicationModel>());
        var originalCalled = false;
        var original = CreateCommand("rebuild", () => originalCalled = true);
        var command = registration.Resource.Annotations.OfType<ResourceLifecycleCommandOverrideAnnotation>()
            .Single(annotation => annotation.CommandName == "rebuild").Factory(original);
        var context = new UpdateCommandStateContext { Services = app.Services, ResourceSnapshot = CreateSnapshot("Running") };
        Assert.Equal(ResourceCommandState.Disabled, command.UpdateState(context));

        await group.FailAsync("test infrastructure failure", TestContext.Current.CancellationToken);

        Assert.False(await group.WaitUntilReadyAsync(TestContext.Current.CancellationToken));
        Assert.False(group.Enabled);
        Assert.Equal("dotnet", registration.Resource.Annotations.OfType<ExecutableAnnotation>().Last().Command);
        Assert.Equal(["ordinary-tool", "--project", registration.Metadata.ProjectPath, "--application-option", "two words"],
            await ArgumentEvaluator.GetArgumentListAsync(registration.Resource, app.Services));
        Assert.Equal(ResourceCommandState.Enabled, command.UpdateState(context));
        var result = await command.ExecuteCommand(CreateCommandContext(app.Services));
        Assert.True(result.Success);
        Assert.True(originalCalled);
        Assert.True(app.Services.GetRequiredService<ResourceNotificationService>().TryGetCurrentState(registration.Resource.Name, out var current));
        Assert.Equal(["watch.mode", "watch.reason"], current.Snapshot.Properties
            .Select(property => property.Name).Where(name => name.StartsWith("watch.", StringComparison.Ordinal)).Order());
    }

    [Fact]
    public async Task AbortedModelPreparationRestoresExecutionWithoutCancelingTheOrdinaryResource()
    {
        using var workspace = TemporaryWorkspace.Create(output);
        using var builder = TestDistributedApplicationBuilder.Create(output);
        var (registration, root) = DotnetWatchGroupTestServices.AddResource(builder, workspace.Path);
        using var app = builder.Build();
        await using var group = DotnetWatchGroupTestServices.CreateGroup(builder, app.Services, registration, root);
        group.Configure(app.Services.GetRequiredService<DistributedApplicationModel>());

        await group.AbortSetupAsync("A later watch component could not be configured.");

        Assert.False(await group.WaitUntilReadyAsync(TestContext.Current.CancellationToken));
        Assert.Equal(["service"], builder.Resources.Select(resource => resource.Name));
        Assert.Equal("dotnet", registration.Resource.Command);
    }

    [Fact]
    public async Task UnconfirmedPrestartCleanupDoesNotLaunchAnOrdinaryDuplicate()
    {
        using var workspace = TemporaryWorkspace.Create(output);
        using var builder = TestDistributedApplicationBuilder.Create(output);
        var (registration, root) = DotnetWatchGroupTestServices.AddResource(builder, workspace.Path);
        using var app = builder.Build();
        await using var group = DotnetWatchGroupTestServices.CreateGroup(builder, app.Services, registration, root);
        group.Configure(app.Services.GetRequiredService<DistributedApplicationModel>());
        var server = builder.Resources.Single(resource => resource.Name == "__dotnet-watch-1");
        server.Annotations.OfType<ResourceLifecycleCommandOverrideAnnotation>().Single(annotation => annotation.CommandName == "stop")
            .Factory(new("stop", "stop", _ => ResourceCommandState.Enabled,
                _ => Task.FromResult(new ExecuteCommandResult { Success = false, Message = "Stop was rejected." }),
                null, arguments: null, null, null, null, false));
        await app.Services.GetRequiredService<ResourceNotificationService>().PublishUpdateAsync(
            server, _ => CreateSnapshot("Running"));

        await group.FailAsync("Initialization failed.", TestContext.Current.CancellationToken);

        Assert.True(group.Failed);
        await Assert.ThrowsAsync<DistributedApplicationException>(() => group.WaitUntilReadyAsync(TestContext.Current.CancellationToken));
        Assert.EndsWith("Cleanup could not be confirmed; ordinary fallback was refused.", group.Failure);
        Assert.Equal("/exact/aspire", registration.Resource.Annotations.OfType<ExecutableAnnotation>().Last().Command);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PrestartFallbackRequiresTheOwnersVerifiedScopeCompletion(bool completed)
    {
        using var workspace = TemporaryWorkspace.Create(output);
        using var builder = TestDistributedApplicationBuilder.Create(output);
        var (registration, root) = DotnetWatchGroupTestServices.AddResource(builder, workspace.Path);
        using var app = builder.Build();
        await using var group = DotnetWatchGroupTestServices.CreateGroup(builder, app.Services, registration, root);
        group.Configure(app.Services.GetRequiredService<DistributedApplicationModel>());
        var server = builder.Resources.Single(resource => resource.Name == "__dotnet-watch-1");
        server.Annotations.OfType<ResourceLifecycleCommandOverrideAnnotation>().Single(annotation => annotation.CommandName == "stop")
            .Factory(new("stop", "stop", _ => ResourceCommandState.Enabled,
                _ => Task.FromResult(new ExecuteCommandResult { Success = true }),
                null, arguments: null, null, null, null, false));
        await builder.Eventing.PublishAsync(
            new BeforeResourceStartedEvent(server, app.Services), TestContext.Current.CancellationToken);
        await app.Services.GetRequiredService<ResourceNotificationService>().PublishUpdateAsync(
            server, _ => CreateSnapshot("Running"));
        if (completed)
        {
            var environment = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(server, serviceProvider: app.Services);
            await File.WriteAllTextAsync(environment[DotnetWatchConfiguration.ScopeCompletionPath], "");
        }

        await group.FailAsync("Initialization failed.", TestContext.Current.CancellationToken);

        Assert.Equal(!completed, group.Enabled);
        Assert.Equal(completed ? "dotnet" : "/exact/aspire", registration.Resource.Command);
        if (completed)
        {
            Assert.False(await group.WaitUntilReadyAsync(TestContext.Current.CancellationToken));
        }
        else
        {
            await Assert.ThrowsAsync<DistributedApplicationException>(() => group.WaitUntilReadyAsync(TestContext.Current.CancellationToken));
        }
    }

    [Fact]
    public async Task PendingFallbackKeepsOrdinaryLaunchAndRebuildFencedUntilCleanupFinishes()
    {
        using var workspace = TemporaryWorkspace.Create(output);
        using var builder = TestDistributedApplicationBuilder.Create(output);
        var (registration, root) = DotnetWatchGroupTestServices.AddResource(builder, workspace.Path);
        using var app = builder.Build();
        await using var group = DotnetWatchGroupTestServices.CreateGroup(builder, app.Services, registration, root);
        group.Configure(app.Services.GetRequiredService<DistributedApplicationModel>());
        var server = builder.Resources.Single(resource => resource.Name == "__dotnet-watch-1");
        var stopping = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        server.Annotations.OfType<ResourceLifecycleCommandOverrideAnnotation>().Single(annotation => annotation.CommandName == "stop")
            .Factory(new("stop", "stop", _ => ResourceCommandState.Enabled, async context =>
            {
                stopping.TrySetResult();
                await stopped.Task.WaitAsync(context.CancellationToken);
                return new() { Success = true };
            }, null, arguments: null, null, null, null, false));
        var rebuild = registration.Resource.Annotations.OfType<ResourceLifecycleCommandOverrideAnnotation>()
            .Single(annotation => annotation.CommandName == "rebuild").Factory(CreateCommand("rebuild", () => { }));
        await app.Services.GetRequiredService<ResourceNotificationService>().PublishUpdateAsync(
            server, _ => CreateSnapshot("Running"));
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        var failure = group.FailAsync("Initialization failed.", timeout.Token);
        try
        {
            await stopping.Task.WaitAsync(timeout.Token);
            var ready = group.WaitUntilReadyAsync(timeout.Token);
            Assert.False(ready.IsCompleted);
            Assert.Equal("/exact/aspire", registration.Resource.Annotations.OfType<ExecutableAnnotation>().Last().Command);
            Assert.False((await rebuild.ExecuteCommand(CreateCommandContext(app.Services))).Success);

            stopped.TrySetResult();
            await failure.WaitAsync(timeout.Token);

            Assert.False(await ready);
            Assert.Equal("dotnet", registration.Resource.Annotations.OfType<ExecutableAnnotation>().Last().Command);
            Assert.True((await rebuild.ExecuteCommand(CreateCommandContext(app.Services))).Success);
        }
        finally
        {
            stopped.TrySetResult();
            await failure.WaitAsync(timeout.Token);
        }
    }

    [Fact]
    public async Task ProjectionKeepsObservedApplicationExitSeparateFromTheLiveProxy()
    {
        using var workspace = TemporaryWorkspace.Create(output);
        using var builder = TestDistributedApplicationBuilder.Create(output);
        var (registration, root) = DotnetWatchGroupTestServices.AddResource(builder, workspace.Path);
        using var app = builder.Build();
        await using var group = DotnetWatchGroupTestServices.CreateGroup(builder, app.Services, registration, root);
        var member = new DotnetWatchMember(group, registration, builder, app.Services, "/exact/aspire", [], "unused");
        member.Configure();
        await member.HandleStatusAsync(new DotnetWatchStatus { Type = "process_started", Projects = [root.Metadata.ProjectPath] });
        await member.HandleStatusAsync(new DotnetWatchStatus { Type = "process_exited", Projects = [root.Metadata.ProjectPath], ExitCode = 17 });
        var raw = CreateSnapshot("Running") with
        {
            Properties = [new("executable.pid", 1234)],
            ExitCode = null,
            StartTimeStamp = DateTime.UtcNow
        };

        var projected = member.ProjectSnapshot(raw);

        Assert.Equal(KnownResourceStates.Exited, projected.State?.Text);
        Assert.Equal(17, projected.ExitCode);
        Assert.Collection(projected.Properties,
            property => Assert.Equal(("watch.transport.state", (object?)"Running"), (property.Name, property.Value)),
            property => Assert.Equal(("watch.transport.pid", (object?)1234), (property.Name, property.Value)),
            property => Assert.Equal(("watch.transport.exitCode", (object?)null), (property.Name, property.Value)),
            property => Assert.Equal(("watch.build", (object?)"initializing"), (property.Name, property.Value)),
            property => Assert.Equal(("watch.generation", (object?)1L), (property.Name, property.Value)),
            property => Assert.Equal(("watch.mode", (object?)"experimental"), (property.Name, property.Value)),
            property => Assert.StartsWith("Experimental, last-observed", Assert.IsType<string>(property.Value)));
        Assert.Null(projected.StartTimeStamp);
        Assert.Null(projected.StopTimeStamp);
    }

    [Fact]
    public async Task LogicalPublicationsDoNotReinterpretApplicationExitAsProxyExit()
    {
        using var workspace = TemporaryWorkspace.Create(output);
        using var builder = TestDistributedApplicationBuilder.Create(output);
        var (registration, root) = DotnetWatchGroupTestServices.AddResource(builder, workspace.Path);
        using var app = builder.Build();
        await using var group = DotnetWatchGroupTestServices.CreateGroup(builder, app.Services, registration, root);
        var member = new DotnetWatchMember(group, registration, builder, app.Services, "/exact/aspire", [], "unused");
        member.Configure();
        var notifications = app.Services.GetRequiredService<ResourceNotificationService>();
        await notifications.PublishUpdateAsync(registration.Resource, _ => CreateSnapshot("Running") with
        {
            Properties = [new("executable.pid", 1234)]
        });
        await member.HandleStatusAsync(new DotnetWatchStatus { Type = "process_started", Projects = [root.Metadata.ProjectPath] });
        await member.HandleStatusAsync(new DotnetWatchStatus { Type = "process_exited", Projects = [root.Metadata.ProjectPath], ExitCode = 4 });

        await member.PublishAsync();
        await notifications.PublishUpdateAsync(registration.Resource,
            snapshot => snapshot with { Properties = [.. snapshot.Properties, new("observer.health", true)] });

        Assert.True(notifications.TryGetCurrentState(registration.Resource.Name, out var current));
        Assert.Equal(4, current.Snapshot.ExitCode);
        Assert.Equal("Running", current.Snapshot.Properties.Single(property => property.Name == "watch.transport.state").Value);
        Assert.Equal(1234, current.Snapshot.Properties.Single(property => property.Name == "watch.transport.pid").Value);
        Assert.Equal(["observer.health", "watch.build", "watch.generation", "watch.mode", "watch.reason",
            "watch.transport.exitCode", "watch.transport.pid", "watch.transport.state"],
            current.Snapshot.Properties.Select(property => property.Name).Order());
    }

    [Theory]
    [InlineData(false, "FailedToStart")]
    [InlineData(true, "Exited")]
    public async Task SuccessfulProxyExitIsNotSuccessfulApplicationCompletion(bool stopRequested, string state)
    {
        using var workspace = TemporaryWorkspace.Create(output);
        using var builder = TestDistributedApplicationBuilder.Create(output);
        var (registration, root) = DotnetWatchGroupTestServices.AddResource(builder, workspace.Path);
        using var app = builder.Build();
        await using var group = DotnetWatchGroupTestServices.CreateGroup(builder, app.Services, registration, root);
        var member = new DotnetWatchMember(group, registration, builder, app.Services, "/exact/aspire", [], "unused");
        member.Configure();
        await member.HandleStatusAsync(new DotnetWatchStatus { Type = "process_started", Projects = [root.Metadata.ProjectPath] });
        if (stopRequested)
        {
            var stop = registration.Resource.Annotations.OfType<ResourceLifecycleCommandOverrideAnnotation>()
                .Single(annotation => annotation.CommandName == "stop").Factory(CreateCommand("stop", () => { }));
            Assert.True((await stop.ExecuteCommand(CreateCommandContext(app.Services))).Success);
        }

        var projected = member.ProjectSnapshot(CreateSnapshot(KnownResourceStates.Finished) with
        {
            ExitCode = 0,
            Properties = [new("executable.pid", 1234)]
        });

        Assert.Equal(state, projected.State?.Text);
        Assert.Null(projected.ExitCode);
        Assert.Equal(0, projected.Properties.Single(property => property.Name == "watch.transport.exitCode").Value);
    }

    [Fact]
    public async Task InvalidEditDoesNotClaimProcessExitAndSuccessfulMetadataUpdateClearsTheBuildFailure()
    {
        using var workspace = TemporaryWorkspace.Create(output);
        using var builder = TestDistributedApplicationBuilder.Create(output);
        var (registration, root) = DotnetWatchGroupTestServices.AddResource(builder, workspace.Path);
        using var app = builder.Build();
        await using var group = DotnetWatchGroupTestServices.CreateGroup(builder, app.Services, registration, root);
        var member = new DotnetWatchMember(group, registration, builder, app.Services, "/exact/aspire", [], "unused");
        member.Configure();
        await member.HandleStatusAsync(new DotnetWatchStatus { Type = "process_started", Projects = [root.Metadata.ProjectPath] });
        await member.HandleStatusAsync(new DotnetWatchStatus { Type = "build_complete", Projects = [root.Metadata.ProjectPath], Success = false });
        var failed = member.ProjectSnapshot(CreateSnapshot("Running"));
        Assert.Equal("Running", failed.State?.Text);
        Assert.Null(failed.ExitCode);
        Assert.Equal("failed", failed.Properties.Single(property => property.Name == "watch.build").Value);

        await member.HandleStatusAsync(new DotnetWatchStatus { Type = "hot_reload_applied", Projects = [root.Metadata.ProjectPath] });

        var recovered = member.ProjectSnapshot(CreateSnapshot("Running"));
        Assert.Equal("Running", recovered.State?.Text);
        Assert.Null(recovered.ExitCode);
        Assert.Equal("idle", recovered.Properties.Single(property => property.Name == "watch.build").Value);
    }

    [Fact]
    public async Task StopMakesSubsequentStartUnavailableButDoesNotCallOrdinaryRestart()
    {
        using var workspace = TemporaryWorkspace.Create(output);
        using var builder = TestDistributedApplicationBuilder.Create(output);
        var (registration, root) = DotnetWatchGroupTestServices.AddResource(builder, workspace.Path);
        using var app = builder.Build();
        await using var group = DotnetWatchGroupTestServices.CreateGroup(builder, app.Services, registration, root);
        var member = new DotnetWatchMember(group, registration, builder, app.Services, "/exact/aspire", [], "unused");
        member.Configure();
        await member.HandleStatusAsync(new DotnetWatchStatus { Type = "process_started", Projects = [root.Metadata.ProjectPath] });
        var originalCalled = false;
        ResourceCommandAnnotation GetCommand(string name) =>
            registration.Resource.Annotations.OfType<ResourceLifecycleCommandOverrideAnnotation>()
                .Single(annotation => annotation.CommandName == name).Factory(CreateCommand(name, () => originalCalled = true));

        Assert.True((await GetCommand("stop").ExecuteCommand(CreateCommandContext(app.Services))).Success);
        Assert.True(originalCalled);
        originalCalled = false;
        var start = await GetCommand("start").ExecuteCommand(CreateCommandContext(app.Services));
        var restart = await GetCommand("restart").ExecuteCommand(CreateCommandContext(app.Services));

        Assert.False(start.Success);
        Assert.False(restart.Success);
        Assert.False(originalCalled);
        Assert.Equal(ResourceCommandState.Disabled, GetCommand("start").UpdateState(new()
        {
            Services = app.Services, ResourceSnapshot = CreateSnapshot("Finished")
        }));
    }

    [Theory]
    [MemberData(nameof(InvalidProtocolCases))]
    public async Task InvalidProtocolCleansUpOnlyItsGroupAndCompletesReadiness(string invalidFrame, bool active)
    {
        using var workspace = TemporaryWorkspace.Create(output);
        using var builder = TestDistributedApplicationBuilder.Create(output);
        var (registration, root) = DotnetWatchGroupTestServices.AddResource(builder, workspace.Path);
        var (unrelated, unrelatedRoot) = DotnetWatchGroupTestServices.AddResource(
            builder, workspace.CreateDirectory("unrelated").FullName, "unrelated");
        using var app = builder.Build();
        await using var group = DotnetWatchGroupTestServices.CreateGroup(builder, app.Services, registration, root);
        await using var unrelatedGroup = DotnetWatchGroupTestServices.CreateGroup(builder, app.Services, unrelated, unrelatedRoot, 2);
        var model = app.Services.GetRequiredService<DistributedApplicationModel>();
        group.Configure(model);
        unrelatedGroup.Configure(model);
        var server = builder.Resources.Single(resource => resource.Name == "__dotnet-watch-1");
        var unrelatedServer = builder.Resources.Single(resource => resource.Name == "__dotnet-watch-2");
        var stopped = new ConcurrentQueue<string>();
        var notifications = app.Services.GetRequiredService<ResourceNotificationService>();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        var scopePaths = new Dictionary<string, string>();
        foreach (var (resource, owner) in new[] { (registration.Resource, server), (unrelated.Resource, unrelatedServer) })
        {
            resource.Annotations.OfType<ResourceLifecycleCommandOverrideAnnotation>()
                .Single(annotation => annotation.CommandName == "stop").Factory(CreateCommand("stop", () => stopped.Enqueue(resource.Name)));
            var environment = await EnvironmentVariableEvaluator.GetEnvironmentVariablesAsync(owner, serviceProvider: app.Services);
            var completedPath = environment[DotnetWatchConfiguration.ScopeCompletionPath];
            scopePaths[owner.Name] = completedPath;
            owner.Annotations.OfType<ResourceLifecycleCommandOverrideAnnotation>().Single(annotation => annotation.CommandName == "stop")
                .Factory(new("stop", "stop", _ => ResourceCommandState.Enabled, async context =>
                {
                    stopped.Enqueue(owner.Name);
                    await File.WriteAllTextAsync(completedPath, "", context.CancellationToken);
                    return new ExecuteCommandResult { Success = true };
                }, null, arguments: null, null, null, null, false));
            await builder.Eventing.PublishAsync(new BeforeResourceStartedEvent(owner, app.Services), timeout.Token);
            await notifications.PublishUpdateAsync(owner, _ => CreateSnapshot("Running"));
            await notifications.PublishUpdateAsync(resource, _ => CreateSnapshot("Running"));
        }
        var arguments = await ArgumentEvaluator.GetArgumentListAsync(server, app.Services);
        var statusName = arguments[arguments.IndexOf("--status-pipe") + 1];
        var controlName = arguments[arguments.IndexOf("--control-pipe") + 1];
        using var status = CreateClient(statusName);
        using var control = CreateClient(controlName);
        var unrelatedArguments = await ArgumentEvaluator.GetArgumentListAsync(unrelatedServer, app.Services);
        using var unrelatedStatus = CreateClient(unrelatedArguments[unrelatedArguments.IndexOf("--status-pipe") + 1]);
        using var unrelatedControl = CreateClient(unrelatedArguments[unrelatedArguments.IndexOf("--control-pipe") + 1]);
        await Task.WhenAll(status.ConnectAsync(timeout.Token), unrelatedStatus.ConnectAsync(timeout.Token), unrelatedControl.ConnectAsync(timeout.Token));
        Assert.True(await unrelatedGroup.WaitUntilReadyAsync(timeout.Token));
        await builder.Eventing.PublishAsync(new BeforeResourceStartedEvent(unrelated.Resource, app.Services), timeout.Token);
        var ready = group.WaitUntilReadyAsync(timeout.Token);
        if (active)
        {
            await control.ConnectAsync(timeout.Token);
            Assert.True(await ready);
            await builder.Eventing.PublishAsync(new BeforeResourceStartedEvent(registration.Resource, app.Services), timeout.Token);
        }
        else
        {
            Assert.False(ready.IsCompleted);
        }
        var cleanup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var stoppingRegistration = group.StoppingToken.Register(() => cleanup.TrySetResult());

        var frame = invalidFrame switch
        {
            "invalid-json" => "{invalid json}",
            "null-status" => "null",
            "null-projects" => """{"type":"building","projects":null}""",
            "empty-project" => """{"type":"building","projects":[""]}""",
            "unknown-event" => """{"type":"unknown","projects":[]}""",
            "missing-build-result" => """{"type":"build_complete","projects":[]}""",
            "missing-exit-code" => """{"type":"process_exited","projects":[]}""",
            "oversized-record" => new string('a', DotnetWatchProtocol.MaximumMessageBytes + 1),
            _ => throw new ArgumentOutOfRangeException(nameof(invalidFrame))
        };
        await status.WriteAsync(Encoding.UTF8.GetBytes(frame + "\n"), timeout.Token);
        await cleanup.Task.WaitAsync(timeout.Token);

        Assert.Equal(active, group.Enabled);
        Assert.Equal(active, group.Failed);
        if (active)
        {
            await Assert.ThrowsAsync<DistributedApplicationException>(() => group.WaitUntilReadyAsync(timeout.Token));
            var failure = await notifications.WaitForResourceAsync(
                registration.Resource.Name, @event => @event.Snapshot.State?.Text == KnownResourceStates.FailedToStart, timeout.Token);
            Assert.Equal("experimental", failure.Snapshot.Properties.Single(property => property.Name == "watch.mode").Value);
            Assert.Equal("/exact/aspire", registration.Resource.Command);
        }
        else
        {
            Assert.False(await ready);
            Assert.Equal("dotnet", registration.Resource.Command);
        }
        Assert.Equal(active ? ["service", "__dotnet-watch-1"] : ["__dotnet-watch-1"], stopped);
        Assert.True(unrelatedGroup.Enabled);
        Assert.False(unrelatedGroup.Failed);
        Assert.False(unrelatedGroup.StoppingToken.IsCancellationRequested);
        Assert.True(await unrelatedGroup.WaitUntilReadyAsync(timeout.Token));
        await unrelatedGroup.RestartAsync(unrelatedRoot.Metadata.ProjectPath, timeout.Token);
        using var controlReader = new StreamReader(unrelatedControl, leaveOpen: true);
        using var request = JsonDocument.Parse((await controlReader.ReadLineAsync(timeout.Token))!);
        Assert.Equal("rebuild", request.RootElement.GetProperty("type").GetString());
        Assert.Equal(unrelatedRoot.Metadata.ProjectPath, Assert.Single(request.RootElement.GetProperty("projects").EnumerateArray()).GetString());

        await group.DisposeAsync().AsTask().WaitAsync(timeout.Token);
        Assert.False(Directory.Exists(Path.GetDirectoryName(scopePaths[server.Name])));
        using var releasedStatus = new NamedPipeServerStream(statusName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        using var releasedControl = new NamedPipeServerStream(controlName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await unrelatedGroup.DisposeAsync().AsTask().WaitAsync(timeout.Token);
        Assert.False(Directory.Exists(Path.GetDirectoryName(scopePaths[unrelatedServer.Name])));
        Assert.Equal(active
            ? ["service", "__dotnet-watch-1", "unrelated", "__dotnet-watch-2"]
            : ["__dotnet-watch-1", "unrelated", "__dotnet-watch-2"], stopped);
    }

    [Fact]
    public async Task InitialSourceBuildFailureDoesNotFallBackToOrdinaryOutput()
    {
        using var workspace = TemporaryWorkspace.Create(output);
        using var builder = TestDistributedApplicationBuilder.Create(output);
        var (registration, root) = DotnetWatchGroupTestServices.AddResource(builder, workspace.Path);
        using var app = builder.Build();
        await using var group = DotnetWatchGroupTestServices.CreateGroup(builder, app.Services, registration, root);
        group.Configure(app.Services.GetRequiredService<DistributedApplicationModel>());
        var server = builder.Resources.Single(resource => resource.Name == "__dotnet-watch-1");
        var arguments = await ArgumentEvaluator.GetArgumentListAsync(server, app.Services);
        using var status = CreateClient(arguments[arguments.IndexOf("--status-pipe") + 1]);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        await status.ConnectAsync(timeout.Token);
        var message = JsonSerializer.Serialize(new DotnetWatchStatus
        {
            Type = "build_complete", Projects = [root.Metadata.ProjectPath], Success = false
        }, DotnetWatchJsonContext.Default.DotnetWatchStatus);
        await status.WriteAsync(Encoding.UTF8.GetBytes(message + "\n"), timeout.Token);
        await app.Services.GetRequiredService<ResourceNotificationService>().WaitForResourceAsync(
            registration.Resource.Name,
            @event => @event.Snapshot.Properties.Any(property => property.Name == "watch.build" && Equals(property.Value, "failed")),
            timeout.Token);

        await group.FailAsync("The initial watch build failed.", timeout.Token);

        await Assert.ThrowsAsync<DistributedApplicationException>(() => group.WaitUntilReadyAsync(timeout.Token));
        Assert.True(group.Enabled);
        Assert.Equal("/exact/aspire", registration.Resource.Annotations.OfType<ExecutableAnnotation>().Last().Command);
    }

    [Fact]
    public async Task FirstLaunchCommitsOnlyAfterBothReadinessChannelsConnect()
    {
        using var workspace = TemporaryWorkspace.Create(output);
        using var builder = TestDistributedApplicationBuilder.Create(output);
        var (registration, root) = DotnetWatchGroupTestServices.AddResource(builder, workspace.Path);
        using var app = builder.Build();
        await using var group = DotnetWatchGroupTestServices.CreateGroup(builder, app.Services, registration, root);
        group.Configure(app.Services.GetRequiredService<DistributedApplicationModel>());
        registration.Resource.Annotations.OfType<ResourceLifecycleCommandOverrideAnnotation>()
            .Single(annotation => annotation.CommandName == "stop").Factory(CreateCommand("stop", () => { }));
        var server = builder.Resources.Single(resource => resource.Name == "__dotnet-watch-1");
        var arguments = await ArgumentEvaluator.GetArgumentListAsync(server, app.Services);
        using var status = CreateClient(arguments[arguments.IndexOf("--status-pipe") + 1]);
        using var control = CreateClient(arguments[arguments.IndexOf("--control-pipe") + 1]);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        var start = builder.Eventing.PublishAsync(new BeforeResourceStartedEvent(registration.Resource, app.Services), timeout.Token);
        await status.ConnectAsync(timeout.Token);
        Assert.False(start.IsCompleted);

        await control.ConnectAsync(timeout.Token);
        await start;

        Assert.True(app.Services.GetRequiredService<ResourceNotificationService>().TryGetCurrentState(registration.Resource.Name, out var current));
        Assert.Equal(KnownResourceStates.Starting, current.Snapshot.State?.Text);
        Assert.Equal("experimental", current.Snapshot.Properties.Single(property => property.Name == "watch.mode").Value);
    }

    [Fact]
    public async Task FailedCoordinatedBuildIsNotClassifiedAsWatchInfrastructureFailure()
    {
        using var workspace = TemporaryWorkspace.Create(output);
        using var builder = TestDistributedApplicationBuilder.Create(output);
        var (registration, root) = DotnetWatchGroupTestServices.AddResource(builder, workspace.Path);
        using var build = new DotnetProjectBuildResource("initial-build", workspace.Path,
            Path.Combine(workspace.Path, "build"), TimeProvider.System);
        builder.AddResource(build);
        using var app = builder.Build();
        await using var group = DotnetWatchGroupTestServices.CreateGroup(builder, app.Services, registration, root);
        group.Configure(app.Services.GetRequiredService<DistributedApplicationModel>());
        await app.Services.GetRequiredService<ResourceNotificationService>().PublishUpdateAsync(
            build, _ => CreateSnapshot("Exited") with { ExitCode = 1 });

        await group.FailAsync("The initial build did not complete successfully.", TestContext.Current.CancellationToken);

        Assert.True(group.Enabled);
        await Assert.ThrowsAsync<DistributedApplicationException>(() => group.WaitUntilReadyAsync(TestContext.Current.CancellationToken));
        Assert.Equal("/exact/aspire", registration.Resource.Annotations.OfType<ExecutableAnnotation>().Last().Command);
    }

    [Fact]
    public async Task UserCancellationDoesNotSelectOrdinaryFallback()
    {
        using var workspace = TemporaryWorkspace.Create(output);
        using var builder = TestDistributedApplicationBuilder.Create(output);
        var (registration, root) = DotnetWatchGroupTestServices.AddResource(builder, workspace.Path);
        using var app = builder.Build();
        await using var group = DotnetWatchGroupTestServices.CreateGroup(builder, app.Services, registration, root);
        group.Configure(app.Services.GetRequiredService<DistributedApplicationModel>());
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => group.WaitUntilReadyAsync(cancellation.Token));

        Assert.True(group.Enabled);
        Assert.Null(group.Failure);
        Assert.Equal("/exact/aspire", registration.Resource.Annotations.OfType<ExecutableAnnotation>().Last().Command);
    }

    [Fact]
    public async Task ShutdownDoesNotReleaseAWaitingResourceIntoOrdinaryExecution()
    {
        using var workspace = TemporaryWorkspace.Create(output);
        using var builder = TestDistributedApplicationBuilder.Create(output);
        var (registration, root) = DotnetWatchGroupTestServices.AddResource(builder, workspace.Path);
        using var app = builder.Build();
        await using var group = DotnetWatchGroupTestServices.CreateGroup(builder, app.Services, registration, root);
        group.Configure(app.Services.GetRequiredService<DistributedApplicationModel>());
        var ready = group.WaitUntilReadyAsync(TestContext.Current.CancellationToken);

        await group.StopAsync(TestContext.Current.CancellationToken);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ready);
        Assert.True(group.Enabled);
        Assert.Null(group.Failure);
    }

    [Fact]
    public async Task HiddenServerDiagnosticsAreVisibleInTheServiceLog()
    {
        using var workspace = TemporaryWorkspace.Create(output);
        using var builder = TestDistributedApplicationBuilder.Create(output);
        var (registration, root) = DotnetWatchGroupTestServices.AddResource(builder, workspace.Path);
        using var app = builder.Build();
        await using var group = DotnetWatchGroupTestServices.CreateGroup(builder, app.Services, registration, root);
        group.Configure(app.Services.GetRequiredService<DistributedApplicationModel>());
        var server = builder.Resources.Single(resource => resource.Name == "__dotnet-watch-1");
        var logs = app.Services.GetRequiredService<ResourceLoggerService>();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        await using var reader = logs.WatchAsync(registration.Resource).GetAsyncEnumerator(timeout.Token);
        var read = reader.MoveNextAsync().AsTask();
        await builder.Eventing.PublishAsync(new BeforeResourceStartedEvent(server, app.Services), timeout.Token);

        logs.GetLogger(server).LogError("CS1002: expected ';'.");

        Assert.True(await read);
        var line = Assert.Single(reader.Current);
        Assert.True(TimestampParser.TryParseConsoleTimestamp(line.Content, out var outer));
        Assert.Equal("[watch] CS1002: expected ';'.", outer.Value.ModifiedText);
        Assert.True(line.IsErrorMessage);
    }

    [Theory]
    [InlineData("{}\n", "{}")]
    [InlineData("{}\r\n", "{}")]
    [InlineData("\n", "")]
    public async Task StatusLineReaderPreservesTheBoundedRecord(string input, string expected)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(input));
        using var reader = new StreamReader(stream);

        Assert.Equal(expected, await DotnetWatchGroup.ReadBoundedLineAsync(reader, TestContext.Current.CancellationToken));
        Assert.Null(await DotnetWatchGroup.ReadBoundedLineAsync(reader, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task StatusLineReaderRejectsTruncation()
    {
        using var stream = new MemoryStream("{}"u8.ToArray());
        using var reader = new StreamReader(stream);

        await Assert.ThrowsAsync<EndOfStreamException>(() => DotnetWatchGroup.ReadBoundedLineAsync(reader, TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData('a', 1024 * 1024 + 1)]
    [InlineData('\u00e9', 1024 * 1024 / 2 + 1)]
    public async Task StatusLineReaderBoundsBytesNotOnlyCharacters(char character, int length)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(new string(character, length) + "\n"));
        using var reader = new StreamReader(stream);

        await Assert.ThrowsAsync<InvalidDataException>(() => DotnetWatchGroup.ReadBoundedLineAsync(reader, TestContext.Current.CancellationToken));
    }

    private static NamedPipeClientStream CreateClient(string pipe) =>
        new(".", pipe, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

    private static ResourceCommandAnnotation CreateCommand(string name, Action execute) =>
        new(name, name, _ => ResourceCommandState.Enabled, _ =>
        {
            execute();
            return Task.FromResult(new ExecuteCommandResult { Success = true });
        }, null, arguments: null, null, null, null, false);

    private static ExecuteCommandContext CreateCommandContext(IServiceProvider services) =>
        new()
        {
            Services = services,
            ResourceName = "service",
            CancellationToken = TestContext.Current.CancellationToken,
            Logger = NullLogger.Instance,
            Arguments = new InteractionInputCollection([])
        };

    private static CustomResourceSnapshot CreateSnapshot(string state) =>
        new() { ResourceType = "project", Properties = [], State = new(state, null) };
}
