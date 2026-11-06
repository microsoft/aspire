// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Runtime.Versioning;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Dotnet.Watch;
using Aspire.Hosting.Utils;
using Microsoft.Extensions.DependencyInjection;

namespace Aspire.Hosting.Dotnet.Tests;

// SPIKE: the watch server is hidden infrastructure with a private run directory
// (docs/plans/project-v2-csharpprogram-watch.md, Session 6).
public class DotnetWatchServerInfrastructureTests(ITestOutputHelper outputHelper)
{
    [Fact]
    public void ServerIsHiddenSystemResourceWithoutStopOrRestart()
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        using var builder = TestDistributedApplicationBuilder.Create(
            ["AppHost:Run:WatchEnabled=true", $"{DotnetWatchCoordinator.ProxyPathConfigurationKey}=/opt/aspire/aspire.dll"]);
        var service = builder.AddDotnetProject("service", Path.GetDirectoryName(CreateProject(workspace.Path))!);

        var server = Assert.Single(builder.Resources.OfType<DotnetWatchServerResource>());
        Assert.Equal(HiddenBehavior.Always, Assert.Single(server.Annotations.OfType<HiddenAnnotation>()).Behavior);
        Assert.Contains(ManifestPublishingCallbackAnnotation.Ignore, server.Annotations);
        Assert.Single(server.Annotations.OfType<ExcludeLifecycleCommandsAnnotation>());

        // What DCP adds when it creates the executable: the built-in lifecycle commands are skipped.
        server.AddLifeCycleCommands();
        var command = Assert.Single(server.Annotations.OfType<ResourceCommandAnnotation>());
        Assert.Equal(KnownResourceCommands.StartCommand, command.Name);

        Assert.Equal([service.Resource], DotnetWatchServerInfrastructure.GetDependents(new DistributedApplicationModel(builder.Resources), server));
    }

    [Theory]
    [InlineData("Running", ResourceCommandState.Hidden)]
    [InlineData("Starting", ResourceCommandState.Hidden)]
    [InlineData("Exited", ResourceCommandState.Enabled)]
    [InlineData("FailedToStart", ResourceCommandState.Enabled)]
    [InlineData("Finished", ResourceCommandState.Enabled)]
    [InlineData("NotStarted", ResourceCommandState.Enabled)]
    public void StartIsOnlyOfferedWhileServerIsStopped(string state, ResourceCommandState expected)
    {
        using var builder = TestDistributedApplicationBuilder.Create();
        var server = builder.AddResource(new DotnetWatchServerResource("dotnet-watch", builder.AppHostDirectory)).AsWatchSystemResource();
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
        Assert.Contains("aspire resource dotnet-watch start", notice);
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

        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var socket = Path.Combine(workspace.Path, "s.sock");
        await using var pipe = new System.IO.Pipes.NamedPipeServerStream(
            socket, System.IO.Pipes.PipeDirection.In, 1, System.IO.Pipes.PipeTransmissionMode.Byte,
            System.IO.Pipes.PipeOptions.Asynchronous | System.IO.Pipes.PipeOptions.CurrentUserOnly);

        DotnetWatchServerInfrastructure.RestrictSocketToCurrentUser(socket);

        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(socket));
    }

    private static string CreateProject(string root)
    {
        var path = Path.Combine(root, "Service", "Service.csproj");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "<Project />");
        return path;
    }
}
