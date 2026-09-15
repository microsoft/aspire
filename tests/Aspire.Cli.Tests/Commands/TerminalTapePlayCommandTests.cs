// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Backchannel;
using Aspire.Cli.Commands;
using Aspire.Cli.Tests.TestServices;
using Aspire.Cli.Tests.Utils;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace Aspire.Cli.Tests.Commands;

public class TerminalTapePlayCommandTests(ITestOutputHelper outputHelper)
{
    [Theory]
    [InlineData("terminal tape --help", 0)]
    [InlineData("terminal tape play --help", 0)]
    [InlineData("terminal tape", 1)]
    [InlineData("terminal tape play", 1)]
    [InlineData("terminal tape play shell", 1)]
    [InlineData("terminal tape play --tape-file probe.tape", 1)]
    public async Task CommandHierarchy(string arguments, int expectedExitCode)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper,
            options => options.EnabledFeatures = [KnownFeatures.TerminalCommandsEnabled]);
        using var provider = services.BuildServiceProvider();

        var result = provider.GetRequiredService<RootCommand>().Parse(arguments);

        Assert.Equal(expectedExitCode, await result.InvokeAsync().DefaultTimeout());
    }

    [Fact]
    public void RequiresTerminalFeature()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper);
        using var provider = services.BuildServiceProvider();

        var result = provider.GetRequiredService<RootCommand>().Parse("terminal tape play shell --tape-file probe.tape");

        Assert.NotEmpty(result.Errors);
    }

    [Theory]
    [InlineData("missing.tape", null, null)]
    [InlineData("broken.tape", "NotACommand", ":1:1:")]
    public async Task InvalidFileFailsBeforeResourceDiscovery(string path, string? contents, string? diagnosticLocation)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        if (contents is not null)
        {
            await File.WriteAllTextAsync(Path.Combine(workspace.WorkspaceRoot.FullName, path), contents);
        }
        using var errors = new StringWriter();
        var (provider, backchannel) = TerminalCommandTestServices.CreateProvider(workspace, outputHelper, _ => { },
            options => options.ErrorTextWriter = errors);
        using (provider)
        {
            var result = provider.GetRequiredService<RootCommand>().Parse(
                ["terminal", "tape", "play", "shell", "--tape-file", path]);

            Assert.Equal(CliExitCodes.InvalidCommand, await result.InvokeAsync().DefaultTimeout());
            Assert.Equal(0, backchannel.GetResourceSnapshotsCallCount);
            Assert.Contains(path, errors.ToString());
            if (diagnosticLocation is not null)
            {
                Assert.Contains(diagnosticLocation, errors.ToString());
            }
        }
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("4294968")]
    [InlineData("2147483647")]
    public async Task InvalidTimeoutFailsBeforeResourceDiscovery(string timeout)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var (provider, backchannel) = TerminalCommandTestServices.CreateProvider(workspace, outputHelper, _ => { });
        using (provider)
        {
            var result = provider.GetRequiredService<RootCommand>().Parse(
                ["terminal", "tape", "play", "shell", "--tape-file", "probe.tape", "--timeout", timeout]);

            Assert.Equal(CliExitCodes.InvalidCommand, await result.InvokeAsync().DefaultTimeout());
            Assert.Equal(0, backchannel.GetResourceSnapshotsCallCount);
        }
    }

    [Fact]
    public async Task NoRunningAppHostFails()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        await File.WriteAllTextAsync(Path.Combine(workspace.WorkspaceRoot.FullName, "probe.tape"), "");
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper,
            options => options.EnabledFeatures = [KnownFeatures.TerminalCommandsEnabled]);
        using var provider = services.BuildServiceProvider();

        var result = provider.GetRequiredService<RootCommand>().Parse("terminal tape play shell --tape-file probe.tape");

        Assert.Equal(CliExitCodes.FailedToFindProject, await result.InvokeAsync().DefaultTimeout());
    }

    [Theory]
    [InlineData("incompatible", CliExitCodes.AppHostIncompatible)]
    [InlineData("missing", CliExitCodes.InvalidCommand)]
    [InlineData("unavailable", CliExitCodes.InvalidCommand)]
    [InlineData("exited", CliExitCodes.FailedToExecuteResourceCommand)]
    [InlineData("replicas", CliExitCodes.InvalidCommand)]
    [InlineData("wrong-replica", CliExitCodes.InvalidCommand)]
    public async Task InvalidResourceFailsBeforeConnecting(string scenario, int expectedExitCode)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        await File.WriteAllTextAsync(Path.Combine(workspace.WorkspaceRoot.FullName, "probe.tape"), "");
        using var provider = CreateProvider(workspace, null, configure: backchannel =>
        {
            switch (scenario)
            {
                case "incompatible":
                    backchannel.SupportsTerminalsV1 = false;
                    break;
                case "missing":
                    backchannel.ResourceSnapshots = [];
                    break;
                case "unavailable":
                    backchannel.TerminalInfoResponse = new() { IsAvailable = false };
                    break;
                case "exited":
                    backchannel.TerminalInfoResponse = new()
                    {
                        IsAvailable = true,
                        Replicas = [new() { ReplicaIndex = 0, Label = "shell-0", ConsumerUdsPath = "unused", IsAlive = false }]
                    };
                    break;
                case "replicas":
                    backchannel.TerminalInfoResponse = new()
                    {
                        IsAvailable = true,
                        Replicas =
                        [
                            new() { ReplicaIndex = 0, Label = "shell-0", ConsumerUdsPath = "unused", IsAlive = true },
                            new() { ReplicaIndex = 1, Label = "shell-1", ConsumerUdsPath = "unused", IsAlive = true }
                        ]
                    };
                    break;
            }
        });
        string[] replicaArguments = scenario == "wrong-replica" ? ["--replica", "7"] : [];
        var result = provider.GetRequiredService<RootCommand>().Parse(
            ["terminal", "tape", "play", "shell", "--tape-file", "probe.tape", .. replicaArguments]);

        Assert.Equal(expectedExitCode, await result.InvokeAsync().DefaultTimeout());
    }

    [Theory]
    [InlineData("", 80, 24)]
    [InlineData("Ready for input", 101, 37)]
    public async Task EmptyTapeReadsInitialScreenWithoutResizingOrStoppingProducer(string screen, int width, int height)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        await using var host = await TerminalTapeTestHost.StartAsync(width, height);
        if (screen.Length > 0)
        {
            await host.WriteAsync(screen);
        }
        var stdout = new TestOutputTextWriter(outputHelper);
        using var provider = CreateProvider(workspace, host.SocketPath, stdout);
        var directory = workspace.WorkspaceRoot.CreateSubdirectory("tape files");
        await File.WriteAllTextAsync(Path.Combine(directory.FullName, "probe.tape"), "# Inspect the current screen.");
        var result = provider.GetRequiredService<RootCommand>().Parse(
            ["terminal", "tape", "play", "shell", "--tape-file", Path.Combine("tape files", "probe.tape"), "--replica", "0"]);

        Assert.Equal(CliExitCodes.Success, await result.InvokeAsync().DefaultTimeout());
        Assert.Equal(host.GetScreenText().TrimEnd(), string.Join('\n', stdout.Logs).TrimEnd());
        Assert.Equal((width, height), host.GetDimensions());
        Assert.True(host.IsRunning);
        await host.WriteAsync("Still running");
    }

    [Fact]
    public async Task PlaysInputAndWaitsForResponseFromSourcedTape()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        await using var host = await TerminalTapeTestHost.StartAsync();
        await host.WriteAsync("Ready for input\r\n");
        var stdout = new TestOutputTextWriter(outputHelper);
        using var provider = CreateProvider(workspace, host.SocketPath, stdout);
        var directory = workspace.WorkspaceRoot.CreateSubdirectory("scripts");
        directory.CreateSubdirectory("nested");
        await File.WriteAllTextAsync(Path.Combine(directory.FullName, "play.tape"), """
            Set TypingSpeed 0
            Source nested/input.tape
            Wait+Screen /Accepted: hello/
            """);
        await File.WriteAllTextAsync(Path.Combine(directory.FullName, "nested", "input.tape"), """
            Wait+Screen /Ready for input/
            Type "hello"
            Enter
            """);
        var result = provider.GetRequiredService<RootCommand>().Parse(
            ["terminal", "tape", "play", "shell", "--tape-file", Path.Combine("scripts", "play.tape")]);

        var play = result.InvokeAsync();
        Assert.Equal("hello\r", await host.ReadInputAsync(6));
        await host.WriteAsync("Accepted: hello\r\n");

        Assert.Equal(CliExitCodes.Success, await play.DefaultTimeout());
        Assert.Equal(host.GetScreenText().TrimEnd(), string.Join('\n', stdout.Logs).TrimEnd());
        Assert.True(host.IsRunning);
    }

    [Fact]
    public async Task ResolvesTextOutputBesideRootTape()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        await using var host = await TerminalTapeTestHost.StartAsync();
        await host.WriteAsync("Ready for input");
        using var provider = CreateProvider(workspace, host.SocketPath);
        var directory = workspace.WorkspaceRoot.CreateSubdirectory("scripts");
        directory.CreateSubdirectory("nested");
        await File.WriteAllTextAsync(Path.Combine(directory.FullName, "play.tape"),
            "Output screen.txt\nSource nested/wait.tape");
        await File.WriteAllTextAsync(Path.Combine(directory.FullName, "nested", "wait.tape"),
            "Output ignored.txt\nWait+Screen /Ready for input/");
        var result = provider.GetRequiredService<RootCommand>().Parse(
            ["terminal", "tape", "play", "shell", "--tape-file", Path.Combine("scripts", "play.tape")]);

        Assert.Equal(CliExitCodes.Success, await result.InvokeAsync().DefaultTimeout());
        Assert.Collection(Directory.EnumerateFiles(directory.FullName, "*.txt", SearchOption.AllDirectories),
            path => Assert.Equal(Path.Combine(directory.FullName, "screen.txt"), path));
        var capture = await File.ReadAllTextAsync(Path.Combine(directory.FullName, "screen.txt"));
        await Verify(capture.Replace(workspace.WorkspaceRoot.FullName, "{workspace}"), "txt");
    }

    [Theory]
    [InlineData("Screenshot unsupported.png")]
    [InlineData("Set Width 120")]
    [InlineData("Env SECRET value")]
    [InlineData("Source missing.tape")]
    [InlineData("Output missing-directory/screen.txt")]
    public async Task InvalidTapePreflightDoesNotSendEarlierInput(string invalidCommand)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        await using var host = await TerminalTapeTestHost.StartAsync();
        using var provider = CreateProvider(workspace, host.SocketPath);
        var file = Path.Combine(workspace.WorkspaceRoot.FullName, "play.tape");
        await File.WriteAllTextAsync(file, $"Set TypingSpeed 0\nType \"unexpected\"\n{invalidCommand}");
        var command = provider.GetRequiredService<RootCommand>();
        var invalid = command.Parse("terminal tape play shell --tape-file play.tape");

        Assert.Equal(CliExitCodes.InvalidCommand, await invalid.InvokeAsync().DefaultTimeout());

        // A later valid tape provides a deterministic input boundary: rejected preflight must not
        // leave any "unexpected" bytes ahead of this marker, without relying on a quiet-time delay.
        await File.WriteAllTextAsync(file, "Set TypingSpeed 0\nType \"marker\"");
        var valid = command.Parse("terminal tape play shell --tape-file play.tape");
        Assert.Equal(CliExitCodes.Success, await valid.InvokeAsync().DefaultTimeout());
        Assert.Equal("marker", await host.ReadInputAsync(6));
    }

    [Fact]
    public async Task WaitFailurePrintsCurrentScreenAndSourceLocation()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        await using var host = await TerminalTapeTestHost.StartAsync();
        await host.WriteAsync("Waiting for authentication");
        var stdout = new TestOutputTextWriter(outputHelper);
        using var errors = new StringWriter();
        using var provider = CreateProvider(workspace, host.SocketPath, stdout, errors);
        await File.WriteAllTextAsync(Path.Combine(workspace.WorkspaceRoot.FullName, "wait.tape"),
            "# Deliberately impossible condition\nWait+Screen@100ms /never_appears/");
        var result = provider.GetRequiredService<RootCommand>().Parse("terminal tape play shell --tape-file wait.tape");

        Assert.Equal(CliExitCodes.FailedToExecuteResourceCommand, await result.InvokeAsync().DefaultTimeout());
        Assert.Equal(host.GetScreenText().TrimEnd(), string.Join('\n', stdout.Logs).TrimEnd());
        Assert.Contains("wait.tape:2:1:", errors.ToString());
        Assert.True(host.IsRunning);
    }

    [Fact]
    public async Task OverallTimeoutCancelsPlaybackWithoutStoppingProducer()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        await using var host = await TerminalTapeTestHost.StartAsync();
        var time = new FakeTimeProvider();
        using var provider = CreateProvider(workspace, host.SocketPath,
            configureOptions: options => options.TimeProvider = time);
        await File.WriteAllTextAsync(Path.Combine(workspace.WorkspaceRoot.FullName, "wait.tape"),
            "Set TypingSpeed 0\nType \"started\"\nSleep 60s");
        var result = provider.GetRequiredService<RootCommand>().Parse("terminal tape play shell --tape-file wait.tape --timeout 5");

        var play = result.InvokeAsync();
        Assert.Equal("started", await host.ReadInputAsync(7));
        time.Advance(TimeSpan.FromSeconds(5));

        Assert.Equal(CliExitCodes.WaitTimeout, await play.DefaultTimeout());
        Assert.True(host.IsRunning);
    }

    [Fact]
    public async Task UserCancellationLeavesProducerRunning()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        await using var host = await TerminalTapeTestHost.StartAsync();
        using var provider = CreateProvider(workspace, host.SocketPath);
        using var cancellation = new CancellationTokenSource();
        await File.WriteAllTextAsync(Path.Combine(workspace.WorkspaceRoot.FullName, "wait.tape"),
            "Set TypingSpeed 0\nType \"started\"\nSleep 60s");
        var result = provider.GetRequiredService<RootCommand>().Parse("terminal tape play shell --tape-file wait.tape");

        var play = result.InvokeAsync(cancellationToken: cancellation.Token);
        Assert.Equal("started", await host.ReadInputAsync(7));
        await cancellation.CancelAsync();

        Assert.Equal(CliExitCodes.Cancelled, await play.DefaultTimeout());
        Assert.True(host.IsRunning);
    }

    [Fact]
    public async Task DisconnectionDuringPlaybackFailsPromptly()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        await using var host = await TerminalTapeTestHost.StartAsync();
        using var errors = new StringWriter();
        using var provider = CreateProvider(workspace, host.SocketPath, stderr: errors);
        await File.WriteAllTextAsync(Path.Combine(workspace.WorkspaceRoot.FullName, "wait.tape"),
            "Set TypingSpeed 0\nType \"started\"\nSleep 60s");
        var result = provider.GetRequiredService<RootCommand>().Parse("terminal tape play shell --tape-file wait.tape");

        var play = result.InvokeAsync();
        Assert.Equal("started", await host.ReadInputAsync(7));
        await host.DisposeAsync();

        Assert.Equal(CliExitCodes.FailedToExecuteResourceCommand, await play.DefaultTimeout());
        Assert.Contains("connection closed", errors.ToString());
    }

    [Fact]
    public async Task UnreachableTerminalFails()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        await using var host = await TerminalTapeTestHost.StartAsync();
        await host.DisposeAsync();
        using var provider = CreateProvider(workspace, host.SocketPath);
        await File.WriteAllTextAsync(Path.Combine(workspace.WorkspaceRoot.FullName, "probe.tape"), "");
        var result = provider.GetRequiredService<RootCommand>().Parse("terminal tape play shell --tape-file probe.tape");

        Assert.Equal(CliExitCodes.FailedToExecuteResourceCommand, await result.InvokeAsync().DefaultTimeout());
    }

    private ServiceProvider CreateProvider(
        TemporaryWorkspace workspace,
        string? socketPath,
        TestOutputTextWriter? stdout = null,
        StringWriter? stderr = null,
        Action<TestAppHostAuxiliaryBackchannel>? configure = null,
        Action<CliServiceCollectionTestOptions>? configureOptions = null)
    {
        var (provider, _) = TerminalCommandTestServices.CreateProvider(workspace, outputHelper, backchannel =>
        {
            backchannel.ResourceSnapshots =
            [
                new ResourceSnapshot { Name = "shell-0", DisplayName = "shell", ResourceType = "Executable", State = "Running" }
            ];
            backchannel.TerminalInfoResponse = new()
            {
                IsAvailable = true,
                Replicas = [new() { ReplicaIndex = 0, Label = "shell-0", ConsumerUdsPath = socketPath ?? "unused", IsAlive = true }]
            };
            configure?.Invoke(backchannel);
        }, options =>
        {
            options.OutputTextWriter = stdout;
            options.ErrorTextWriter = stderr;
            configureOptions?.Invoke(options);
        });
        return provider;
    }
}
