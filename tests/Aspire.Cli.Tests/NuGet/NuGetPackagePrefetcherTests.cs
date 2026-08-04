// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.CommandLine;
using System.Diagnostics;
using Aspire.Cli.Commands;
using Aspire.Cli.Configuration;
using Aspire.Cli.NuGet;
using Aspire.Cli.Packaging;
using Aspire.Cli.Tests.TestServices;
using Aspire.Cli.Tests.Utils;
using Aspire.Cli.Utils;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Testing;
using Microsoft.Extensions.Time.Testing;

namespace Aspire.Cli.Tests.NuGet;

public class NuGetPackagePrefetcherTests(ITestOutputHelper outputHelper)
{
    [Fact]
    public void CliExecutionContextSetsCommand()
    {
        var workingDir = new DirectoryInfo(Environment.CurrentDirectory);
        var executionContext = TestExecutionContextHelper.CreateExecutionContext(workingDir);

        Assert.Null(executionContext.Command);

        var testCommand = new TestCommand();
        executionContext.Command = testCommand;
        Assert.Same(testCommand, executionContext.Command);
    }

    [Theory]
    [InlineData("run", true)]
    [InlineData("publish", true)]
    [InlineData("deploy", true)]
    [InlineData("new", false)]
    [InlineData("add", false)]
    public void ShouldPrefetchTemplatePackagesReturnsCorrectValueForRuntimeCommands(string commandName, bool expectSkipTemplatePackages)
    {
        var command = new TestCommand(commandName);
        
        // Create test prefetcher to access static method
        bool shouldPrefetch = TestNuGetPrefetcher.TestShouldPrefetchTemplatePackages(command);
        bool shouldSkip = !shouldPrefetch;
        
        Assert.Equal(expectSkipTemplatePackages, shouldSkip);
    }

    [Fact]
    public void ShouldPrefetchTemplatePackagesWithNullCommandReturnsTrueForDefaultBehavior()
    {
        bool shouldPrefetch = TestNuGetPrefetcher.TestShouldPrefetchTemplatePackages(null);
        
        Assert.True(shouldPrefetch);
    }

    [Fact]
    public void NewCommandImplementsIPackageMetaPrefetchingCommand()
    {
        // This test verifies that NewCommand correctly implements the interface
        Assert.True(typeof(IPackageMetaPrefetchingCommand).IsAssignableFrom(typeof(NewCommand)));
    }

    [Fact]
    public void PackageMetaPrefetchingCommandDefaultsToTrueForBothPackageTypes()
    {
        var testCommandWithInterface = new TestCommandWithInterface();
        
        Assert.True(testCommandWithInterface.PrefetchesTemplatePackageMetadata);
        Assert.True(testCommandWithInterface.PrefetchesCliPackageMetadata);
    }

    [Fact]
    public async Task PrefetchingCancellationDueToShutdownLogsCleanMessage()
    {
        var sink = new TestSink();

        using var stoppingCts = new CancellationTokenSource();
        var executionContext = CreateExecutionContext();
        executionContext.CommandSelected.TrySetResult(new TestCommand("new"));

        var features = new TestFeatures();
        features.SetFeature(KnownFeatures.UpdateNotificationsEnabled, true);

        // Async barrier: each callback signals arrival, then waits for the other before cancelling.
        // This ensures both Task.Run calls have started before either cancels the token.
        var templateArrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cliArrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        _ = Task.Run(async () =>
        {
            await Task.WhenAll(templateArrived.Task, cliArrived.Task);
            stoppingCts.Cancel();
        });

        var packagingService = new TestPackagingService
        {
            GetChannelsAsyncCallback = async _ =>
            {
                templateArrived.SetResult();
                await AsyncTestHelpers.WaitForCancellationAsync(stoppingCts.Token);
                throw new UnreachableException();
            }
        };

        var updateNotifier = new TestCliUpdateNotifier
        {
            CheckForCliUpdatesAsyncCallback = async (_, _) =>
            {
                cliArrived.SetResult();
                await AsyncTestHelpers.WaitForCancellationAsync(stoppingCts.Token);
            }
        };

        // Wait for both cancellation messages to appear in the sink.
        var templateTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cliTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        sink.MessageLogged += context =>
        {
            if (context.Message?.Contains("Template package prefetching was cancelled") == true)
            {
                templateTcs.TrySetResult();
            }
            if (context.Message?.Contains("CLI package prefetching was cancelled") == true)
            {
                cliTcs.TrySetResult();
            }
        };

        var prefetcher = CreatePrefetcher(
            executionContext,
            features,
            packagingService,
            updateNotifier,
            sink);

        await prefetcher.StartAsync(stoppingCts.Token).DefaultTimeout();

        // This will timeout if the expected log messages are not produced.
        await Task.WhenAll(templateTcs.Task, cliTcs.Task).DefaultTimeout();

        await prefetcher.StopAsync(CancellationToken.None).DefaultTimeout();
    }

