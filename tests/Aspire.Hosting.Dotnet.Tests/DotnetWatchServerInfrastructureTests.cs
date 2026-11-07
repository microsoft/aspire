// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREWATCH001 // Watch infrastructure uses experimental lifecycle overrides.

using System.Runtime.Versioning;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Dotnet.Watch;
using Aspire.Hosting.Utils;
using Microsoft.Extensions.DependencyInjection;

namespace Aspire.Hosting.Dotnet.Tests;

public class DotnetWatchServerInfrastructureTests(ITestOutputHelper outputHelper)
{
    [Fact]
    public async Task ControlPipeDisposalSettlesPendingCommandsAndIsIdempotent()
    {
        var directory = Directory.CreateTempSubdirectory("apx");
        try
        {
            var path = OperatingSystem.IsWindows()
                ? $"aspire-control-{Guid.NewGuid():N}" : Path.Combine(directory.FullName, "control.sock");
            Exception? failure = null;
            var control = new DotnetWatchControlPipeServer(path, exception => failure = exception);
            control.Start();
            var command = control.SendRebuildAsync(["service.csproj"], CancellationToken.None);

            await control.DisposeAsync();
            await control.DisposeAsync();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => command);
            Assert.Null(failure);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task OwnedLifecycleCommandsUseTheCurrentDcpInstance(bool start)
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var server = builder.AddResource(new DotnetWatchServerResource("dotnet-watch", builder.AppHostDirectory)).AsWatchSystemResource();
        await using var app = builder.Build();
        var notifications = app.Services.GetRequiredService<ResourceNotificationService>();
        await notifications.PublishUpdateAsync(server.Resource, "dotnet-watch-instance", snapshot => snapshot with
        {
            State = start ? KnownResourceStates.Finished : KnownResourceStates.Running
        });
        var commandName = start ? KnownResourceCommands.StartCommand : KnownResourceCommands.StopCommand;
        var addressedInstances = new List<string>();
        var original = new ResourceCommandAnnotation(commandName, commandName, _ => ResourceCommandState.Enabled,
            context =>
            {
                addressedInstances.Add(context.ResourceName);
                return Task.FromResult(CommandResults.Success());
            }, null, arguments: null, null, null, null, false);
        Assert.Single(server.Resource.Annotations.OfType<ResourceLifecycleCommandOverrideAnnotation>(),
            annotation => annotation.CommandName == commandName).Factory(original);

        var result = start
            ? await DotnetWatchServerInfrastructure.StartOwnedAsync(server.Resource, app.Services, TestContext.Current.CancellationToken)
            : await DotnetWatchServerInfrastructure.StopOwnedAsync(server.Resource, app.Services, TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.Message);
        Assert.Equal(["dotnet-watch-instance"], addressedInstances);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public async Task OwnedLifecycleRejectsMissingOrAmbiguousInstances(int instanceCount)
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var server = builder.AddResource(new DotnetWatchServerResource("dotnet-watch", builder.AppHostDirectory)).AsWatchSystemResource();
        await using var app = builder.Build();
        var notifications = app.Services.GetRequiredService<ResourceNotificationService>();
        for (var index = 0; index < instanceCount; index++)
        {
            await notifications.PublishUpdateAsync(server.Resource, $"dotnet-watch-{index}", snapshot => snapshot with
            {
                State = KnownResourceStates.Running
            });
        }
        var called = false;
        var original = new ResourceCommandAnnotation(KnownResourceCommands.StopCommand, "Stop", _ => ResourceCommandState.Enabled,
            _ =>
            {
                called = true;
                return Task.FromResult(CommandResults.Success());
            }, null, arguments: null, null, null, null, false);
        Assert.Single(server.Resource.Annotations.OfType<ResourceLifecycleCommandOverrideAnnotation>(),
            annotation => annotation.CommandName == KnownResourceCommands.StopCommand).Factory(original);

        var result = await DotnetWatchServerInfrastructure.StopOwnedAsync(
            server.Resource, app.Services, TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Equal($"Expected one current watcher instance for 'dotnet-watch', but found {instanceCount}. Restart the watch run.", result.Message);
        Assert.False(called);
    }

    [Fact]
    public void ServerIsHiddenSystemResourceWithoutStopOrRestart()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        using var builder = TestDistributedApplicationBuilder.Create(
            ["AppHost:Run:WatchEnabled=true"]);
        var service = builder.AddDotnetProject("service", Path.GetDirectoryName(CreateProject(workspace.Path))!);

        var server = Assert.Single(builder.Resources.OfType<DotnetWatchServerResource>());
        Assert.Equal(HiddenBehavior.Always, Assert.Single(server.Annotations.OfType<HiddenAnnotation>()).Behavior);
        Assert.Contains(ManifestPublishingCallbackAnnotation.Ignore, server.Annotations);
        Assert.Equal(3, server.Annotations.OfType<ResourceLifecycleCommandOverrideAnnotation>().Count());

        // What DCP adds when it creates the executable: the built-in lifecycle commands are skipped. The coordinator's
        // own restart (a fresh watcher for reference changes) stops the server through a command the user never sees.
        server.AddLifeCycleCommands();
        Assert.Equal(
            [KnownResourceCommands.StartCommand, KnownResourceCommands.StopCommand, KnownResourceCommands.RestartCommand],
            server.Annotations.OfType<ResourceCommandAnnotation>().Select(c => c.Name));
        var stopForRestart = Assert.Single(server.Annotations.OfType<ResourceCommandAnnotation>(), c => c.Name == KnownResourceCommands.StopCommand);
        foreach (var state in new[] { "Running", "Exited" })
        {
            Assert.Equal(ResourceCommandState.Hidden, stopForRestart.UpdateState(new UpdateCommandStateContext
            {
                ResourceSnapshot = new CustomResourceSnapshot { ResourceType = "Executable", Properties = [], State = state },
                Services = new ServiceCollection().BuildServiceProvider(),
            }));
        }

        Assert.Equal([service.Resource], DotnetWatchServerInfrastructure.GetDependents(new DistributedApplicationModel(builder.Resources), server));
    }

