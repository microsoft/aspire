// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using System.Net.Sockets;
using System.Text.Json;
using Aspire.Cli.Backchannel;
using Aspire.Cli.DotnetWatch;
using Aspire.Cli.DotNet;
using Aspire.Cli.Interaction;
using Aspire.Cli.Resources;
using Aspire.Cli.Watch;
using Aspire.Hosting;
using Aspire.Shared.DotnetWatch;
using Microsoft.Extensions.Logging;

namespace Aspire.Cli.Projects;

internal sealed partial class DotNetAppHostProject
{
    private async Task<DotnetWatchAppHostLaunch?> PreparePackageWatchAsync(
        AppHostProjectContext context,
        Dictionary<string, string> environment,
        CancellationToken cancellationToken)
    {
        DotnetWatchAppHostLaunch launch;
        try
        {
            launch = await DotnetWatchAppHostLaunch.ResolveAsync(_runner, context.AppHostFile, environment,
                _environment.GetEnvironmentVariable, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException or InvalidOperationException or JsonException or
            KeyNotFoundException or ArgumentException)
        {
            // Service preflight remains package-owned even when this prelaunch operation is
            // unavailable. In particular, an invalid explicit tool override must not fall back
            // to a default payload in either the CLI or the service coordinator.
            _interactionService.DisplayMessage(KnownEmojis.PoliceCarLight,
                string.Format(CultureInfo.CurrentCulture, RunCommandStrings.WatchAppHostFallback, exception.Message));
            _logger.LogDebug(exception, "The package-owned AppHost watch descriptor could not be resolved.");
            return null;
        }

        if (launch.HostIncompatibility is null &&
            !TryApplyProjectLaunchSettings(
                context.AppHostFile,
                new Dictionary<string, string>(),
                [],
                noLaunchProfile: false,
                context.LaunchProfile ?? GetEffectiveEnvironmentValue(environment, "DOTNET_LAUNCH_PROFILE"),
                hasExplicitApplicationArgs: context.UnmatchedTokens.Length > 0,
                hasRunArguments: false))
        {
            launch = launch with { HostIncompatibility = RunCommandStrings.WatchAppHostProfileUnsupported };
        }
        if (context.WatchGeneration is null && launch.HostIncompatibility is null)
        {
            launch = launch with { HostIncompatibility = RunCommandStrings.WatchAppHostGenerationRequired };
        }
        if (launch.HostIncompatibility is { } reason)
        {
            _interactionService.DisplayMessage(KnownEmojis.PoliceCarLight,
                string.Format(CultureInfo.CurrentCulture, RunCommandStrings.WatchAppHostFallback, reason));
        }

        return launch;
    }

    private async Task<int?> RunPackageWatchAsync(
        AppHostProjectContext context,
        DotnetWatchAppHostLaunch launch,
        DotnetWatchAppHostGeneration generation,
        Dictionary<string, string> environment,
        TaskCompletionSource<IAppHostCliBackchannel> backchannelCompletionSource,
        ProcessInvocationOptions options,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            generation.ApplyEnvironment(environment);
        }
        catch (Exception exception) when (exception is IOException or SocketException or UnauthorizedAccessException or ArgumentException)
        {
            // Failure to create the private channel is still prelaunch: no wrapper or model
            // exists, so preserve ordinary AppHost launch and independent service coverage.
            DotnetWatchRunEnvironment.ClearHostGeneration(environment);
            _interactionService.DisplayMessage(KnownEmojis.PoliceCarLight,
                string.Format(CultureInfo.CurrentCulture, RunCommandStrings.WatchAppHostFallback, exception.Message));
            return null;
        }

        environment[KnownConfigNames.SuppressCliRunHook] = "true";
        environment["DOTNET_WATCH_SUPPRESS_LAUNCH_BROWSER"] = "true";
        var watchOptions = options.Clone();
        watchOptions.DotNetExecutablePath = launch.DotnetPath;
        watchOptions.DisableExtensionLaunch = true;
        watchOptions.BackchannelConnectionReady = generation.HostStarted;
        watchOptions.GracefulShutdownTarget = generation.GetGracefulShutdownTarget;
        var arguments = launch.CreateArguments(context.AppHostFile, watchOptions, context.UnmatchedTokens);
        watchOptions.AppHostArgumentStartIndex = context.UnmatchedTokens.Length > 0
            ? arguments.Length - context.UnmatchedTokens.Length
            : null;
        using var stopAttempt = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var stopStartupWait = CancellationTokenSource.CreateLinkedTokenSource(stopAttempt.Token);
        Task? startupDisconnect = null;
        var childExit = generation.RequiresLauncherMonitoring
            ? WaitForAnnouncedHostExitAsync(generation.HostStarted, stopStartupWait.Token)
            : null;
        var watchBackchannel = new TaskCompletionSource<IAppHostCliBackchannel>(TaskCreationOptions.RunContinuationsAsynchronously);
        context.OutputCollector?.MarkWatcherOutput();
        var run = _runner.RunAppHostCommandAsync(
            context.AppHostFile,
            launch.DotnetPath,
            context.AppHostFile.Directory!,
            arguments,
            environment,
            watchBackchannel,
            watchOptions,
            stopAttempt.Token);

        try
        {
            await Task.WhenAny(childExit is null ? [run, watchBackchannel.Task] : [run, watchBackchannel.Task, childExit])
                .ConfigureAwait(false);
            if (childExit?.IsCompletedSuccessfully == true)
            {
                return await FailExitedChildStartupAsync().ConfigureAwait(false);
            }
            if (watchBackchannel.Task.IsCompletedSuccessfully)
            {
                var backchannel = await watchBackchannel.Task.ConfigureAwait(false);
                var announcedBeforeConnection = generation.HostStarted.IsCompletedSuccessfully;
                startupDisconnect = backchannel.WaitForDisconnectAsync(stopStartupWait.Token);
                // RunCommand's existing startup budget and cancellation bound this wait. A
                // healthy backchannel alone cannot establish the initial real process identity.
                await Task.WhenAny(childExit is null
                    ? [generation.HostStarted, startupDisconnect, run]
                    : [generation.HostStarted, startupDisconnect, run, childExit]).WaitAsync(cancellationToken).ConfigureAwait(false);
                if (childExit?.IsCompletedSuccessfully == true)
                {
                    return await FailExitedChildStartupAsync().ConfigureAwait(false);
                }
                if (!announcedBeforeConnection || startupDisconnect.IsCompleted)
                {
                    // The real runner gates the connection on HostStarted. Reject any earlier
                    // candidate rather than authenticate its stale connection with a subsequent
                    // rude-edit child's first announcement.
                    backchannelCompletionSource.TrySetException(new InvalidOperationException(RunCommandStrings.WatchAppHostIdentityRequired));
                    await stopAttempt.CancelAsync().ConfigureAwait(false);
                    return CliExitCodes.FailedToDotnetRunAppHost;
                }
                if (!generation.HostStarted.IsCompletedSuccessfully)
                {
                    return await run.ConfigureAwait(false);
                }
                cancellationToken.ThrowIfCancellationRequested();
                generation.MarkBackchannelEstablished();
                backchannelCompletionSource.TrySetResult(backchannel);
                return await run.ConfigureAwait(false);
            }

            if (watchBackchannel.Task.IsFaulted)
            {
                backchannelCompletionSource.TrySetException(watchBackchannel.Task.Exception!.InnerExceptions);
                // Backchannel negotiation failure after launch is an AppHost failure, not
                // permission to execute startup code a second time through ordinary launch.
                await stopAttempt.CancelAsync().ConfigureAwait(false);
                return CliExitCodes.FailedToDotnetRunAppHost;
            }

            return await run.ConfigureAwait(false);
        }
        catch (OperationCanceledException exception) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(exception.Message, exception, cancellationToken);
        }
        finally
        {
            try
            {
                await stopStartupWait.CancelAsync().ConfigureAwait(false);
                if (startupDisconnect is not null)
                {
                    try
                    {
                        await startupDisconnect.ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (stopStartupWait.IsCancellationRequested)
                    {
                    }
                }
                if (childExit is not null)
                {
                    try
                    {
                        await childExit.ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (stopStartupWait.IsCancellationRequested)
                    {
                    }
                }
            }
            finally
            {
                await stopAttempt.CancelAsync().ConfigureAwait(false);
                try
                {
                    await run.ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (stopAttempt.IsCancellationRequested)
                {
                }
                _ = watchBackchannel.Task.Exception;
            }
        }

        async Task<int> FailExitedChildStartupAsync()
        {
            var message = string.Format(
                CultureInfo.CurrentCulture,
                RunCommandStrings.WatchAppHostExitedDuringStartup,
                _fileLoggerProvider.LogFilePath);
            _logger.LogError("{Message}", message);
            backchannelCompletionSource.TrySetException(new InvalidOperationException(message));
            await stopAttempt.CancelAsync().ConfigureAwait(false);
            return CliExitCodes.FailedToDotnetRunAppHost;
        }
    }

    internal static async Task WaitForAnnouncedHostExitAsync(
        Task<string> hostStarted,
        CancellationToken cancellationToken)
    {
        var instance = await hostStarted.WaitAsync(cancellationToken).ConfigureAwait(false);
        if (!DotnetWatchAppHostProtocol.TryParseInstance(instance, out var processId, out var started))
        {
            throw new InvalidDataException("The watched AppHost announced an invalid process identity.");
        }

        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(100));
        while (ProcessStartTimeHelper.IsProcessRunning(processId, started) &&
            await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
        {
        }
    }
}
