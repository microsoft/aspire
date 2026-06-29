// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREINTERACTION001 // IInteractionService is experimental.

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Eventing;
using Aspire.Hosting.Lifecycle;
using Aspire.Hosting.Maui.Annotations;
using Aspire.Hosting.Maui.Utilities;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting.Maui.Lifecycle;

/// <summary>
/// Selects a MAUI Android emulator or iOS simulator before the resource starts.
/// </summary>
internal sealed class MauiEmulatorSelectionEventSubscriber(
    IInteractionService interactionService,
    ResourceNotificationService notificationService,
    ResourceLoggerService loggerService,
    ILogger<MauiEmulatorSelectionEventSubscriber> logger) : IDistributedApplicationEventingSubscriber
{
    private static readonly ResourceStateSnapshot s_canceledState = new(KnownResourceStates.Exited, KnownResourceStateStyles.Warn);

    internal Func<ILogger, CancellationToken, Task<IReadOnlyList<EmulatorOption>>>? AndroidEnumeratorOverride { get; set; }
    internal Func<ILogger, CancellationToken, Task<IReadOnlyList<EmulatorOption>>>? IOSSimulatorEnumeratorOverride { get; set; }
    internal Func<string, ILogger, CancellationToken, Task<string>>? EnsureAndroidEmulatorRunningOverride { get; set; }

    public Task SubscribeAsync(IDistributedApplicationEventing eventing, DistributedApplicationExecutionContext executionContext, CancellationToken cancellationToken)
    {
        if (executionContext.IsRunMode)
        {
            eventing.Subscribe<BeforeResourceStartedEvent>(OnBeforeResourceStartedAsync);
        }

        return Task.CompletedTask;
    }

    private async Task OnBeforeResourceStartedAsync(BeforeResourceStartedEvent @event, CancellationToken cancellationToken)
    {
        if (!@event.Resource.TryGetLastAnnotation<SelectedEmulatorAnnotation>(out var selection))
        {
            return;
        }

        selection.SelectedId = null;

        var resource = @event.Resource;
        var resourceLogger = loggerService.GetLogger(resource);
        var options = await EnumerateTargetsAsync(selection.TargetKind, cancellationToken).ConfigureAwait(false);

        if (options.Count == 0)
        {
            ThrowNoTargetsFound(selection.TargetKind, resourceLogger);
        }

        var selectedId = options.Count == 1
            ? options[0].Id
            : await PromptForTargetAsync(resource, selection.TargetKind, options, resourceLogger, cancellationToken).ConfigureAwait(false);

        var selectedOption = options.First(option => string.Equals(option.Id, selectedId, StringComparison.Ordinal));
        resourceLogger.LogInformation("Selected {DisplayName}.", selectedOption.DisplayName);

        selection.SelectedId = selection.TargetKind switch
        {
            MauiTargetSelectionKind.AndroidEmulator => await EnsureAndroidEmulatorRunningAsync(selectedId, resourceLogger, cancellationToken).ConfigureAwait(false),
            MauiTargetSelectionKind.IOSSimulator => selectedId,
            _ => selectedId
        };
    }

    private async Task<IReadOnlyList<EmulatorOption>> EnumerateTargetsAsync(MauiTargetSelectionKind targetKind, CancellationToken cancellationToken)
    {
        return targetKind switch
        {
            MauiTargetSelectionKind.AndroidEmulator => AndroidEnumeratorOverride is not null
                ? await AndroidEnumeratorOverride(logger, cancellationToken).ConfigureAwait(false)
                : await AndroidEmulatorEnumerator.GetAvailableEmulatorsAsync(logger, cancellationToken).ConfigureAwait(false),

            MauiTargetSelectionKind.IOSSimulator => IOSSimulatorEnumeratorOverride is not null
                ? await IOSSimulatorEnumeratorOverride(logger, cancellationToken).ConfigureAwait(false)
                : await IOSSimulatorEnumerator.GetAvailableSimulatorsAsync(logger, cancellationToken).ConfigureAwait(false),

            _ => []
        };
    }

    private async Task<string> PromptForTargetAsync(
        IResource resource,
        MauiTargetSelectionKind targetKind,
        IReadOnlyList<EmulatorOption> options,
        ILogger resourceLogger,
        CancellationToken cancellationToken)
    {
        if (!interactionService.IsAvailable)
        {
            var targetName = GetTargetName(targetKind);
            var availableTargets = string.Join(", ", options.Select(option => $"{option.DisplayName} ({option.Id})"));
            throw new DistributedApplicationException(
                $"Multiple {targetName} are available, but interactive selection is not available. " +
                $"Specify the target ID explicitly in the AppHost. Available targets: {availableTargets}");
        }

        var (title, message, label) = GetPromptStrings(targetKind);
        var result = await interactionService.PromptInputAsync(
            title,
            message,
            new InteractionInput
            {
                Name = "target",
                InputType = InputType.Choice,
                Label = label,
                Required = true,
                Options = options.Select(option => KeyValuePair.Create(option.Id, option.DisplayName)).ToArray(),
                Placeholder = "Select a target"
            },
            cancellationToken: cancellationToken).ConfigureAwait(false);

        if (result.Canceled)
        {
            resourceLogger.LogInformation("{TargetName} selection was canceled.", GetTargetName(targetKind));
            _ = OverrideCanceledSelectionStateAsync(resource, resourceLogger, CancellationToken.None);
            throw new OperationCanceledException($"{GetTargetName(targetKind)} selection was canceled.", cancellationToken);
        }

        var selectedId = result.Data.Value;
        if (string.IsNullOrWhiteSpace(selectedId) || !options.Any(option => string.Equals(option.Id, selectedId, StringComparison.Ordinal)))
        {
            throw new DistributedApplicationException(
                $"The selected {GetTargetName(targetKind)} value '{selectedId}' is not one of the available targets.");
        }

        return selectedId;
    }

    private async Task<string> EnsureAndroidEmulatorRunningAsync(string avdName, ILogger resourceLogger, CancellationToken cancellationToken)
    {
        return EnsureAndroidEmulatorRunningOverride is not null
            ? await EnsureAndroidEmulatorRunningOverride(avdName, resourceLogger, cancellationToken).ConfigureAwait(false)
            : await AndroidEmulatorEnumerator.EnsureEmulatorRunningAsync(avdName, resourceLogger, cancellationToken).ConfigureAwait(false);
    }

    private static void ThrowNoTargetsFound(MauiTargetSelectionKind targetKind, ILogger resourceLogger)
    {
        var (targetName, instructions) = targetKind switch
        {
            MauiTargetSelectionKind.AndroidEmulator => (
                "Android emulators",
                "Create an Android Virtual Device with Android Studio Device Manager or avdmanager, then start the Aspire resource again. See https://developer.android.com/studio/run/managing-avds."),

            MauiTargetSelectionKind.IOSSimulator => (
                "iOS simulators",
                "Install Xcode and an iOS Simulator runtime, then create a simulator in Xcode's Devices and Simulators window. See https://developer.apple.com/documentation/xcode/installing-additional-simulator-runtimes."),

            _ => ("emulators or simulators", "Install the required platform tooling.")
        };

        resourceLogger.LogError("No {TargetName} found. {Instructions}", targetName, instructions);
        throw new DistributedApplicationException($"No {targetName} found. {instructions}");
    }

    private static (string Title, string Message, string Label) GetPromptStrings(MauiTargetSelectionKind targetKind)
    {
        return targetKind switch
        {
            MauiTargetSelectionKind.AndroidEmulator => (
                "Select Android Emulator",
                "Choose which Android emulator to start and run the application on.",
                "Android emulator"),

            MauiTargetSelectionKind.IOSSimulator => (
                "Select iOS Simulator",
                "Choose which iOS simulator to run the application on.",
                "iOS Simulator"),

            _ => ("Select Target", "Choose which target to use.", "Target")
        };
    }

    private static string GetTargetName(MauiTargetSelectionKind targetKind)
    {
        return targetKind switch
        {
            MauiTargetSelectionKind.AndroidEmulator => "Android emulators",
            MauiTargetSelectionKind.IOSSimulator => "iOS simulators",
            _ => "targets"
        };
    }

    private async Task OverrideCanceledSelectionStateAsync(IResource resource, ILogger resourceLogger, CancellationToken cancellationToken)
    {
        try
        {
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                cts.CancelAfter(TimeSpan.FromSeconds(10));
                await notificationService.WaitForResourceAsync(
                    resource.Name,
                    e => string.Equals(e.Snapshot.State?.Text, KnownResourceStates.FailedToStart, StringComparison.Ordinal),
                    cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // DCP can race with the cancellation path. If FailedToStart is not observed,
                // publish the canceled state anyway so prompt dismissal does not look like a crash.
            }

            await notificationService.PublishUpdateAsync(resource, s =>
            {
                if (s.State?.Text is not null && !string.Equals(s.State.Text, KnownResourceStates.FailedToStart, StringComparison.Ordinal))
                {
                    return s;
                }

                return s with
                {
                    State = s_canceledState,
                    StartTimeStamp = null,
                    StopTimeStamp = null,
                    ExitCode = null
                };
            }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            resourceLogger.LogDebug(ex, "Failed to override state to Exited for canceled target selection on resource '{ResourceName}'.", resource.Name);
        }
    }
}
