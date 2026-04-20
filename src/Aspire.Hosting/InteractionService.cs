// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting;

#pragma warning disable ASPIREINTERACTION001 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.

internal class InteractionService : IInteractionService
{
    internal const string DiagnosticId = "ASPIREINTERACTION001";

    private Action<Interaction>? OnInteractionUpdated { get; set; }
    private readonly object _onInteractionUpdatedLock = new();
    private readonly InteractionCollection _interactionCollection = new();
    private readonly ILogger<InteractionService> _logger;
    private readonly DistributedApplicationOptions _distributedApplicationOptions;
    private readonly IServiceProvider _serviceProvider;
    private readonly IConfiguration _configuration;

    public InteractionService(ILogger<InteractionService> logger, DistributedApplicationOptions distributedApplicationOptions, IServiceProvider serviceProvider, IConfiguration configuration)
    {
        _logger = logger;
        _distributedApplicationOptions = distributedApplicationOptions;
        _serviceProvider = serviceProvider;
        _configuration = configuration;
    }

    public bool IsAvailable
    {
        get
        {
            if (_distributedApplicationOptions.DisableDashboard)
            {
                return false;
            }

            // Check if interactivity is explicitly disabled via configuration
            var interactivityEnabled = _configuration[KnownConfigNames.InteractivityEnabled];
            if (!string.IsNullOrEmpty(interactivityEnabled) &&
                bool.TryParse(interactivityEnabled, out var enabled) &&
                !enabled)
            {
                return false;
            }

            return true;
        }
    }

    public async Task<InteractionResult<bool>> PromptConfirmationAsync(string title, string message, MessageBoxInteractionOptions? options = null, CancellationToken cancellationToken = default)
    {
        options ??= MessageBoxInteractionOptions.CreateDefault();
        options.Intent = MessageIntent.Confirmation;
        options.ShowDismiss ??= false;
        options.ShowSecondaryButton ??= true;

        return await PromptMessageBoxCoreAsync(title, message, options, cancellationToken).ConfigureAwait(false);
    }

    public async Task<InteractionResult<bool>> PromptMessageBoxAsync(string title, string message, MessageBoxInteractionOptions? options = null, CancellationToken cancellationToken = default)
    {
        options ??= MessageBoxInteractionOptions.CreateDefault();
        options.ShowSecondaryButton ??= false;
        options.ShowDismiss ??= false;

        return await PromptMessageBoxCoreAsync(title, message, options, cancellationToken).ConfigureAwait(false);
    }

    private async Task<InteractionResult<bool>> PromptMessageBoxCoreAsync(string title, string message, MessageBoxInteractionOptions options, CancellationToken cancellationToken)
    {
        EnsureServiceAvailable();

        cancellationToken.ThrowIfCancellationRequested();
        using var interactionCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        try
        {
            options ??= MessageBoxInteractionOptions.CreateDefault();
            options.ShowDismiss ??= false;

            var newState = new Interaction(title, message, options, new Interaction.MessageBoxInteractionInfo(intent: options.Intent ?? MessageIntent.None), interactionCts.Token);
            AddInteractionUpdate(newState);

            using var _ = cancellationToken.Register(OnInteractionCancellation, state: newState);

            var completion = await newState.CompletionTcs.Task.ConfigureAwait(false);
            var promptState = completion.State as bool?;
            return promptState == null
                ? InteractionResult.Cancel<bool>()
                : InteractionResult.Ok(promptState.Value);
        }
        finally
        {
            interactionCts.Cancel();
        }
    }

    public async Task<InteractionResult<InteractionInput>> PromptInputAsync(string title, string? message, string inputLabel, string placeHolder, InputsDialogInteractionOptions? options = null, CancellationToken cancellationToken = default)
    {
        return await PromptInputAsync(title, message, new InteractionInput { Name = InteractionHelpers.LabelToName(inputLabel), InputType = InputType.Text, Label = inputLabel, Required = true, Placeholder = placeHolder }, options, cancellationToken).ConfigureAwait(false);
    }

    public async Task<InteractionResult<InteractionInput>> PromptInputAsync(string title, string? message, InteractionInput input, InputsDialogInteractionOptions? options = null, CancellationToken cancellationToken = default)
    {
        var result = await PromptInputsAsync(title, message, [input], options, cancellationToken).ConfigureAwait(false);
        if (result.Canceled)
        {
            return InteractionResult.Cancel<InteractionInput>();
        }

        return InteractionResult.Ok(result.Data[0]);
    }

