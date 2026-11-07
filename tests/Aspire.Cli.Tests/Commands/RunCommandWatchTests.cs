// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Commands;
using Aspire.Cli.Projects;
using Aspire.Cli.Resources;
using Aspire.Cli.Tests.TestServices;
using Aspire.Cli.Tests.Utils;
using Aspire.Hosting;
using Aspire.Shared.DotnetWatch;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Aspire.Cli.Tests.Commands;

public class RunCommandWatchTests(ITestOutputHelper outputHelper)
{
    [Theory]
    [InlineData("run", true, true, 1)]
    [InlineData("run", false, false, 1)]
    [InlineData("run --watch", false, true, 0)]
    [InlineData("run --watch=false", true, false, 0)]
    [InlineData("run -- --watch=false", true, true, 1)]
    public async Task CombinedWatchIsResolvedOnceAndOnlyEffectiveWatchCreatesAGeneration(
        string commandLine, bool defaultWatch, bool expectedWatch, int expectedDefaultReads)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        using var cancellation = new CancellationTokenSource();
        var started = new TaskCompletionSource<AppHostProjectContext>(TaskCreationOptions.RunContinuationsAsynchronously);
        var defaultReads = 0;
        var features = new TestFeatures
        {
            IsFeatureEnabledCallback = (name, defaultValue) =>
            {
                if (name != KnownFeatures.DefaultWatchEnabled)
                {
                    return defaultValue;
                }
                Interlocked.Increment(ref defaultReads);
                return defaultWatch;
            }
        };
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.ProjectLocatorFactory = _ => new TestProjectLocator();
            options.FeatureFlagsFactory = _ => features;
            options.AppHostProjectFactory = _ => new TestAppHostProjectFactory
            {
                RunAsyncCallback = async (context, token) =>
                {
                    started.TrySetResult(context);
                    context.BuildCompletionSource!.TrySetResult(true);
                    context.BackchannelCompletionSource!.TrySetResult(new TestAppHostBackchannel());
                    await Task.Delay(Timeout.InfiniteTimeSpan, token);
                    return 0;
                }
            };
        });
        using var provider = services.BuildServiceProvider();
        var run = provider.GetRequiredService<RootCommand>().Parse(commandLine).InvokeAsync(cancellationToken: cancellation.Token);
        var context = await started.Task.DefaultTimeout();
        await cancellation.CancelAsync();
        var exitCode = await run.DefaultTimeout();

        Assert.Equal(CliExitCodes.Success, exitCode);
        Assert.Equal(expectedWatch, context.Watch);
        Assert.Equal(expectedWatch, context.WatchGeneration is not null);
        Assert.Equal(expectedDefaultReads, defaultReads);
        Assert.False(context.WatchGeneration?.IsEnabled ?? false);
        if (commandLine.Contains(" -- --watch", StringComparison.Ordinal))
        {
            Assert.Equal(["--watch=false"], context.UnmatchedTokens);
            Assert.False(context.WatchExplicitlySet);
        }
    }

    [Theory]
    [InlineData("run --watch", false)]
    [InlineData("run", true)]
    public async Task AspireTerminalWithoutAnIdeSessionKeepsCombinedWatchCliOwned(string commandLine, bool defaultWatch)
    {
        using var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        using var cancellation = new CancellationTokenSource();
        var started = new TaskCompletionSource<AppHostProjectContext>(TaskCreationOptions.RunContinuationsAsynchronously);
        var delegated = false;
        var services = CliTestHelper.CreateServiceCollection(workspace, outputHelper, options =>
        {
            options.ProjectLocatorFactory = _ => new TestProjectLocator();
            options.FeatureFlagsFactory = _ => new TestFeatures().SetFeature(KnownFeatures.DefaultWatchEnabled, defaultWatch);
            options.ExtensionBackchannelFactory = _ => new TestExtensionBackchannel();
            options.InteractionServiceFactory = sp => new TestExtensionInteractionService(sp)
            {
                StartDebugSessionCallback = (_, _, _, _) => delegated = true
            };
            options.AppHostProjectFactory = _ => new TestAppHostProjectFactory
            {
                RunAsyncCallback = async (context, token) =>
                {
                    started.TrySetResult(context);
                    context.BuildCompletionSource!.TrySetResult(true);
                    context.BackchannelCompletionSource!.TrySetResult(new TestAppHostBackchannel());
                    await Task.Delay(Timeout.InfiniteTimeSpan, token);
                    return 0;
                }
            };
        });
        using var provider = services.BuildServiceProvider();
        var run = provider.GetRequiredService<RootCommand>().Parse(commandLine).InvokeAsync(cancellationToken: cancellation.Token);
        var context = await started.Task.DefaultTimeout();
        await cancellation.CancelAsync();
        var exitCode = await run.DefaultTimeout();

        Assert.Equal(CliExitCodes.Success, exitCode);
        Assert.True(context.Watch);
        Assert.NotNull(context.WatchGeneration);
        Assert.False(delegated);
    }

    [Theory]
    [InlineData(null, null, null, null, false)]
    [InlineData(null, "localhost:12345", null, null, true)]
    [InlineData(null, null, "{}", null, true)]
    [InlineData(null, null, null, "NoDebug", true)]
    [InlineData("cli", "localhost:12345", "{}", "NoDebug", false)]
    public void UpstreamExecutionServiceSettingsAreNotMistakenForRealIdeOwnership(
        string? owner, string? port, string? info, string? mode, bool expectedIde)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            [DotnetWatchOwnership.OwnerEnvironmentVariable] = owner,
            [KnownConfigNames.DebugSessionPort] = port,
            [KnownConfigNames.DebugSessionInfo] = info,
            [KnownConfigNames.DebugSessionRunMode] = mode
        }).Build();

        Assert.Equal(expectedIde, RunCommand.IsIdeOwnedRun(configuration));
        configuration[KnownConfigNames.ExtensionDebugSessionId] = "real-ide-session";
        Assert.True(RunCommand.IsIdeOwnedRun(configuration));
    }

    [Fact]
    public void LocalizedHelpDescribesAllCombinedWatchPathsAndExplicitFalse()
    {
        Assert.Contains("C# AppHosts", RunCommandStrings.WatchOptionDescription, StringComparison.Ordinal);
        Assert.Contains("reconstruct", RunCommandStrings.WatchOptionDescription, StringComparison.Ordinal);
        Assert.Contains("polyglot", RunCommandStrings.WatchOptionDescription, StringComparison.Ordinal);
        Assert.Contains("--watch false", RunCommandStrings.WatchOptionDescription, StringComparison.Ordinal);
        Assert.Contains("ResolveAspireDotnetWatch", RunCommandStrings.WatchBootstrapUnavailable, StringComparison.Ordinal);
    }
}