    [Fact]
    public async Task TemplatePrefetchingNonCancellationExceptionLogsExceptionDetails()
    {
        var sink = new TestSink();

        var executionContext = CreateExecutionContext();
        executionContext.CommandSelected.TrySetResult(new TestCommand("new"));

        var ex = new InvalidOperationException("Something went wrong");
        var packagingService = new TestPackagingService
        {
            GetChannelsAsyncCallback = _ => throw ex
        };

        // Wait for the error message to appear in the sink.
        var errorTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        sink.MessageLogged += context =>
        {
            if (context.Exception == ex)
            {
                errorTcs.TrySetResult();
            }
        };

        var prefetcher = CreatePrefetcher(
            executionContext,
            new TestFeatures(),
            packagingService,
            new TestCliUpdateNotifier(),
            sink);

        await prefetcher.StartAsync(CancellationToken.None).DefaultTimeout();

        // This will timeout if the expected log messages are not produced.
        await errorTcs.Task.DefaultTimeout();

        await prefetcher.StopAsync(CancellationToken.None).DefaultTimeout();
    }

    // The tests below resolve the real commands and drive the real NuGetPackagePrefetcher rather than
    // going through TestNuGetPrefetcher at the bottom of this file. That helper re-implements the
    // production prefetch decision instead of calling it, and has already drifted from it: its
    // IsRuntimeOnlyCommand is missing "do". Tests written against the copy pass no matter what the
    // production code decides, which is exactly the behaviour these tests need to pin down.
    [Theory]
    [InlineData(typeof(LsCommand))]
    [InlineData(typeof(PsCommand))]
    public void ReadOnlyCommandsDisablePackageMetadataPrefetching(Type commandType)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper);
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService(commandType);

        var prefetchingCommand = Assert.IsAssignableFrom<IPackageMetaPrefetchingCommand>(command);
        Assert.False(prefetchingCommand.PrefetchesTemplatePackageMetadata);
        Assert.False(prefetchingCommand.PrefetchesCliPackageMetadata);
    }

    [Theory]
    [InlineData(typeof(LsCommand))]
    [InlineData(typeof(PsCommand))]
    public async Task ReadOnlyCommandsStartNoPrefetching(Type commandType)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper);
        using var provider = services.BuildServiceProvider();

        var executionContext = CreateExecutionContext();
        executionContext.CommandSelected.TrySetResult((Command)provider.GetRequiredService(commandType));

        var features = new TestFeatures();
        features.SetFeature(KnownFeatures.UpdateNotificationsEnabled, true);

        var templateStarted = false;
        var packagingService = new TestPackagingService
        {
            GetChannelsAsyncCallback = _ =>
            {
                templateStarted = true;
                return Task.FromResult(Enumerable.Empty<PackageChannel>());
            }
        };

        var cliStarted = false;
        var updateNotifier = new TestCliUpdateNotifier
        {
            CheckForCliUpdatesAsyncCallback = (_, _) =>
            {
                cliStarted = true;
                return Task.CompletedTask;
            }
        };

        var prefetcher = CreatePrefetcher(
            executionContext,
            features,
            packagingService,
            updateNotifier);

        await prefetcher.StartAsync(CancellationToken.None).DefaultTimeout();
        await prefetcher.ExecuteTask!.DefaultTimeout();
        await prefetcher.StopAsync(CancellationToken.None).DefaultTimeout();

        Assert.False(templateStarted);
        Assert.False(cliStarted);
    }

    // Command selection happens in BaseCommand's action, which the host reaches only after the first-run
    // banner has played. The banner spends 1660ms in fixed delays, so a prefetcher that gave up waiting
    // after a second would fall back to the null default and prefetch for `ls`/`ps` anyway. Advance the
    // clock past that former timeout before selecting the command.
    [Fact]
    public async Task CommandSelectedAfterABannerLengthDelayStillDisablesPrefetching()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper);
        using var provider = services.BuildServiceProvider();

        var executionContext = CreateExecutionContext();
        var timeProvider = new FakeTimeProvider();

        var features = new TestFeatures();
        features.SetFeature(KnownFeatures.UpdateNotificationsEnabled, true);

        var templateStarted = false;
        var packagingService = new TestPackagingService
        {
            GetChannelsAsyncCallback = _ =>
            {
                templateStarted = true;
                return Task.FromResult(Enumerable.Empty<PackageChannel>());
            }
        };

        var cliStarted = false;
        var updateNotifier = new TestCliUpdateNotifier
        {
            CheckForCliUpdatesAsyncCallback = (_, _) =>
            {
                cliStarted = true;
                return Task.CompletedTask;
            }
        };

        var prefetcher = CreatePrefetcher(
            executionContext,
            features,
            packagingService,
            updateNotifier,
            timeProvider: timeProvider);

        await prefetcher.StartAsync(CancellationToken.None).DefaultTimeout();

        // The removed timeout was one second. 1500ms crosses that boundary without coupling
        // this test to every delay that contributes to the banner's full 1660ms duration.
        timeProvider.Advance(TimeSpan.FromMilliseconds(1500));

        executionContext.CommandSelected.TrySetResult(provider.GetRequiredService<LsCommand>());

        await prefetcher.ExecuteTask!.DefaultTimeout();
        await prefetcher.StopAsync(CancellationToken.None).DefaultTimeout();

        Assert.False(templateStarted);
        Assert.False(cliStarted);
    }

    [Fact]
    public async Task InFlightPrefetchingCompletesBeforeTheServiceStops()
    {
        var executionContext = CreateExecutionContext();
        executionContext.CommandSelected.TrySetResult(new TestCommand("new"));

        var features = new TestFeatures();
        features.SetFeature(KnownFeatures.UpdateNotificationsEnabled, true);

        var templateEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var templateFinished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cliEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cliFinished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var packagingService = new TestPackagingService
        {
            GetChannelsAsyncCallback = async token =>
            {
                templateEntered.SetResult();
                try
                {
                    await AsyncTestHelpers.WaitForCancellationAsync(token);
                }
                finally
                {
                    templateFinished.SetResult();
                }

                throw new UnreachableException();
            }
        };

        var updateNotifier = new TestCliUpdateNotifier
        {
            CheckForCliUpdatesAsyncCallback = async (_, token) =>
            {
                cliEntered.SetResult();
                try
                {
                    await AsyncTestHelpers.WaitForCancellationAsync(token);
                }
                finally
                {
                    cliFinished.SetResult();
                }
            }
        };

        var prefetcher = CreatePrefetcher(
            executionContext,
            features,
            packagingService,
            updateNotifier);

        await prefetcher.StartAsync(CancellationToken.None).DefaultTimeout();
        await Task.WhenAll(templateEntered.Task, cliEntered.Task).DefaultTimeout();

        Assert.False(prefetcher.ExecuteTask!.IsCompleted);

        await prefetcher.StopAsync(CancellationToken.None).DefaultTimeout();

        Assert.True(templateFinished.Task.IsCompletedSuccessfully);
        Assert.True(cliFinished.Task.IsCompletedSuccessfully);
    }

    private static NuGetPackagePrefetcher CreatePrefetcher(
        CliExecutionContext executionContext,
        IFeatures features,
        IPackagingService packagingService,
        ICliUpdateNotifier updateNotifier,
        TestSink? sink = null,
        TimeProvider? timeProvider = null)
    {
        return new NuGetPackagePrefetcher(
            CreateLogger(sink ?? new TestSink()),
            timeProvider ?? TimeProvider.System,
            executionContext,
            features,
            packagingService,
            updateNotifier);
    }

    private static TestLogger<NuGetPackagePrefetcher> CreateLogger(TestSink sink)
        => new(new TestLoggerFactory(sink, enabled: true));

    private static CliExecutionContext CreateExecutionContext()
    {
        var workingDir = new DirectoryInfo(Environment.CurrentDirectory);
        return TestExecutionContextHelper.CreateExecutionContext(workingDir);
    }
}

