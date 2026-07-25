// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Threading.Channels;
using Aspire.DashboardService.Proto.V1;
using Grpc.Core;

namespace Aspire.Dashboard.Backend;

internal interface IDashboardInteractionService
{
    DashboardInteraction[] GetInteractions();

    bool TryGetFileUploadLimit(int interactionId, string inputName, out long maximumSize);

    ValueTask<DashboardInteractionFileUploadResponse?> UploadFileAsync(
        int interactionId,
        string inputName,
        string fileName,
        Stream fileStream,
        long? expectedSize,
        CancellationToken cancellationToken);

    ValueTask<bool> RespondAsync(
        DashboardRespondInteractionRequest request,
        CancellationToken cancellationToken);
}

internal sealed class DashboardInteractionService(
    IDashboardResourceServiceConnection resourceServiceConnection,
    ILogger<DashboardInteractionService> logger) : BackgroundService, IDashboardInteractionService
{
    private const long DefaultMaximumFileSize = 100 * 1024 * 1024;
    private const int MaximumPendingInteractions = 256;
    private const int MaximumUploadedFilesPerInteraction = 100;
    private const int ResponseBufferCapacity = 64;

    private readonly object _lock = new();
    private readonly Dictionary<int, WatchInteractionsResponseUpdate> _interactions = [];
    private readonly List<int> _interactionOrder = [];
    private readonly Dictionary<int, DashboardPendingInteractionResponse> _terminalResponsesInFlight = [];
    private readonly Dictionary<int, int> _uploadedFileCounts = [];
    private readonly SemaphoreSlim _fileUploadSlots = new(4, 4);
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

    public bool TryGetFileUploadLimit(int interactionId, string inputName, out long maximumSize)
    {
        lock (_lock)
        {
            var input = FindFileInput(interactionId, inputName);
            if (input is null)
            {
                maximumSize = 0;
                return false;
            }

            maximumSize = GetMaximumFileSize(input);
            return true;
        }
    }

    public async ValueTask<DashboardInteractionFileUploadResponse?> UploadFileAsync(
        int interactionId,
        string inputName,
        string fileName,
        Stream fileStream,
        long? expectedSize,
        CancellationToken cancellationToken)
    {
        long maximumSize;
        lock (_lock)
        {
            var input = FindFileInput(interactionId, inputName);
            if (input is null)
            {
                return null;
            }

            maximumSize = GetMaximumFileSize(input);
            if (expectedSize is { } size && size > maximumSize)
            {
                throw new DashboardInteractionFileTooLargeException(maximumSize);
            }

            var uploadCount = _uploadedFileCounts.GetValueOrDefault(interactionId);
            if (uploadCount >= MaximumUploadedFilesPerInteraction)
            {
                throw new DashboardInteractionFileUploadLimitException(MaximumUploadedFilesPerInteraction);
            }
            _uploadedFileCounts[interactionId] = uploadCount + 1;
        }

        var uploadSlotAcquired = false;
        try
        {
            await _fileUploadSlots.WaitAsync(cancellationToken).ConfigureAwait(false);
            uploadSlotAcquired = true;

            var fileId = await resourceServiceConnection.UploadFileAsync(
                fileStream,
                fileName,
                maximumSize,
                expectedSize,
                cancellationToken).ConfigureAwait(false);
            return new DashboardInteractionFileUploadResponse(fileId, fileName);
        }
        catch
        {
            ReleaseFileUploadReservation(interactionId);
            throw;
        }
        finally
        {
            if (uploadSlotAcquired)
            {
                _fileUploadSlots.Release();
            }
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
        _uploadedFileCounts.Remove(interactionId);
    }

    private InteractionInput? FindFileInput(int interactionId, string inputName)
    {
        if (!_interactions.TryGetValue(interactionId, out var interaction)
            || interaction.InputsDialog is not { } inputsDialog)
        {
            return null;
        }

        return inputsDialog.InputItems.FirstOrDefault(
            input => input.InputType is InputType.File
                && string.Equals(input.Name, inputName, StringComparison.Ordinal));
    }

    private static long GetMaximumFileSize(InteractionInput input) =>
        input.MaxFileSize > 0 ? input.MaxFileSize : DefaultMaximumFileSize;

    private void ReleaseFileUploadReservation(int interactionId)
    {
        lock (_lock)
        {
            var uploadCount = _uploadedFileCounts.GetValueOrDefault(interactionId);
            if (uploadCount <= 1)
            {
                _uploadedFileCounts.Remove(interactionId);
            }
            else
            {
                _uploadedFileCounts[interactionId] = uploadCount - 1;
            }
        }
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
                InputType.File => "file",
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
            UpdateStateOnChange: input.UpdateStateOnChange,
            FileFilter: input.FileFilter,
            AllowMultipleFiles: input.AllowMultipleFiles,
            MaxFileSize: GetMaximumFileSize(input));
    }
}

internal sealed class DashboardInteractionFileUploadLimitException(int maximumFileCount)
    : Exception($"No more than {maximumFileCount} files can be uploaded for one interaction.")
{
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
