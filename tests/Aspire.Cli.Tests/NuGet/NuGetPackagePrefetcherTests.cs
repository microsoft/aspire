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
using Microsoft.Extensions.Logging.Abstractions;
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

    [Fact]
    public void NewCommandsDefaultToNoPackageMetadataPrefetching()
    {
        var command = new TestCommand();

        Assert.False(command.PrefetchesTemplatePackageMetadata);
        Assert.False(command.PrefetchesCliPackageMetadata);
    }

#if DEBUG
    [Fact]
    public async Task TemplatePackageMetadataConsumptionRequiresPrefetchCapabilityInTests()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.NuGetPackageCacheFactory = _ => new FakeNuGetPackageCache();
        });
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<LsCommand>();
        command.SelectForExecution(command.Parse("ls"));
        var channel = (await provider.GetRequiredService<IPackagingService>().GetChannelsAsync()).First();

        var exception = await Assert.ThrowsAsync<PackageMetadataPrefetchingValidationException>(() =>
            channel.GetTemplatePackagesAsync(workspace.WorkspaceRoot, CancellationToken.None));

        Assert.Contains(nameof(BaseCommand.PrefetchesTemplatePackageMetadata), exception.Message);
    }

    [Fact]
    public async Task CachedCliPackageMetadataConsumptionRequiresPrefetchCapabilityInTests()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.NuGetPackageCacheFactory = _ => new FakeNuGetPackageCache();
        });
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<LsCommand>();
        command.SelectForExecution(command.Parse("ls"));
        var updateNotifier = provider.GetRequiredService<ICliUpdateNotifier>();

        var exception = Assert.Throws<PackageMetadataPrefetchingValidationException>(() => updateNotifier.IsUpdateAvailable());
        Assert.Contains(nameof(BaseCommand.PrefetchesCliPackageMetadata), exception.Message);

        _ = await updateNotifier.GetVersionStatusAsync(workspace.WorkspaceRoot, CancellationToken.None);
    }
#endif

#if !DEBUG
    [Fact]
    public async Task PackageMetadataConsumptionWithoutPrefetchCapabilityDoesNotThrowInReleaseBuilds()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.NuGetPackageCacheFactory = _ => new FakeNuGetPackageCache();
        });
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<LsCommand>();
        command.SelectForExecution(command.Parse("ls"));
        var channel = (await provider.GetRequiredService<IPackagingService>().GetChannelsAsync()).First();

        _ = await channel.GetTemplatePackagesAsync(workspace.WorkspaceRoot, CancellationToken.None);
        _ = provider.GetRequiredService<ICliUpdateNotifier>().IsUpdateAvailable();
    }
