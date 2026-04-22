// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.CommandLine;
using System.Diagnostics;
using Aspire.Cli.Commands;
using Aspire.Cli.NuGet;
using Aspire.Cli.Tests.TestServices;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.Extensions.Logging.Testing;

namespace Aspire.Cli.Tests.NuGet;

public class NuGetPackagePrefetcherTests
{
    [Fact]
    public void CliExecutionContextSetsCommand()
    {
        var workingDir = new DirectoryInfo(Environment.CurrentDirectory);
        var hivesDir = new DirectoryInfo(Path.Combine(Environment.CurrentDirectory, "hives"));
    var cacheDir = new DirectoryInfo(Path.Combine(workingDir.FullName, ".aspire", "cache"));
    var executionContext = new CliExecutionContext(workingDir, hivesDir, cacheDir, new DirectoryInfo(Path.Combine(Path.GetTempPath(), "aspire-test-runtimes")), new DirectoryInfo(Path.Combine(Path.GetTempPath(), "aspire-test-logs")), "test.log");
        
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
        var logger = new TestLogger<NuGetPackagePrefetcher>(new TestLoggerFactory(sink, enabled: true));

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

        var prefetcher = new NuGetPackagePrefetcher(
            logger,
            executionContext,
            features,
            packagingService,
            updateNotifier);

        await prefetcher.StartAsync(stoppingCts.Token).DefaultTimeout();

        // This will timeout if the expected log messages are not produced.
        await Task.WhenAll(templateTcs.Task, cliTcs.Task).DefaultTimeout();

        await prefetcher.StopAsync(CancellationToken.None).DefaultTimeout();
    }

    [Fact]
    public async Task TemplatePrefetchingNonCancellationExceptionLogsExceptionDetails()
    {
        var sink = new TestSink();
        var logger = new TestLogger<NuGetPackagePrefetcher>(new TestLoggerFactory(sink, enabled: true));

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

        var prefetcher = new NuGetPackagePrefetcher(
            logger,
            executionContext,
            new TestFeatures(),
            packagingService,
            new TestCliUpdateNotifier());

        await prefetcher.StartAsync(CancellationToken.None).DefaultTimeout();

        // This will timeout if the expected log messages are not produced.
        await errorTcs.Task.DefaultTimeout();

        await prefetcher.StopAsync(CancellationToken.None).DefaultTimeout();
    }

    private static CliExecutionContext CreateExecutionContext()
    {
        var workingDir = new DirectoryInfo(Environment.CurrentDirectory);
        var hivesDir = new DirectoryInfo(Path.Combine(Environment.CurrentDirectory, "hives"));
        var cacheDir = new DirectoryInfo(Path.Combine(workingDir.FullName, ".aspire", "cache"));
        return new CliExecutionContext(
            workingDir,
            hivesDir,
            cacheDir,
            new DirectoryInfo(Path.Combine(Path.GetTempPath(), "aspire-test-runtimes")),
            new DirectoryInfo(Path.Combine(Path.GetTempPath(), "aspire-test-logs")),
            "test.log");
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
    public TestCommand(string name = "test") : base(name, "Test command", null!, null!, null!, null!, null!)
    {
    }

    protected override Task<int> ExecuteAsync(ParseResult parseResult, CancellationToken cancellationToken)
    {
        return Task.FromResult(0);
    }
}

internal sealed class TestCommandWithInterface : BaseCommand, IPackageMetaPrefetchingCommand
{
    public TestCommandWithInterface() : base("test-interface", "Test command with interface", null!, null!, null!, null!, null!)
    {
    }

    public bool PrefetchesTemplatePackageMetadata => true;
    public bool PrefetchesCliPackageMetadata => true;

    protected override Task<int> ExecuteAsync(ParseResult parseResult, CancellationToken cancellationToken)
    {
        return Task.FromResult(0);
    }
}
