// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.DashboardService.Proto.V1;

namespace Aspire.Dashboard.Api;

internal sealed class DeckInteractionService(
    IDashboardClient dashboardClient,
    ILogger<DeckInteractionService> logger) : IAsyncDisposable
{
    private const long DefaultMaximumFileSize = 100 * 1024 * 1024;
    private const int MaximumUploadedFilesPerInteraction = 100;

    private readonly CancellationTokenSource _cts = new();
    private readonly object _lock = new();
    private readonly Dictionary<int, WatchInteractionsResponseUpdate> _interactions = [];
    private readonly List<int> _interactionOrder = [];
    private readonly Dictionary<int, int> _uploadedFileCounts = [];
    private readonly SemaphoreSlim _fileUploadSlots = new(4, 4);
    private Task? _watchTask;

    public DeckInteraction[] GetInteractions()
    {
        EnsureStarted();

        lock (_lock)
        {
            return _interactionOrder
                .Select(interactionId => MapInteraction(_interactions[interactionId]))
                .ToArray();
        }
    }

    public async Task<bool> RespondAsync(
        int interactionId,
        string action,
        IReadOnlyDictionary<string, string> values,
        CancellationToken cancellationToken)
    {
        EnsureStarted();

        WatchInteractionsResponseUpdate interaction;
        lock (_lock)
        {
            if (!_interactions.TryGetValue(interactionId, out var pendingInteraction))
            {
                return false;
            }

            interaction = pendingInteraction.Clone();
            if (!string.Equals(action, "update", StringComparison.Ordinal))
            {
                RemoveInteraction(interactionId);
            }
        }

        var request = BuildRequest(interaction, action, values);
        try
        {
            await dashboardClient.SendInteractionRequestAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // Terminal actions are removed optimistically so the browser cannot submit twice. Restore
            // the interaction if delivery fails, allowing the user to retry after a transient failure.
            if (!string.Equals(action, "update", StringComparison.Ordinal))
            {
                lock (_lock)
                {
                    AddOrUpdateInteraction(interaction);
                }
            }

            throw;
        }

        return true;
    }

    public bool TryGetFileUploadLimit(int interactionId, string inputName, out long maximumSize)
    {
        EnsureStarted();

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

    public async Task<DeckInteractionFileUploadResponse?> UploadFileAsync(
        int interactionId,
        string inputName,
        string fileName,
        Stream fileStream,
        long? expectedSize,
        CancellationToken cancellationToken)
    {
        EnsureStarted();

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
                throw new DeckInteractionFileTooLargeException(maximumSize);
            }

            var uploadCount = _uploadedFileCounts.GetValueOrDefault(interactionId);
            if (uploadCount >= MaximumUploadedFilesPerInteraction)
            {
                throw new DeckInteractionFileUploadLimitException(MaximumUploadedFilesPerInteraction);
            }
            _uploadedFileCounts[interactionId] = uploadCount + 1;
        }

        var uploadSlotAcquired = false;
        try
        {
            await _fileUploadSlots.WaitAsync(cancellationToken).ConfigureAwait(false);
            uploadSlotAcquired = true;

            var fileId = await dashboardClient.UploadFileAsync(
                fileStream,
                fileName,
                expectedSize ?? maximumSize,
                cancellationToken).ConfigureAwait(false);
            return new DeckInteractionFileUploadResponse(fileId, fileName);
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

    private void EnsureStarted()
    {
        if (!dashboardClient.IsEnabled)
        {
            return;
        }

        lock (_lock)
        {
            _watchTask ??= Task.Run(() => WatchInteractionsAsync(_cts.Token));
        }
    }

    private async Task WatchInteractionsAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var interaction in dashboardClient.SubscribeInteractionsAsync(cancellationToken).ConfigureAwait(false))
            {
                lock (_lock)
                {
                    if (interaction.KindCase == WatchInteractionsResponseUpdate.KindOneofCase.Complete)
                    {
                        RemoveInteraction(interaction.InteractionId);
                    }
                    else
                    {
                        AddOrUpdateInteraction(interaction);
                    }
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Unexpected error while watching Deck interactions.");
        }
    }

    private void AddOrUpdateInteraction(WatchInteractionsResponseUpdate interaction)
    {
        if (!_interactions.ContainsKey(interaction.InteractionId))
        {
            _interactionOrder.Add(interaction.InteractionId);
        }

        _interactions[interaction.InteractionId] = interaction;
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

    private static DeckInteraction MapInteraction(WatchInteractionsResponseUpdate interaction)
    {
        var intent = interaction.MessageBox?.Intent ?? interaction.Notification?.Intent ?? MessageIntent.None;
        return new DeckInteraction(
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

    private static DeckInteractionInput MapInput(InteractionInput input)
    {
        var options = input.Options
            .OrderBy(option => option.Value, StringComparer.Ordinal)
            .ThenBy(option => option.Key, StringComparer.Ordinal)
            .Select(option => new[] { option.Key, option.Value })
            .ToArray();

        return new DeckInteractionInput(
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

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync().ConfigureAwait(false);
        if (_watchTask is not null)
        {
            try
            {
                await _watchTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        _cts.Dispose();
    }
}

internal sealed class DeckInteractionFileTooLargeException(long maximumSize)
    : Exception($"The uploaded file exceeds the maximum size of {maximumSize} bytes.")
{
}

internal sealed class DeckInteractionFileUploadLimitException(int maximumFileCount)
    : Exception($"No more than {maximumFileCount} files can be uploaded for one interaction.")
{
}
