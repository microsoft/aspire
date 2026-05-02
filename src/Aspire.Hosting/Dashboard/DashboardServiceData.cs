// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Runtime.CompilerServices;
using System.Text.Json;
using Aspire.DashboardService.Proto.V1;
using Aspire.Hosting.ApplicationModel;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting.Dashboard;

#pragma warning disable ASPIREINTERACTION001 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.

/// <summary>
/// Models the state for <see cref="DashboardService"/>, as that service is constructed
/// for each gRPC request. This long-lived object holds state across requests.
/// </summary>
internal sealed class DashboardServiceData : IDisposable
{
    private readonly CancellationTokenSource _cts = new();
    private readonly ResourcePublisher _resourcePublisher;
    private readonly ResourceCommandService _resourceCommandService;
    private readonly InteractionService _interactionService;
    private readonly ResourceLoggerService _resourceLoggerService;

    public DashboardServiceData(
        ResourceNotificationService resourceNotificationService,
        ResourceLoggerService resourceLoggerService,
        ILogger<DashboardServiceData> logger,
        ResourceCommandService resourceCommandService,
        InteractionService interactionService)
    {
        _resourceLoggerService = resourceLoggerService;
        _resourcePublisher = new ResourcePublisher(_cts.Token);
        _resourceCommandService = resourceCommandService;
        _interactionService = interactionService;
        var cancellationToken = _cts.Token;

        Task.Run(async () =>
        {
            static GenericResourceSnapshot CreateResourceSnapshot(IResource resource, string resourceId, DateTime creationTimestamp, CustomResourceSnapshot snapshot)
            {
                return new GenericResourceSnapshot(snapshot)
                {
                    Uid = resourceId,
                    CreationTimeStamp = snapshot.CreationTimeStamp ?? creationTimestamp,
                    StartTimeStamp = snapshot.StartTimeStamp,
                    StopTimeStamp = snapshot.StopTimeStamp,
                    Name = resourceId,
                    DisplayName = resource.Name,
                    Urls = snapshot.Urls,
                    Volumes = snapshot.Volumes,
                    Environment = snapshot.EnvironmentVariables,
                    Relationships = snapshot.Relationships,
                    ExitCode = snapshot.ExitCode,
                    State = snapshot.State?.Text,
                    StateStyle = snapshot.State?.Style,
                    HealthReports = snapshot.HealthReports,
                    Commands = snapshot.Commands,
                    IsHidden = snapshot.IsHidden,
                    SupportsDetailedTelemetry = snapshot.SupportsDetailedTelemetry,
                    IconName = snapshot.IconName,
                    IconVariant = snapshot.IconVariant
                };
            }

            var timestamp = DateTime.UtcNow;

            await foreach (var @event in resourceNotificationService.WatchAsync().WithCancellation(cancellationToken).ConfigureAwait(false))
            {
                try
                {
                    var snapshot = CreateResourceSnapshot(@event.Resource, @event.ResourceId, timestamp, @event.Snapshot);

                    if (logger.IsEnabled(LogLevel.Debug))
                    {
                        logger.LogDebug("Updating resource snapshot for {Name}/{DisplayName}: {State}", snapshot.Name, snapshot.DisplayName, snapshot.State);
                    }

                    await _resourcePublisher.IntegrateAsync(@event.Resource, snapshot, ResourceSnapshotChangeType.Upsert)
                            .ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogError(ex, "Error updating resource snapshot for {Name}", @event.Resource.Name);
                }
            }
        },
        cancellationToken);
    }

    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
    }

    internal async Task<(ExecuteCommandResultType result, string? message, ApplicationModel.CommandResultData? value)> ExecuteCommandAsync(string resourceId, string type, JsonElement? arguments, CancellationToken cancellationToken)
    {
        try
        {
            var result = await _resourceCommandService.ExecuteCommandAsync(resourceId, type, arguments, cancellationToken).ConfigureAwait(false);
            if (result.Canceled)
            {
                return (ExecuteCommandResultType.Canceled, result.Message, null);
            }
            return (result.Success ? ExecuteCommandResultType.Success : ExecuteCommandResultType.Failure, result.Message, result.Data);
        }
        catch
        {
            // Note: Exception is already logged in the command executor.
            return (ExecuteCommandResultType.Failure, "Unhandled exception thrown while executing command.", null);
        }
    }

    internal IAsyncEnumerable<Interaction> SubscribeInteractionUpdates()
    {
        return _interactionService.SubscribeInteractionUpdates();
    }

    internal ResourceSnapshotSubscription SubscribeResources()
    {
        return _resourcePublisher.Subscribe();
    }

    internal IAsyncEnumerable<IReadOnlyList<LogLine>>? SubscribeConsoleLogs(string resourceName)
    {
        var sequence = _resourceLoggerService.WatchAsync(resourceName);

        return sequence is null ? null : Enumerate();

        async IAsyncEnumerable<IReadOnlyList<LogLine>> Enumerate([EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _cts.Token);

            await foreach (var item in sequence.WithCancellation(linked.Token).ConfigureAwait(false))
            {
                yield return item;
            }
        }
    }

    internal IAsyncEnumerable<IReadOnlyList<LogLine>>? GetConsoleLogs(string resourceName)
    {
        var sequence = _resourceLoggerService.GetAllAsync(resourceName);

        return sequence is null ? null : Enumerate();

        async IAsyncEnumerable<IReadOnlyList<LogLine>> Enumerate([EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _cts.Token);

            await foreach (var item in sequence.WithCancellation(linked.Token).ConfigureAwait(false))
            {
                yield return item;
            }
        }
    }

    internal async Task SendInteractionRequestAsync(WatchInteractionsRequestUpdate request, CancellationToken cancellationToken)
    {
        await _interactionService.ProcessInteractionFromClientAsync(
            request.InteractionId,
            (interaction, serviceProvider, logger) =>
            {
                switch (request.KindCase)
                {
                    case WatchInteractionsRequestUpdate.KindOneofCase.MessageBox:
                        return new InteractionCompletionState { Complete = true, State = request.MessageBox.Result };
                    case WatchInteractionsRequestUpdate.KindOneofCase.Notification:
                        return new InteractionCompletionState { Complete = true, State = request.Notification.Result };
                    case WatchInteractionsRequestUpdate.KindOneofCase.InputsDialog:
                        var inputsInfo = (Interaction.InputsInteractionInfo)interaction.InteractionInfo;
                        var options = (InputsDialogInteractionOptions)interaction.Options;

                        ProcessInputs(
                            serviceProvider,
                            logger,
                            inputsInfo,
                            request.InputsDialog.InputItems.Select(i => new InputDto(i.Name, i.Value, DashboardService.MapInputType(i.InputType))).ToList(),
                            request.ResponseUpdate,
                            interaction.CancellationToken);

                        // If the interaction was sent to the server because an input changed, don't try to complete the interaction.
                        return new InteractionCompletionState { Complete = !request.ResponseUpdate, State = inputsInfo.Inputs };
                    default:
                        return new InteractionCompletionState { Complete = true };
                }
            },
            cancellationToken).ConfigureAwait(false);
    }

    public record InputDto(string Name, string Value, InputType InputType);

    public static void ProcessInputs(IServiceProvider serviceProvider, ILogger logger, Interaction.InputsInteractionInfo inputsInfo, List<InputDto> inputDtos, bool dependencyChange, CancellationToken cancellationToken)
    {
        var choiceInteractionsToUpdate = new HashSet<InteractionInput>();

        for (var i = 0; i < inputDtos.Count; i++)
        {
            var requestInput = inputDtos[i];
            if (!inputsInfo.Inputs.TryGetByName(requestInput.Name, out var modelInput))
            {
                continue;
            }

            var incomingValue = requestInput.Value;

            // Ensure checkbox value is either true or false.
            if (requestInput.InputType == InputType.Boolean)
            {
                incomingValue = (bool.TryParse(incomingValue, out var b) && b) ? "true" : "false";
            }

            if (!string.Equals(modelInput.Value ?? string.Empty, incomingValue ?? string.Empty))
            {
                modelInput.Value = incomingValue;

                // If we're processing updates because of a dependency change, check to see if this input is depended on.
                if (dependencyChange)
                {
                    var dependentInputs = inputsInfo.Inputs.Where(
                        i => i.DynamicLoading is { } dynamic &&
                        (dynamic.DependsOnInputs?.Any(d => string.Equals(modelInput.Name, d, StringComparisons.InteractionInputName)) ?? false));

                    foreach (var dependentInput in dependentInputs)
                    {
                        choiceInteractionsToUpdate.Add(dependentInput);
                    }
                }
            }
        }

        // Refresh options for choice inputs that depend on other inputs.
        foreach (var inputToUpdate in choiceInteractionsToUpdate)
        {
            var options = new QueueLoadOptions(logger, cancellationToken, inputToUpdate, inputsInfo.Inputs, serviceProvider);
            inputToUpdate.DynamicLoadingState!.QueueLoad(options);
        }
    }
}

internal enum ExecuteCommandResultType
{
    Success,
    Failure,
    Canceled
}

#pragma warning restore ASPIREINTERACTION001 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.