    public async Task<InteractionResult<InteractionInputCollection>> PromptInputsAsync(string title, string? message, IReadOnlyList<InteractionInput> inputs, InputsDialogInteractionOptions? options = null, CancellationToken cancellationToken = default)
    {
        EnsureServiceAvailable();

        cancellationToken.ThrowIfCancellationRequested();

        // Create the collection early to validate names and generate missing ones
        var inputCollection = new InteractionInputCollection(inputs);

        // Validate inputs.
        for (var i = 0; i < inputs.Count; i++)
        {
            var input = inputs[i];
            if (input.DynamicLoading is { } dynamic)
            {
                if (dynamic.DependsOnInputs != null)
                {
                    foreach (var dependsOnInputName in dynamic.DependsOnInputs)
                    {
                        // Validate dependency input exists and is defined before this input.
                        // We check that the dependency is defined before this input so that experiences such as the CLI, where inputs are forward only, work correctly.
                        if (!inputCollection.TryGetByName(dependsOnInputName, out var dependsOnInput))
                        {
                            throw new InvalidOperationException($"The input '{input.Name}' has {nameof(InteractionInput.DynamicLoading)} that depends on an input named '{dependsOnInputName}', but no such input exists.");
                        }
                        if (inputCollection.IndexOf(dependsOnInput) >= i)
                        {
                            throw new InvalidOperationException($"The input '{input.Name}' has {nameof(InteractionInput.DynamicLoading)} that depends on an input named '{dependsOnInputName}', but that input is not defined before it. Inputs must be defined in order so that dependencies are always to earlier inputs.");
                        }
                    }
                }
            }
        }

        using var interactionCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        try
        {
            options ??= InputsDialogInteractionOptions.Default;

            var newState = new Interaction(title, message, options, new Interaction.InputsInteractionInfo(inputCollection), interactionCts.Token);
            AddInteractionUpdate(newState);

            using var _ = cancellationToken.Register(OnInteractionCancellation, state: newState);

            foreach (var input in inputs)
            {
                if (input.DynamicLoading is { } dynamic)
                {
                    var dynamicState = new InputLoadingState(dynamic)
                    {
                        OnLoadComplete = (input) =>
                        {
                            // Options or value on a choice could have changed. Ensure the value is still valid.
                            if (input.InputType == InputType.Choice)
                            {
                                // Check that the previously specified value is in the new options.
                                // If the value isn't in the new options then clear it.
                                // Don't clear the value if a custom choice is allowed.
                                if (!input.AllowCustomChoice && !string.IsNullOrEmpty(input.Value))
                                {
                                    if (input.Options == null || !input.Options.Any(o => o.Key == input.Value))
                                    {
                                        input.Value = null;
                                    }
                                }
                            }

                            // Notify the UI that the interaction has been updated.
                            UpdateInteraction(newState);
                        }
                    };

                    input.DynamicLoadingState = dynamicState;

                    // Refresh input on start if:
                    // -The dynamic input doesn't depend on other inputs, or
                    // -Has been configured to always update
                    if (dynamic.DependsOnInputs == null || dynamic.DependsOnInputs.Count == 0 || dynamic.AlwaysLoadOnStart)
                    {
                        var refreshOptions = new QueueLoadOptions(_logger, interactionCts.Token, input, inputCollection, _serviceProvider);
                        input.DynamicLoadingState.QueueLoad(refreshOptions);
                    }
                }
            }

            var completion = await newState.CompletionTcs.Task.ConfigureAwait(false);
            return completion.State is not IReadOnlyList<InteractionInput> inputState
                ? InteractionResult.Cancel<InteractionInputCollection>()
                : InteractionResult.Ok(new InteractionInputCollection(inputState));
        }
        finally
        {
            interactionCts.Cancel();
        }
    }

    public async Task<InteractionResult<bool>> PromptNotificationAsync(string title, string message, NotificationInteractionOptions? options = null, CancellationToken cancellationToken = default)
    {
        EnsureServiceAvailable();

        cancellationToken.ThrowIfCancellationRequested();
        using var interactionCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        try
        {
            options ??= NotificationInteractionOptions.CreateDefault();

            var newState = new Interaction(title, message, options, new Interaction.NotificationInteractionInfo(intent: options.Intent ?? MessageIntent.None, linkText: options.LinkText, linkUrl: options.LinkUrl), interactionCts.Token);
            AddInteractionUpdate(newState);

            using var _ = cancellationToken.Register(OnInteractionCancellation, state: newState);

            var completion = await newState.CompletionTcs.Task.ConfigureAwait(false);
            var promptState = completion.State as bool?;
            return promptState == null
                ? InteractionResult.Cancel<bool>()
                : InteractionResult.Ok(promptState.Value);
        }
        finally
        {
            interactionCts.Cancel();
        }
    }

    // For testing.
    internal List<Interaction> GetCurrentInteractions()
    {
        lock (_onInteractionUpdatedLock)
        {
            return _interactionCollection.ToList();
        }
    }

