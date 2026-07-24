// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Threading.Channels;
using Aspire.DashboardService.Proto.V1;
using Grpc.Core;

namespace Aspire.Dashboard.Backend;

internal interface IDashboardInteractionService
{
    DashboardInteraction[] GetInteractions();

    ValueTask<bool> RespondAsync(
        DashboardRespondInteractionRequest request,
        CancellationToken cancellationToken);
}

internal sealed class DashboardInteractionService(
    IDashboardResourceServiceConnection resourceServiceConnection,
    ILogger<DashboardInteractionService> logger) : BackgroundService, IDashboardInteractionService
{
    private const int MaximumPendingInteractions = 256;
    private const int ResponseBufferCapacity = 64;

    private readonly object _lock = new();
    private readonly Dictionary<int, WatchInteractionsResponseUpdate> _interactions = [];
    private readonly List<int> _interactionOrder = [];
    private readonly Dictionary<int, DashboardPendingInteractionResponse> _terminalResponsesInFlight = [];
    private readonly SemaphoreSlim _responseOrder = new(1, 1);
    private readonly Channel<DashboardPendingInteractionResponse> _responses =
        Channel.CreateBounded<DashboardPendingInteractionResponse>(new BoundedChannelOptions(ResponseBufferCapacity)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait
        });

    public DashboardInteraction[] GetInteractions()
    {
        lock (_lock)
        {
            return _interactionOrder
                .Select(interactionId => MapInteraction(_interactions[interactionId]))
                .ToArray();
        }
    }

    public async ValueTask<bool> RespondAsync(
        DashboardRespondInteractionRequest request,
        CancellationToken cancellationToken)
    {
        // Serialize browser responses before they enter the bounded transport queue. Dynamic
        // input updates must reach the AppHost before a subsequent submit, matching the UI's
        // update chain even when requests arrive on different HTTP/2 streams.
        await _responseOrder.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            WatchInteractionsResponseUpdate interaction;
            var originalOrder = -1;
            lock (_lock)
            {
                if (!_interactions.TryGetValue(request.InteractionId, out var pendingInteraction))
                {
                    return false;
                }

                interaction = pendingInteraction.Clone();
                if (!string.Equals(request.Action, "update", StringComparison.Ordinal))
                {
                    originalOrder = _interactionOrder.IndexOf(request.InteractionId);
                    RemoveInteraction(request.InteractionId);
                }
            }

            DashboardPendingInteractionResponse? response = null;
            response = new DashboardPendingInteractionResponse(
                BuildRequest(interaction, request.Action, request.Values ?? []),
                onDelivered: () => CompleteTerminalResponse(response),
                onFailed: _ => RestoreTerminalResponse(response, interaction, originalOrder));

            if (originalOrder >= 0)
            {
                lock (_lock)
                {
                    _terminalResponsesInFlight[request.InteractionId] = response;
                }
            }

            try
            {
                await _responses.Writer.WriteAsync(response, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                response.MarkFailed(new DashboardResourceServiceUnavailableException(
                    "The interaction response could not be queued for the AppHost resource service."));
                throw;
            }

            return true;
        }
        finally
        {
            _responseOrder.Release();
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!resourceServiceConnection.IsConfigured)
        {
            logger.LogDebug(
                "Interaction streaming is disabled. {UnavailableMessage}",
                resourceServiceConnection.UnavailableMessage);
            return;
        }

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await resourceServiceConnection.RunInteractionSessionAsync(
                        _responses.Reader,
                        update =>
                        {
                            ApplyUpdate(update);
                            return ValueTask.CompletedTask;
                        },
                        stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    return;
                }
                catch (RpcException ex) when (ex.StatusCode is StatusCode.Unimplemented)
                {
                    logger.LogWarning("The AppHost resource service does not support interactions.");
                    return;
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "The AppHost interaction stream disconnected; retrying.");
                }

                await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _responses.Writer.TryComplete();
            while (_responses.Reader.TryRead(out var response))
            {
                response.MarkFailed(new OperationCanceledException(
                    "The dashboard interaction service stopped before delivering the response.",
                    stoppingToken));
            }
        }
    }

    internal void ApplyUpdate(WatchInteractionsResponseUpdate interaction)
    {
        lock (_lock)
        {
            // Any authoritative AppHost update supersedes a locally removed interaction.
            // A later transport failure must not resurrect a completed or revised prompt.
            _terminalResponsesInFlight.Remove(interaction.InteractionId);

            if (interaction.KindCase is WatchInteractionsResponseUpdate.KindOneofCase.Complete)
            {
                RemoveInteraction(interaction.InteractionId);
                return;
            }

            AddOrUpdateInteraction(interaction);
        }
    }

    private void AddOrUpdateInteraction(WatchInteractionsResponseUpdate interaction)
    {
        if (!_interactions.ContainsKey(interaction.InteractionId))
        {
            if (_interactions.Count >= MaximumPendingInteractions)
            {
                throw new InvalidOperationException(
                    $"The AppHost supplied more than {MaximumPendingInteractions} pending interactions.");
            }

            _interactionOrder.Add(interaction.InteractionId);
        }

        _interactions[interaction.InteractionId] = interaction.Clone();
    }

    private void RemoveInteraction(int interactionId)
    {
        _interactions.Remove(interactionId);
        _interactionOrder.Remove(interactionId);
    }

    private void CompleteTerminalResponse(DashboardPendingInteractionResponse? response)
    {
        if (response is null)
        {
            return;
        }

        lock (_lock)
        {
            if (_terminalResponsesInFlight.GetValueOrDefault(response.Request.InteractionId) == response)
            {
                _terminalResponsesInFlight.Remove(response.Request.InteractionId);
            }
        }
    }

    private void RestoreTerminalResponse(
        DashboardPendingInteractionResponse? response,
        WatchInteractionsResponseUpdate interaction,
        int originalOrder)
    {
        if (response is null || originalOrder < 0)
        {
            return;
        }

        lock (_lock)
        {
            if (_terminalResponsesInFlight.GetValueOrDefault(interaction.InteractionId) != response)
            {
                return;
            }

            _terminalResponsesInFlight.Remove(interaction.InteractionId);
            if (_interactions.ContainsKey(interaction.InteractionId))
            {
                return;
            }

            _interactions[interaction.InteractionId] = interaction;
            _interactionOrder.Insert(Math.Min(originalOrder, _interactionOrder.Count), interaction.InteractionId);
        }
    }

    private static WatchInteractionsRequestUpdate BuildRequest(
        WatchInteractionsResponseUpdate interaction,
        string action,
        IReadOnlyDictionary<string, string> values)
    {
        var request = new WatchInteractionsRequestUpdate
        {
            InteractionId = interaction.InteractionId
        };

        if (action is "submit" or "update" && interaction.InputsDialog is { } inputsDialog)
        {
            var response = inputsDialog.Clone();
            foreach (var input in response.InputItems)
            {
                if (values.TryGetValue(input.Name, out var value))
                {
                    input.Value = value;
                }

                input.ValidationErrors.Clear();
            }

            request.InputsDialog = response;
            request.ResponseUpdate = action == "update";
        }
        else if (action is "primary" or "secondary")
        {
            var result = action == "primary";
            if (interaction.Notification is { } notification)
            {
                request.Notification = notification.Clone();
                request.Notification.Result = result;
            }
            else
            {
                request.MessageBox = interaction.MessageBox?.Clone() ?? new InteractionMessageBox();
                request.MessageBox.Result = result;
            }
        }
        else
        {
            request.Complete = new InteractionComplete();
        }

        return request;
    }

    internal static DashboardInteraction MapInteraction(WatchInteractionsResponseUpdate interaction)
    {
        var intent = interaction.MessageBox?.Intent ?? interaction.Notification?.Intent ?? MessageIntent.None;
        return new DashboardInteraction(
            InteractionId: interaction.InteractionId,
            Kind: interaction.KindCase switch
            {
                WatchInteractionsResponseUpdate.KindOneofCase.InputsDialog => "inputsDialog",
                WatchInteractionsResponseUpdate.KindOneofCase.MessageBox => "messageBox",
                WatchInteractionsResponseUpdate.KindOneofCase.Notification => "notification",
                _ => "complete"
            },
            Title: interaction.Title,
            Message: interaction.Message,
            PrimaryButtonText: interaction.PrimaryButtonText,
            SecondaryButtonText: interaction.SecondaryButtonText,
            ShowSecondaryButton: interaction.ShowSecondaryButton,
            ShowDismiss: interaction.ShowDismiss,
            EnableMessageMarkdown: interaction.EnableMessageMarkdown,
            Intent: intent switch
            {
                MessageIntent.Success => "success",
                MessageIntent.Warning => "warning",
                MessageIntent.Error => "error",
                MessageIntent.Information => "information",
                MessageIntent.Confirmation => "confirmation",
                _ => "none"
            },
            Inputs: interaction.InputsDialog?.InputItems.Select(MapInput).ToArray() ?? [],
            LinkText: interaction.Notification?.LinkText ?? string.Empty,
            LinkUrl: interaction.Notification?.LinkUrl ?? string.Empty);
    }

    private static DashboardInteractionInput MapInput(InteractionInput input)
    {
        var options = input.Options
            .OrderBy(option => option.Value, StringComparer.Ordinal)
            .ThenBy(option => option.Key, StringComparer.Ordinal)
            .Select(option => new[] { option.Key, option.Value })
            .ToArray();

        return new DashboardInteractionInput(
            Name: input.Name,
            Label: input.Label,
            Placeholder: input.Placeholder,
            InputType: input.InputType switch
            {
                InputType.SecretText => "secretText",
                InputType.Choice => "choice",
                InputType.Boolean => "boolean",
                InputType.Number => "number",
                _ => "text"
            },
            Required: input.Required,
            Options: options,
            Value: input.Value,
            ValidationErrors: input.ValidationErrors.ToArray(),
            Description: input.Description,
            EnableDescriptionMarkdown: input.EnableDescriptionMarkdown,
            MaxLength: input.MaxLength,
            AllowCustomChoice: input.AllowCustomChoice,
            Disabled: input.Disabled || input.Loading,
            UpdateStateOnChange: input.UpdateStateOnChange);
    }
}

internal sealed class DashboardPendingInteractionResponse(
    WatchInteractionsRequestUpdate request,
    Action onDelivered,
    Action<Exception> onFailed)
{
    private int _completionState;

    public WatchInteractionsRequestUpdate Request { get; } = request;

    public void MarkDelivered()
    {
        if (Interlocked.CompareExchange(ref _completionState, 1, 0) is 0)
        {
            onDelivered();
        }
    }

    public void MarkFailed(Exception exception)
    {
        if (Interlocked.CompareExchange(ref _completionState, 2, 0) is 0)
        {
            onFailed(exception);
        }
    }
}