    [Theory]
    [InlineData("Running", ResourceCommandState.Hidden)]
    [InlineData("Starting", ResourceCommandState.Hidden)]
    [InlineData("Exited", ResourceCommandState.Hidden)]
    [InlineData("FailedToStart", ResourceCommandState.Hidden)]
    [InlineData("Finished", ResourceCommandState.Hidden)]
    [InlineData("NotStarted", ResourceCommandState.Hidden)]
    public void ServerStartIsOwnedByTheRun(string state, ResourceCommandState expected)
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var server = builder.AddResource(new DotnetWatchServerResource("dotnet-watch", builder.AppHostDirectory)).AsWatchSystemResource();
        server.Resource.AddLifeCycleCommands();
        var command = Assert.Single(server.Resource.Annotations.OfType<ResourceCommandAnnotation>(), c => c.Name == KnownResourceCommands.StartCommand);

        var actual = command.UpdateState(new UpdateCommandStateContext
        {
            ResourceSnapshot = new CustomResourceSnapshot { ResourceType = "Executable", Properties = [], State = state },
            Services = new ServiceCollection().BuildServiceProvider(),
        });

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void StoppedNoticeSaysHowToRecover()
    {
        var notice = DotnetWatchServerInfrastructure.FormatStoppedNotice("dotnet-watch", 137);

        Assert.Contains("exit code 137", notice);
        Assert.Contains("Restart the complete watch run", notice);
        Assert.DoesNotContain("exit code", DotnetWatchServerInfrastructure.FormatStoppedNotice("dotnet-watch", null));
    }

    [Fact]
    [UnsupportedOSPlatform("windows")]
    public void RunDirectoryIsPrivateToTheCurrentUser()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Unix file modes.");

        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var path = Path.Combine(workspace.Path, $"aspw-{Guid.NewGuid():N}"[..17]);

        DotnetWatchServerInfrastructure.EnsurePrivateDirectory(path);
        Assert.Equal(DotnetWatchServerInfrastructure.PrivateDirectoryMode, File.GetUnixFileMode(path));

        // A server restart re-asserts the mode on the directory this run created.
        File.SetUnixFileMode(path, DotnetWatchServerInfrastructure.PrivateDirectoryMode | UnixFileMode.GroupRead | UnixFileMode.OtherExecute);
        DotnetWatchServerInfrastructure.EnsurePrivateDirectory(path);
        Assert.Equal(DotnetWatchServerInfrastructure.PrivateDirectoryMode, File.GetUnixFileMode(path));
    }

    [Fact]
    [UnsupportedOSPlatform("windows")]
    public async Task RunDirectorySharedByConcurrentServersIsAcceptedByAll()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Unix file modes.");

        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var path = Path.Combine(workspace.Path, $"aspw-{Guid.NewGuid():N}"[..17]);

        // The shared server and dedicated servers prepare the same run directory concurrently.
        using var start = new Barrier(16);
        var tasks = Enumerable.Range(0, 16).Select(_ => Task.Run(() =>
        {
            start.SignalAndWait();
            DotnetWatchServerInfrastructure.EnsurePrivateDirectory(path);
        })).ToArray();

        await Task.WhenAll(tasks);
        Assert.Equal(DotnetWatchServerInfrastructure.PrivateDirectoryMode, File.GetUnixFileMode(path));
    }

    [Fact]
    [UnsupportedOSPlatform("windows")]
    public void RunDirectoryThatAlreadyExistsIsRejected()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Unix file modes.");

        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var planted = Path.Combine(workspace.Path, "planted");
        Directory.CreateDirectory(planted);
        Assert.Throws<DistributedApplicationException>(() => DotnetWatchServerInfrastructure.EnsurePrivateDirectory(planted));

        var link = Path.Combine(workspace.Path, "link");
        Directory.CreateSymbolicLink(link, planted);
        Assert.Throws<DistributedApplicationException>(() => DotnetWatchServerInfrastructure.EnsurePrivateDirectory(link));
    }

    [Fact]
    [UnsupportedOSPlatform("windows")]
    public async Task StatusSocketIsOwnerOnly()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Unix sockets.");

        // A short, owner-only temp directory: a TemporaryWorkspace path under macOS's /var/folders/.../T/ pushes the
        // socket past the 103-byte Unix socket path limit.
        var directory = Directory.CreateTempSubdirectory("apx");
        try
        {
            var socket = Path.Combine(directory.FullName, "s.sock");
            await using (var pipe = new System.IO.Pipes.NamedPipeServerStream(
                socket, System.IO.Pipes.PipeDirection.In, 1, System.IO.Pipes.PipeTransmissionMode.Byte,
                System.IO.Pipes.PipeOptions.Asynchronous | System.IO.Pipes.PipeOptions.CurrentUserOnly))
            {
                DotnetWatchServerInfrastructure.RestrictSocketToCurrentUser(socket);

                Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(socket));
            }
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    private static string CreateProject(string root)
    {
        var path = Path.Combine(root, "Service", "Service.csproj");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "<Project />");
        return path;
    }
}