    private void OnInteractionCancellation(object? newState)
    {
        var interactionState = (Interaction)newState!;

        interactionState.State = Interaction.InteractionState.Complete;
        interactionState.CompletionTcs.TrySetResult(new InteractionCompletionState { Complete = true });
        AddInteractionUpdate(interactionState);
    }

    private void AddInteractionUpdate(Interaction interactionUpdate)
    {
        lock (_onInteractionUpdatedLock)
        {
            var updateEvent = false;

            if (interactionUpdate.State == Interaction.InteractionState.Complete)
            {
                Debug.Assert(
                    interactionUpdate.CompletionTcs.Task.IsCompleted,
                    "TaskCompletionSource should be completed when interaction is done.");

                // Only update event if interaction was previously registered and not already removed.
                updateEvent = _interactionCollection.Remove(interactionUpdate.InteractionId);
            }
            else
            {
                if (_interactionCollection.Contains(interactionUpdate.InteractionId))
                {
                    // Should never happen, but throw descriptive exception if it does.
                    throw new InvalidOperationException($"An interaction with ID {interactionUpdate.InteractionId} already exists. Interaction IDs must be unique.");
                }

                _interactionCollection.Add(interactionUpdate);
                updateEvent = true;
            }

            if (updateEvent)
            {
                OnInteractionUpdated?.Invoke(interactionUpdate);
            }
        }
    }

    internal void UpdateInteraction(Interaction interaction)
    {
        lock (_onInteractionUpdatedLock)
        {
            // Double check interaction is still in collection after awaiting the result creation.
            if (!_interactionCollection.TryGetValue(interaction.InteractionId, out var interactionState))
            {
                return;
            }

            // Broadcast out the updated interaction.
            OnInteractionUpdated?.Invoke(interactionState);
        }
    }

    internal async Task ProcessInteractionFromClientAsync(int interactionId, Func<Interaction, IServiceProvider, ILogger, InteractionCompletionState> createResult, CancellationToken cancellationToken)
    {
        Interaction? interactionState = null;

        lock (_onInteractionUpdatedLock)
        {
            if (!_interactionCollection.TryGetValue(interactionId, out interactionState))
            {
                _logger.LogDebug("No interaction found with ID {InteractionId}.", interactionId);
                return;
            }
        }

        var result = createResult(interactionState, _serviceProvider, _logger);

        // Run validation for inputs interaction.
        if (!await RunValidationAsync(interactionState, result, cancellationToken).ConfigureAwait(false))
        {
            // Interaction is not complete if there are validation errors.
            result = new InteractionCompletionState { Complete = false, State = result.State };
        }

        lock (_onInteractionUpdatedLock)
        {
            // Double check interaction is still in collection after awaiting the result creation.
            if (!_interactionCollection.TryGetValue(interactionId, out interactionState))
            {
                return;
            }

            if (result.Complete)
            {
                interactionState.CompletionTcs.TrySetResult(result);
                interactionState.State = Interaction.InteractionState.Complete;
                _interactionCollection.Remove(interactionId);
            }

            // Either broadcast out the interaction is complete, or its updated state.
            OnInteractionUpdated?.Invoke(interactionState);
        }
    }

    /// <summary>
    /// Runs validation for the inputs interaction.
    /// </summary>
    /// <returns>
    /// true if validation passed, false if there were validation errors.
    /// </returns>
    private async Task<bool> RunValidationAsync(Interaction interactionState, InteractionCompletionState result, CancellationToken cancellationToken)
    {
        if (result.Complete && interactionState.InteractionInfo is Interaction.InputsInteractionInfo inputsInfo)
        {
            // State could be null if the user dismissed the inputs dialog. There is nothing to validate in this situation.
            if (result.State is IReadOnlyList<InteractionInput> inputs)
            {
                foreach (var input in inputs)
                {
                    input.ValidationErrors.Clear();
                }

                var context = new InputsDialogValidationContext
                {
                    CancellationToken = cancellationToken,
                    Services = _serviceProvider,
                    Inputs = inputsInfo.Inputs
                };

                foreach (var input in inputs)
                {
                    var value = input.Value = input.Value?.Trim();

                    if (string.IsNullOrEmpty(value))
                    {
                        if (input.Required)
                        {
                            context.AddValidationError(input, "Value is required.");
                        }
                    }
                    else
                    {
                        switch (input.InputType)
                        {
                            case InputType.Text:
                            case InputType.SecretText:
                                var maxLength = InteractionHelpers.GetMaxLength(input.MaxLength);

                                if (value.Length > maxLength)
                                {
                                    context.AddValidationError(input, $"Value length exceeds {maxLength} characters.");
                                }
                                break;
                            case InputType.Choice:
                                if (!input.AllowCustomChoice)
                                {
                                    var options = input.Options;
                                    if (options != null && !options.Any(o => o.Key == value))
                                    {
                                        context.AddValidationError(input, "Value must be one of the provided options.");
                                    }
                                }
                                break;
                            case InputType.Boolean:
                                if (!bool.TryParse(value, out _))
                                {
                                    context.AddValidationError(input, "Value must be a valid boolean.");
                                }
                                break;
                            case InputType.Number:
                                if (!int.TryParse(value, CultureInfo.InvariantCulture, out _))
                                {
                                    context.AddValidationError(input, "Value must be a valid number.");
                                }
                                break;
                            case InputType.FileChooser:
                                // File chooser values are file paths; basic validation that the value is not empty is handled above.
                                break;
                            default:
                                break;
                        }
                    }
                }

                // Only run validation callback if there are no data validation errors.
                if (!context.HasErrors)
                {
                    var options = (InputsDialogInteractionOptions)interactionState.Options;
                    if (options.ValidationCallback is { } validationCallback)
                    {
                        await validationCallback(context).ConfigureAwait(false);
                    }
                }

                return !context.HasErrors;
            }
        }

        return true;
    }