// Test helper class to expose static methods for testing
internal static class TestNuGetPrefetcher
{
    public static bool TestShouldPrefetchTemplatePackages(BaseCommand? command)
    {
        // If the command implements IPackageMetaPrefetchingCommand, use its setting
        if (command is IPackageMetaPrefetchingCommand prefetchingCommand)
        {
            return prefetchingCommand.PrefetchesTemplatePackageMetadata;
        }

        // Default behavior: prefetch templates for all commands except run, publish, deploy
        return command is null || !IsRuntimeOnlyCommand(command);
    }

    public static bool TestShouldPrefetchCliPackages(BaseCommand? command)
    {
        // If the command implements IPackageMetaPrefetchingCommand, use its setting
        if (command is IPackageMetaPrefetchingCommand prefetchingCommand)
        {
            return prefetchingCommand.PrefetchesCliPackageMetadata;
        }

        // Default behavior: always prefetch CLI packages for update notifications
        return true;
    }

    private static bool IsRuntimeOnlyCommand(BaseCommand command)
    {
        var commandName = command.Name;
        return commandName is "run" or "publish" or "deploy";
    }
}

// Test command implementations
internal sealed class TestCommand : BaseCommand
{
    public TestCommand(string name = "test") : base(name, "Test command", new CommonCommandServices(null!, null!, null!, null!, null!, null!, null!, null!))
    {
    }

    protected override Task<CommandResult> ExecuteAsync(ParseResult parseResult, CancellationToken cancellationToken)
    {
        return Task.FromResult(CommandResult.Success());
    }
}

internal sealed class TestCommandWithInterface : BaseCommand, IPackageMetaPrefetchingCommand
{
    public TestCommandWithInterface() : base("test-interface", "Test command with interface", new CommonCommandServices(null!, null!, null!, null!, null!, null!, null!, null!))
    {
    }

    public bool PrefetchesTemplatePackageMetadata => true;
    public bool PrefetchesCliPackageMetadata => true;

    protected override Task<CommandResult> ExecuteAsync(ParseResult parseResult, CancellationToken cancellationToken)
    {
        return Task.FromResult(CommandResult.Success());
    }
}
