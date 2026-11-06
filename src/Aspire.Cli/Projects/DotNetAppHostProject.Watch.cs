// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using System.Text.Json;
using Aspire.Cli.Backchannel;
using Aspire.Cli.DotNet;
using Aspire.Cli.Interaction;
using Aspire.Cli.Resources;
using Aspire.Cli.Utils;
using Aspire.Cli.Watch;
using Aspire.Hosting;
using Microsoft.Extensions.Logging;

namespace Aspire.Cli.Projects;

internal sealed partial class DotNetAppHostProject
{
    private async Task<DotnetWatchAppHostLaunch?> PreparePackageWatchAsync(
        AppHostProjectContext context,
        Dictionary<string, string> environment,
        CancellationToken cancellationToken)
    {
        DotnetWatchLaunchEnvironment.Apply(environment, CliSelfInvocation.Current());
        DotnetWatchAppHostLaunch launch;
        try
        {
            launch = await DotnetWatchAppHostLaunch.ResolveAsync(_runner, context.AppHostFile, environment, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException or InvalidOperationException or JsonException or KeyNotFoundException or ArgumentException)
        {
            // Do not retry acquisition in Hosting after an explicit prelaunch attempt failed.
            // A cancellation is deliberately not a classified watch failure.
            DotnetWatchLaunchEnvironment.Disable(environment);
            _interactionService.DisplayMessage(KnownEmojis.PoliceCarLight,
                string.Format(CultureInfo.CurrentCulture, RunCommandStrings.WatchUnavailable, exception.Message));
            _logger.LogDebug(exception, "Experimental watch bootstrap failed.");
            return null;
        }

        launch.ApplyEnvironment(environment);
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
        var hostIncompatibility = launch.HostIncompatibility ??
            (context.WatchGeneration is null ? RunCommandStrings.WatchAppHostGenerationRequired : null);
        if (hostIncompatibility is not null)
        {
            _interactionService.DisplayMessage(KnownEmojis.PoliceCarLight,
                string.Format(CultureInfo.CurrentCulture, RunCommandStrings.WatchAppHostFallback, hostIncompatibility));
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
        generation.ApplyEnvironment(environment);
        environment[KnownConfigNames.SuppressCliRunHook] = "true";
        environment["DOTNET_WATCH_SUPPRESS_LAUNCH_BROWSER"] = "true";

        var watchOptions = options.Clone();
        watchOptions.DotNetExecutablePath = launch.DotnetPath;
        watchOptions.DisableExtensionLaunch = true;
        using var stopAttempt = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var watchBackchannel = new TaskCompletionSource<IAppHostCliBackchannel>(TaskCreationOptions.RunContinuationsAsynchronously);
        // The runner's connection poll can report a failed wrapper after its process task has
        // settled. Observe that losing attempt even when an ordinary fallback is already running.
        _ = watchBackchannel.Task.ContinueWith(static task => _ = task.Exception, CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        var run = _runner.RunAppHostCommandAsync(
            context.AppHostFile,
            launch.DotnetPath,
            context.AppHostFile.Directory!,
            launch.CreateArguments(context.AppHostFile, watchOptions, context.UnmatchedTokens),
            environment,
            watchBackchannel,
            watchOptions,
            stopAttempt.Token);

        try
        {
            await Task.WhenAny(run, watchBackchannel.Task).ConfigureAwait(false);
            if (watchBackchannel.Task.IsCompletedSuccessfully)
            {
                generation.MarkBackchannelEstablished();
                backchannelCompletionSource.TrySetResult(await watchBackchannel.Task.ConfigureAwait(false));
                return await run.ConfigureAwait(false);
            }

            if (watchBackchannel.Task.IsFaulted && !run.IsCompleted)
            {
                // A failed backchannel negotiation is an AppHost failure, not permission to
                // launch the model a second time through the ordinary path.
                return await FailWatchBackchannelAsync().ConfigureAwait(false);
            }

            var exitCode = await run.ConfigureAwait(false);
            if (exitCode is -1 or 255 &&
                !watchBackchannel.Task.IsCompletedSuccessfully &&
                !generation.HostStarted.IsCompletedSuccessfully)
            {
                // The pinned entrypoint returns -1 for command/initialization exceptions, while
                // source compilation errors remain in its watch loop. The CLI pre-build already
                // succeeded, and neither a host startup notification nor a backchannel has
                // been observed. Never downgrade a wrapper that already announced an AppHost.
                _interactionService.DisplayMessage(KnownEmojis.PoliceCarLight,
                    string.Format(CultureInfo.CurrentCulture, RunCommandStrings.WatchAppHostFallback, RunCommandStrings.WatchAppHostInitializationFailed));
                generation.Disable();
                return null;
            }

            if (watchBackchannel.Task.IsFaulted)
            {
                backchannelCompletionSource.TrySetException(watchBackchannel.Task.Exception!.InnerExceptions);
            }

            return exitCode;
        }
        catch (OperationCanceledException exception) when (cancellationToken.IsCancellationRequested)
        {
            // The process uses the attempt's linked token, but RunCommand recognizes cancellation
            // using the project's caller token. Preserve that identity during generation teardown
            // instead of reporting an expected replacement as an unexpected TaskCanceledException.
            throw new OperationCanceledException(exception.Message, exception, cancellationToken);
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

        async Task<int> FailWatchBackchannelAsync()
        {
            try
            {
                await watchBackchannel.Task.ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                backchannelCompletionSource.TrySetException(exception);
            }

            return CliExitCodes.FailedToDotnetRunAppHost;
        }
    }
}