    internal async IAsyncEnumerable<Interaction> SubscribeInteractionUpdates([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var channel = Channel.CreateUnbounded<Interaction>();

        void WriteToChannel(Interaction resourceEvent) =>
            channel.Writer.TryWrite(resourceEvent);

        List<Interaction> pendingInteractions;

        lock (_onInteractionUpdatedLock)
        {
            OnInteractionUpdated += WriteToChannel;

            pendingInteractions = _interactionCollection.ToList();
        }

        foreach (var interaction in pendingInteractions)
        {
            yield return interaction;
        }

        try
        {
            await foreach (var item in channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                yield return item;
            }
        }
        finally
        {
            lock (_onInteractionUpdatedLock)
            {
                OnInteractionUpdated -= WriteToChannel;
            }

            channel.Writer.TryComplete();
        }
    }

    private void EnsureServiceAvailable()
    {
        if (!IsAvailable)
        {
            throw new InvalidOperationException($"InteractionService is not available because the dashboard is not enabled. Use the {nameof(IsAvailable)} property to determine whether the service is available.");
        }
    }
}

internal class InteractionCollection : KeyedCollection<int, Interaction>
{
    protected override int GetKeyForItem(Interaction item) => item.InteractionId;
}

[DebuggerDisplay("State = {State}, Complete = {Complete}")]
internal sealed class InteractionCompletionState
{
    public bool Complete { get; init; }
    public object? State { get; init; }
}

[DebuggerDisplay("InteractionId = {InteractionId}, State = {State}, Title = {Title}")]
internal class Interaction
{
    private static int s_nextInteractionId;

    public int InteractionId { get; }
    public InteractionState State { get; set; }
    public TaskCompletionSource<InteractionCompletionState> CompletionTcs { get; } = new TaskCompletionSource<InteractionCompletionState>(TaskCreationOptions.RunContinuationsAsynchronously);
    public InteractionInfoBase InteractionInfo { get; }
    public CancellationToken CancellationToken { get; }

    public string Title { get; }
    public string? Message { get; }
    public InteractionOptions Options { get; }

    public Interaction(string title, string? message, InteractionOptions options, InteractionInfoBase interactionInfo, CancellationToken cancellationToken)
    {
        InteractionId = Interlocked.Increment(ref s_nextInteractionId);
        Title = title;
        Message = message;
        Options = options;
        InteractionInfo = interactionInfo;
        CancellationToken = cancellationToken;
    }

    internal enum InteractionState
    {
        InProgress,
        Complete
    }

    internal abstract class InteractionInfoBase
    {
    }

    internal sealed class MessageBoxInteractionInfo : InteractionInfoBase
    {
        public MessageBoxInteractionInfo(MessageIntent intent)
        {
            Intent = intent;
        }

        public MessageIntent Intent { get; }
    }

    internal sealed class NotificationInteractionInfo : InteractionInfoBase
    {
        public NotificationInteractionInfo(MessageIntent intent, string? linkText, string? linkUrl)
        {
            Intent = intent;
            LinkText = linkText;
            LinkUrl = linkUrl;
        }

        public MessageIntent Intent { get; }
        public string? LinkText { get; }
        public string? LinkUrl { get; }
    }

    internal sealed class InputsInteractionInfo : InteractionInfoBase
    {
        public InputsInteractionInfo(InteractionInputCollection inputs)
        {
            Inputs = inputs;
        }

        public InteractionInputCollection Inputs { get; }
    }
}

#pragma warning restore ASPIREINTERACTION001 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.