#endif

    [Fact]
    public async Task PrefetchingCancellationDueToShutdownLogsCleanMessage()
    {
        var sink = new TestSink();

        using var stoppingCts = new CancellationTokenSource();
        var executionContext = CreateExecutionContext();
        executionContext.CommandSelected.TrySetResult(new TestCommand(prefetchesTemplatePackages: true, prefetchesCliPackages: true));

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
        executionContext.CommandSelected.TrySetResult(new TestCommand(prefetchesTemplatePackages: true, prefetchesCliPackages: true));

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

    [Theory]
    [InlineData(typeof(NewCommand), true, true)]
    [InlineData(typeof(InitCommand), true, true)]
    [InlineData(typeof(AddCommand), false, true)]
    [InlineData(typeof(PublishCommand), false, true)]
    [InlineData(typeof(UpdateCommand), false, true)]
    [InlineData(typeof(RunCommand), false, true)]
    [InlineData(typeof(LsCommand), false, false)]
    [InlineData(typeof(PsCommand), false, false)]
    [InlineData(typeof(IntegrationListCommand), false, false)]
    [InlineData(typeof(IntegrationSearchCommand), false, false)]
    [InlineData(typeof(DoctorCommand), false, false)]
    [InlineData(typeof(IntegrationCommand), false, false)]
    public async Task CommandsStartExpectedPackageMetadataPrefetching(Type commandType, bool expectedTemplatePrefetch, bool expectedCliPrefetch)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper);
        using var provider = services.BuildServiceProvider();

        var command = Assert.IsAssignableFrom<BaseCommand>(provider.GetRequiredService(commandType));

        await AssertPrefetchingAsync(provider, command, command.Name, expectedTemplatePrefetch, expectedCliPrefetch);
    }

    [Theory]
    [InlineData(typeof(UpdateCommand), true)]
    [InlineData(typeof(AddCommand), false)]
    public async Task DisabledUpdateNotificationsOnlyPrefetchRequiredCliPackageMetadata(Type commandType, bool expectedCliPrefetch)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper);
        using var provider = services.BuildServiceProvider();

        var command = Assert.IsAssignableFrom<BaseCommand>(provider.GetRequiredService(commandType));

        await AssertPrefetchingAsync(
            provider,
            command,
            command.Name,
            expectedTemplatePrefetch: false,
            expectedCliPrefetch,
            updateNotificationsEnabled: false);
    }

    [Fact]
    public async Task GeneratedTemplateCommandStartsBothPackageMetadataPrefetches()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper);
        using var provider = services.BuildServiceProvider();

        var newCommand = provider.GetRequiredService<NewCommand>();
        var templateCommand = Assert.IsType<TemplateCommand>(newCommand.Subcommands.First());

        await AssertPrefetchingAsync(provider, templateCommand, templateCommand.Name, expectedTemplatePrefetch: true, expectedCliPrefetch: true);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NewSourceOverrideSkipsTemplatePackageMetadataPrefetching(bool useTemplateSubcommand)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper);
        using var provider = services.BuildServiceProvider();

        var newCommand = provider.GetRequiredService<NewCommand>();
        BaseCommand command = useTemplateSubcommand
            ? Assert.IsType<TemplateCommand>(newCommand.Subcommands.First())
            : newCommand;
        var commandLine = useTemplateSubcommand
            ? $"new {command.Name} --source source-feed"
            : "new --source source-feed";
        var parseResult = newCommand.Parse(commandLine);

        await AssertPrefetchingAsync(
            provider,
            command,
            commandLine,
            expectedTemplatePrefetch: false,
            expectedCliPrefetch: true,
            parseResult: parseResult);
    }

    [Fact]
    public async Task ConfiguredNewSourceSkipsTemplateSubcommandPackageMetadataPrefetching()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.ConfigurationCallback += configuration =>
                configuration[AspireConfigFile.NuGetSourceKey] = "https://configured.example/v3/index.json";
        });
        using var provider = services.BuildServiceProvider();

        var newCommand = provider.GetRequiredService<NewCommand>();
        var templateCommand = Assert.IsType<TemplateCommand>(newCommand.Subcommands.First());
        var commandLine = $"new {templateCommand.Name}";

        await AssertPrefetchingAsync(
            provider,
            templateCommand,
            commandLine,
            expectedTemplatePrefetch: false,
            expectedCliPrefetch: true,
            parseResult: newCommand.Parse(commandLine));
    }

    [Fact]
    public async Task DetachedRunStartsNoPackageMetadataPrefetching()
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper);
        using var provider = services.BuildServiceProvider();

        var command = provider.GetRequiredService<RunCommand>();

        await AssertPrefetchingAsync(provider, command, "run --detach", expectedTemplatePrefetch: false, expectedCliPrefetch: false);
    }

    [Theory]
    [InlineData(typeof(StartCommand), "start --format json")]
    [InlineData(typeof(RunCommand), "run --detach --format json")]
    [InlineData(typeof(DoCommand), "do --list-steps --format json")]
    [InlineData(typeof(DoCommand), "do --list-steps --format=json")]
    public async Task JsonOutputStartsNoCliPackageMetadataPrefetching(Type commandType, string commandLine)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper);
        using var provider = services.BuildServiceProvider();

        var command = Assert.IsAssignableFrom<BaseCommand>(provider.GetRequiredService(commandType));

        await AssertPrefetchingAsync(provider, command, commandLine, expectedTemplatePrefetch: false, expectedCliPrefetch: false);
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
        var packageCache = new FakeNuGetPackageCache
        {
            GetTemplatePackagesAsyncCallback = (_, _, _, _) =>
            {
                templateStarted = true;
                return Task.FromResult<IEnumerable<Aspire.Shared.NuGetPackageCli>>([]);
            }
        };
        var channel = PackageChannel.CreateImplicitChannel(packageCache, features, NullLogger.Instance);
        var packagingService = new TestPackagingService
        {
            GetChannelsAsyncCallback = _ => Task.FromResult<IEnumerable<PackageChannel>>([channel])
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
        executionContext.CommandSelected.TrySetResult(new TestCommand(prefetchesTemplatePackages: true, prefetchesCliPackages: true));

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

    private static async Task AssertPrefetchingAsync(
        IServiceProvider provider,
        BaseCommand command,
        string commandLine,
        bool expectedTemplatePrefetch,
        bool expectedCliPrefetch,
        bool updateNotificationsEnabled = true,
        ParseResult? parseResult = null)
    {
        var features = new TestFeatures();
        features.SetFeature(KnownFeatures.UpdateNotificationsEnabled, updateNotificationsEnabled);

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

        var executionContext = provider.GetRequiredService<CliExecutionContext>();
        var prefetcher = CreatePrefetcher(executionContext, features, packagingService, updateNotifier);

        await prefetcher.StartAsync(CancellationToken.None).DefaultTimeout();
        command.SelectForExecution(parseResult ?? command.Parse(commandLine));
        await prefetcher.ExecuteTask!.DefaultTimeout();
        await prefetcher.StopAsync(CancellationToken.None).DefaultTimeout();

        Assert.Equal(expectedTemplatePrefetch, templateStarted);
        Assert.Equal(expectedCliPrefetch, cliStarted);
    }

    private static TestLogger<NuGetPackagePrefetcher> CreateLogger(TestSink sink)
        => new(new TestLoggerFactory(sink, enabled: true));

    private static CliExecutionContext CreateExecutionContext()
    {
        var workingDir = new DirectoryInfo(Environment.CurrentDirectory);
        return TestExecutionContextHelper.CreateExecutionContext(workingDir);
    }
}

internal sealed class TestCommand : BaseCommand
{
    private readonly bool _prefetchesTemplatePackages;
    private readonly bool _prefetchesCliPackages;

    internal override bool PrefetchesTemplatePackageMetadata => _prefetchesTemplatePackages;
    internal override bool RequiresCliPackageMetadata => _prefetchesCliPackages;

    public TestCommand(bool prefetchesTemplatePackages = false, bool prefetchesCliPackages = false)
        : base("test", "Test command", new CommonCommandServices(null!, null!, null!, null!, null!, null!, null!, null!))
    {
        _prefetchesTemplatePackages = prefetchesTemplatePackages;
        _prefetchesCliPackages = prefetchesCliPackages;
    }

    protected override Task<CommandResult> ExecuteAsync(ParseResult parseResult, CancellationToken cancellationToken)
    {
        return Task.FromResult(CommandResult.Success());
    }
}
