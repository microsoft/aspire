// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.Extensions.DependencyInjection;

namespace Aspire.Hosting.Ats;

#pragma warning disable ASPIREINTERACTION001 // IInteractionService and related types are experimental.

/// <summary>
/// ATS exports for the interaction service.
/// </summary>
/// <remarks>
/// <para>
/// The interaction service surface is tailored for polyglot app hosts rather than exposed directly. The shipped
/// .NET API models inputs with delegate-bearing option types (for example <see cref="InputLoadOptions.LoadCallback"/>)
/// that cannot be serialized as ATS DTOs. Instead, polyglot callers build inputs through factory capabilities that
/// return the opaque <see cref="InteractionInputBuilder"/> handle, attach behavior such as dynamic loading via
/// callbacks on that handle, and then pass the handles to the prompt capabilities.
/// </para>
/// </remarks>
internal static class InteractionExports
{
    /// <summary>
    /// Gets the interaction service from the service provider.
    /// </summary>
    /// <param name="serviceProvider">The service provider handle.</param>
    /// <returns>An interaction service handle.</returns>
    [AspireExport]
    public static IInteractionService GetInteractionService(this IServiceProvider serviceProvider)
    {
        ArgumentNullException.ThrowIfNull(serviceProvider);

        return serviceProvider.GetRequiredService<IInteractionService>();
    }

    /// <summary>
    /// Gets a value indicating whether the interaction service is available to prompt the user.
    /// </summary>
    /// <param name="interactionService">The interaction service handle.</param>
    /// <returns><see langword="true"/> when the service can prompt the user; otherwise <see langword="false"/>.</returns>
    [AspireExport]
    public static bool IsAvailable(this IInteractionService interactionService)
    {
        ArgumentNullException.ThrowIfNull(interactionService);

        return interactionService.IsAvailable;
    }

    /// <summary>
    /// Prompts the user for confirmation with an OK/Cancel dialog.
    /// </summary>
    [AspireExport]
    public static async Task<BoolInteractionResult> PromptConfirmation(
        this IInteractionService interactionService,
        string title,
        string message,
        InteractionMessageBoxOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(interactionService);

        var result = await interactionService.PromptConfirmationAsync(title, message, options?.ToOptions(), cancellationToken).ConfigureAwait(false);
        return BoolInteractionResult.From(result);
    }

    /// <summary>
    /// Prompts the user with a message box dialog.
    /// </summary>
    [AspireExport]
    public static async Task<BoolInteractionResult> PromptMessageBox(
        this IInteractionService interactionService,
        string title,
        string message,
        InteractionMessageBoxOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(interactionService);

        var result = await interactionService.PromptMessageBoxAsync(title, message, options?.ToOptions(), cancellationToken).ConfigureAwait(false);
        return BoolInteractionResult.From(result);
    }

    /// <summary>
    /// Prompts the user with a notification.
    /// </summary>
    [AspireExport]
    public static async Task<BoolInteractionResult> PromptNotification(
        this IInteractionService interactionService,
        string title,
        string message,
        InteractionNotificationOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(interactionService);

        var result = await interactionService.PromptNotificationAsync(title, message, options?.ToOptions(), cancellationToken).ConfigureAwait(false);
        return BoolInteractionResult.From(result);
    }

    /// <summary>
    /// Prompts the user for a single input.
    /// </summary>
    // Prompts can invoke dynamic-loading and validation callbacks that re-enter the remote host through ATS, so the
    // synchronous invocation path must run on a background thread to keep the JSON-RPC loop processing nested callbacks.
    [AspireExport(RunSyncOnBackgroundThread = true)]
    public static async Task<InputInteractionResult> PromptInput(
        this IInteractionService interactionService,
        string title,
        string? message,
        InteractionInputBuilder input,
        InteractionInputsDialogOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(interactionService);
        ArgumentNullException.ThrowIfNull(input);

        var result = await interactionService.PromptInputAsync(title, message, input.Input, options?.ToOptions(), cancellationToken).ConfigureAwait(false);
        return InputInteractionResult.From(result);
    }

    /// <summary>
    /// Prompts the user for multiple inputs.
    /// </summary>
    // Prompts can invoke dynamic-loading and validation callbacks that re-enter the remote host through ATS, so the
    // synchronous invocation path must run on a background thread to keep the JSON-RPC loop processing nested callbacks.
    [AspireExport(RunSyncOnBackgroundThread = true)]
    public static async Task<InputsInteractionResult> PromptInputs(
        this IInteractionService interactionService,
        string title,
        string? message,
        InteractionInputBuilder[] inputs,
        InteractionInputsDialogOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(interactionService);
        ArgumentNullException.ThrowIfNull(inputs);

        var interactionInputs = new InteractionInput[inputs.Length];
        for (var i = 0; i < inputs.Length; i++)
        {
            var input = inputs[i] ?? throw new ArgumentException($"The input at index {i} cannot be null.", nameof(inputs));
            interactionInputs[i] = input.Input;
        }

        var result = await interactionService.PromptInputsAsync(title, message, interactionInputs, options?.ToOptions(), cancellationToken).ConfigureAwait(false);
        return InputsInteractionResult.From(result);
    }

    // The input factories hang off IInteractionService so the ATS scanner treats the service handle as the
    // receiver (polyglot: interactionService.createTextInput(...)). The receiver itself is unused because inputs
    // are independent of the service, so suppress the unused-parameter analyzer for the factory block.
#pragma warning disable IDE0060 // Remove unused parameter
    /// <summary>
    /// Creates a single-line text input.
    /// </summary>
    [AspireExport]
    public static InteractionInputBuilder CreateTextInput(this IInteractionService interactionService, string name, CreateInteractionInputOptions? options = null)
    {
        return InteractionInputBuilder.Create(name, InputType.Text, options);
    }

    /// <summary>
    /// Creates a secret (masked) text input.
    /// </summary>
    [AspireExport]
    public static InteractionInputBuilder CreateSecretInput(this IInteractionService interactionService, string name, CreateInteractionInputOptions? options = null)
    {
        return InteractionInputBuilder.Create(name, InputType.SecretText, options);
    }

    /// <summary>
    /// Creates a boolean (checkbox) input.
    /// </summary>
    [AspireExport]
    public static InteractionInputBuilder CreateBooleanInput(this IInteractionService interactionService, string name, CreateInteractionInputOptions? options = null)
    {
        return InteractionInputBuilder.Create(name, InputType.Boolean, options);
    }

    /// <summary>
    /// Creates a numeric input.
    /// </summary>
    [AspireExport]
    public static InteractionInputBuilder CreateNumberInput(this IInteractionService interactionService, string name, CreateInteractionInputOptions? options = null)
    {
        return InteractionInputBuilder.Create(name, InputType.Number, options);
    }

    /// <summary>
    /// Creates a choice input that selects from a list of options.
    /// </summary>
    /// <param name="interactionService">The interaction service.</param>
    /// <param name="name">The name of the input.</param>
    /// <param name="choices">The available choices, in display order. Each option pairs a submitted value with a display label.</param>
    /// <param name="options">Optional configuration for the input.</param>
    [AspireExport]
    public static InteractionInputBuilder CreateChoiceInput(this IInteractionService interactionService, string name, IReadOnlyList<InteractionChoiceOption>? choices = null, CreateInteractionInputOptions? options = null)
    {
        var builder = InteractionInputBuilder.Create(name, InputType.Choice, options);
        if (choices is { Count: > 0 })
        {
            builder.Input.Options = ToOptionList(choices);
        }

        return builder;
    }
#pragma warning restore IDE0060 // Remove unused parameter

    // Preserve the caller-specified order: the native Options list is ordered, and the order is user-visible in the
    // rendered dropdown. Materialize a copy so a caller-held list cannot mutate the input after the fact.
    internal static IReadOnlyList<KeyValuePair<string, string>> ToOptionList(IReadOnlyList<InteractionChoiceOption> choices)
    {
        var list = new List<KeyValuePair<string, string>>(choices.Count);
        foreach (var choice in choices)
        {
            list.Add(KeyValuePair.Create(choice.Value, choice.Label));
        }

        return list;
    }

    // The engine returns the same InteractionInput instances that the builders own, and those still carry the
    // dynamic-loading delegate on DynamicLoading.LoadCallback. That delegate is a .NET Func that cannot be
    // serialized across the ATS/JSON-RPC boundary, so project result inputs onto callback-free copies before they
    // are sent back to the polyglot caller. The caller only consumes data fields such as Name, Value and Options.
    internal static InteractionInput ToResultInput(InteractionInput input)
    {
        return new InteractionInput
        {
            Name = input.Name,
            Label = input.Label,
            Description = input.Description,
            EnableDescriptionMarkdown = input.EnableDescriptionMarkdown,
            InputType = input.InputType,
            Required = input.Required,
            Options = input.Options,
            Value = input.Value,
            Placeholder = input.Placeholder,
            AllowCustomChoice = input.AllowCustomChoice,
            Disabled = input.Disabled,
            MaxLength = input.MaxLength,
            // DynamicLoading is intentionally omitted: it holds the non-serializable LoadCallback delegate.
        };
    }
}

/// <summary>
/// An opaque, server-side builder for an <see cref="InteractionInput"/> used by polyglot app hosts.
/// </summary>
/// <remarks>
/// The builder owns the live <see cref="InteractionInput"/> instance. Dynamic-loading callbacks mutate this same
/// instance through <see cref="InteractionInputLoadContext"/>, which is why the input is modeled as a handle here
/// instead of the by-value <c>InteractionInput</c> DTO.
/// </remarks>
[AspireExport]
internal sealed class InteractionInputBuilder
{
    private InteractionInputBuilder(InteractionInput input)
    {
        Input = input;
    }

    internal InteractionInput Input { get; }

    internal static InteractionInputBuilder Create(string name, InputType inputType, CreateInteractionInputOptions? options)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        var input = new InteractionInput
        {
            Name = name,
            InputType = inputType,
            Label = options?.Label,
            Description = options?.Description,
            EnableDescriptionMarkdown = options?.EnableDescriptionMarkdown ?? false,
            Required = options?.Required ?? false,
            Placeholder = options?.Placeholder,
            Value = options?.Value,
            AllowCustomChoice = options?.AllowCustomChoice ?? false,
            Disabled = options?.Disabled ?? false,
            MaxLength = options?.MaxLength,
        };

        return new InteractionInputBuilder(input);
    }

    /// <summary>
    /// Sets the choice options for the input.
    /// </summary>
    /// <param name="choices">The available choices, in display order. Each option pairs a submitted value with a display label.</param>
    /// <returns>The same builder handle.</returns>
    [AspireExport]
    public InteractionInputBuilder WithChoiceOptions(IReadOnlyList<InteractionChoiceOption> choices)
    {
        ArgumentNullException.ThrowIfNull(choices);

        Input.Options = InteractionExports.ToOptionList(choices);
        return this;
    }

    /// <summary>
    /// Sets the value of the input.
    /// </summary>
    /// <param name="value">The value to assign.</param>
    /// <returns>The same builder handle.</returns>
    [AspireExport]
    public InteractionInputBuilder WithValue(string? value)
    {
        Input.Value = value;
        return this;
    }

    /// <summary>
    /// Attaches a callback that dynamically loads or updates the input after the prompt starts.
    /// </summary>
    /// <param name="callback">The callback invoked to load the input. Use the supplied context to read other inputs and update this input.</param>
    /// <param name="options">Optional configuration that controls when the callback runs.</param>
    /// <returns>The same builder handle.</returns>
    [AspireExport]
    public InteractionInputBuilder WithDynamicLoading(Func<InteractionInputLoadContext, Task> callback, DynamicLoadingOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(callback);

        // Bridge the engine's LoadInputContext to the curated polyglot context so callbacks never see the raw
        // IServiceProvider and can only mutate the live input through guarded setters.
        Input.SetDynamicLoading(new InputLoadOptions
        {
            LoadCallback = loadContext => callback(new InteractionInputLoadContext(loadContext)),
            AlwaysLoadOnStart = options?.AlwaysLoadOnStart ?? false,
            DependsOnInputs = options?.DependsOnInputs,
        });

        return this;
    }
}

/// <summary>
/// The context passed to a polyglot dynamic-loading callback. Exposes the loading input as a handle and provides
/// read access to the other inputs in the prompt.
/// </summary>
[AspireExport(ExposeProperties = true)]
internal sealed class InteractionInputLoadContext
{
    private readonly LoadInputContext _inner;
    private readonly InteractionLoadingInput _input;

    internal InteractionInputLoadContext(LoadInputContext inner)
    {
        _inner = inner;
        _input = new InteractionLoadingInput(inner);
    }

    /// <summary>
    /// Gets a handle to the input that is loading. Mutate the input through this handle.
    /// </summary>
    /// <returns>A handle to the loading input.</returns>
    /// <remarks>
    /// Mirrors the native <c>LoadInputContext.Input</c>: the callback updates the live input it is loading, rather than
    /// the context itself. The input is a handle (not a by-value DTO) so guarded setters route back to the server-side
    /// input across the ATS boundary.
    /// </remarks>
    [AspireExport]
    public InteractionLoadingInput Input()
    {
        return _input;
    }

    /// <summary>
    /// Gets all inputs in the prompt, including the one currently loading.
    /// </summary>
    /// <remarks>
    /// Mirrors the native <c>LoadInputContext.AllInputs</c>. Use the collection's by-name accessors (for example
    /// <c>value</c> or <c>requiredValue</c>) to read the dependency inputs declared via
    /// <see cref="DynamicLoadingOptions.DependsOnInputs"/>. This is the same <see cref="InteractionInputCollection"/>
    /// idiom used by the validation callback and prompt results, so reading inputs by name is consistent across every
    /// callback context. This is exposed as a property (rather than a method) so it routes through the generated
    /// collection accessor, matching the other contexts that surface an <see cref="InteractionInputCollection"/>.
    /// </remarks>
    public InteractionInputCollection Inputs => _inner.AllInputs;
}

/// <summary>
/// A handle to the input currently being loaded by a dynamic-loading callback. Mirrors the native
/// <c>LoadInputContext.Input</c> by letting callbacks update the live input directly.
/// </summary>
/// <remarks>
/// The handle owns the live <see cref="InteractionInput"/> for the duration of the load callback. Setters are routed
/// back to the server-side input across the ATS boundary, which is why this is a handle rather than the by-value
/// <c>InteractionInput</c> DTO.
/// </remarks>
[AspireExport]
internal sealed class InteractionLoadingInput
{
    private readonly LoadInputContext _inner;

    internal InteractionLoadingInput(LoadInputContext inner)
    {
        _inner = inner;
    }

    /// <summary>
    /// Gets the name of the input.
    /// </summary>
    /// <returns>The input name.</returns>
    [AspireExport]
    public string GetName()
    {
        return _inner.Input.Name;
    }

    /// <summary>
    /// Sets the choice options for the input.
    /// </summary>
    /// <param name="choices">The available choices, in display order. Each option pairs a submitted value with a display label.</param>
    [AspireExport]
    public void SetChoiceOptions(IReadOnlyList<InteractionChoiceOption> choices)
    {
        ArgumentNullException.ThrowIfNull(choices);

        // Honor cancellation so a stale load that was superseded by a newer one does not overwrite the input.
        _inner.CancellationToken.ThrowIfCancellationRequested();
        _inner.Input.Options = InteractionExports.ToOptionList(choices);
    }

    /// <summary>
    /// Sets the value of the input.
    /// </summary>
    /// <param name="value">The value to assign.</param>
    [AspireExport]
    public void SetValue(string? value)
    {
        _inner.CancellationToken.ThrowIfCancellationRequested();
        _inner.Input.Value = value;
    }
}

/// <summary>
/// A single selectable option for a choice input. Options are presented in the order supplied.
/// </summary>
[AspireDto]
internal sealed class InteractionChoiceOption
{
    /// <summary>
    /// Gets or sets the value submitted when this option is selected.
    /// </summary>
    public string Value { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the label displayed for this option.
    /// </summary>
    public string Label { get; set; } = string.Empty;
}

/// <summary>
/// Optional configuration shared by interaction input factory capabilities.
/// </summary>
[AspireDto]
internal sealed class CreateInteractionInputOptions
{
    /// <summary>
    /// Gets or sets the label for the input. Defaults to the input name when not specified.
    /// </summary>
    public string? Label { get; init; }

    /// <summary>
    /// Gets or sets the description for the input.
    /// </summary>
    public string? Description { get; init; }

    /// <summary>
    /// Gets or sets a value indicating whether the description is rendered as Markdown.
    /// </summary>
    public bool? EnableDescriptionMarkdown { get; init; }

    /// <summary>
    /// Gets or sets a value indicating whether the input is required.
    /// </summary>
    public bool? Required { get; init; }

    /// <summary>
    /// Gets or sets the placeholder text for the input.
    /// </summary>
    public string? Placeholder { get; init; }

    /// <summary>
    /// Gets or sets the initial value of the input.
    /// </summary>
    public string? Value { get; init; }

    /// <summary>
    /// Gets or sets a value indicating whether a custom choice is allowed. Only used by choice inputs.
    /// </summary>
    public bool? AllowCustomChoice { get; init; }

    /// <summary>
    /// Gets or sets a value indicating whether the input is disabled.
    /// </summary>
    public bool? Disabled { get; init; }

    /// <summary>
    /// Gets or sets the maximum length for text inputs.
    /// </summary>
    public int? MaxLength { get; init; }
}

/// <summary>
/// Options controlling when a dynamic-loading callback runs.
/// </summary>
[AspireDto]
internal sealed class DynamicLoadingOptions
{
    /// <summary>
    /// Gets or sets a value indicating whether the callback always runs at the start of the prompt.
    /// </summary>
    public bool? AlwaysLoadOnStart { get; init; }

    /// <summary>
    /// Gets or sets the names of inputs this input depends on. The callback runs when any of them change.
    /// </summary>
    public IReadOnlyList<string>? DependsOnInputs { get; init; }
}

/// <summary>
/// Options for message box and confirmation prompts.
/// </summary>
[AspireDto]
internal sealed class InteractionMessageBoxOptions
{
    /// <summary>
    /// Gets or sets the primary button text.
    /// </summary>
    public string? PrimaryButtonText { get; init; }

    /// <summary>
    /// Gets or sets the secondary button text.
    /// </summary>
    public string? SecondaryButtonText { get; init; }

    /// <summary>
    /// Gets or sets a value indicating whether the secondary button is shown.
    /// </summary>
    public bool? ShowSecondaryButton { get; init; }

    /// <summary>
    /// Gets or sets a value indicating whether the dismiss button is shown.
    /// </summary>
    public bool? ShowDismiss { get; init; }

    /// <summary>
    /// Gets or sets a value indicating whether Markdown in the message is rendered.
    /// </summary>
    public bool? EnableMessageMarkdown { get; init; }

    /// <summary>
    /// Gets or sets the intent of the message box.
    /// </summary>
    public MessageIntent? Intent { get; init; }

    internal MessageBoxInteractionOptions ToOptions()
    {
        return new MessageBoxInteractionOptions
        {
            PrimaryButtonText = PrimaryButtonText,
            SecondaryButtonText = SecondaryButtonText,
            ShowSecondaryButton = ShowSecondaryButton,
            ShowDismiss = ShowDismiss,
            EnableMessageMarkdown = EnableMessageMarkdown,
            Intent = Intent,
        };
    }
}

/// <summary>
/// Options for notification prompts.
/// </summary>
[AspireDto]
internal sealed class InteractionNotificationOptions
{
    /// <summary>
    /// Gets or sets the primary button text.
    /// </summary>
    public string? PrimaryButtonText { get; init; }

    /// <summary>
    /// Gets or sets the secondary button text.
    /// </summary>
    public string? SecondaryButtonText { get; init; }

    /// <summary>
    /// Gets or sets a value indicating whether the secondary button is shown.
    /// </summary>
    public bool? ShowSecondaryButton { get; init; }

    /// <summary>
    /// Gets or sets a value indicating whether the dismiss button is shown.
    /// </summary>
    public bool? ShowDismiss { get; init; }

    /// <summary>
    /// Gets or sets a value indicating whether Markdown in the message is rendered.
    /// </summary>
    public bool? EnableMessageMarkdown { get; init; }

    /// <summary>
    /// Gets or sets the intent of the notification.
    /// </summary>
    public MessageIntent? Intent { get; init; }

    /// <summary>
    /// Gets or sets the text for a link in the notification.
    /// </summary>
    public string? LinkText { get; init; }

    /// <summary>
    /// Gets or sets the URL for the link in the notification.
    /// </summary>
    public string? LinkUrl { get; init; }

    internal NotificationInteractionOptions ToOptions()
    {
        return new NotificationInteractionOptions
        {
            PrimaryButtonText = PrimaryButtonText,
            SecondaryButtonText = SecondaryButtonText,
            ShowSecondaryButton = ShowSecondaryButton,
            ShowDismiss = ShowDismiss,
            EnableMessageMarkdown = EnableMessageMarkdown,
            Intent = Intent,
            LinkText = LinkText,
            LinkUrl = LinkUrl,
        };
    }
}

/// <summary>
/// Options for inputs dialog prompts.
/// </summary>
[AspireDto]
internal sealed class InteractionInputsDialogOptions
{
    /// <summary>
    /// Gets or sets the primary button text.
    /// </summary>
    public string? PrimaryButtonText { get; init; }

    /// <summary>
    /// Gets or sets the secondary button text.
    /// </summary>
    public string? SecondaryButtonText { get; init; }

    /// <summary>
    /// Gets or sets a value indicating whether the secondary button is shown.
    /// </summary>
    public bool? ShowSecondaryButton { get; init; }

    /// <summary>
    /// Gets or sets a value indicating whether the dismiss button is shown.
    /// </summary>
    public bool? ShowDismiss { get; init; }

    /// <summary>
    /// Gets or sets a value indicating whether Markdown in the message is rendered.
    /// </summary>
    public bool? EnableMessageMarkdown { get; init; }

    /// <summary>
    /// Gets or sets a callback invoked to validate the inputs before the dialog is accepted. The callback
    /// receives a validation context that exposes the current inputs and can record validation errors.
    /// </summary>
    public Func<InputsDialogValidationContext, Task>? ValidationCallback { get; init; }

    internal InputsDialogInteractionOptions ToOptions()
    {
        return new InputsDialogInteractionOptions
        {
            PrimaryButtonText = PrimaryButtonText,
            SecondaryButtonText = SecondaryButtonText,
            ShowSecondaryButton = ShowSecondaryButton,
            ShowDismiss = ShowDismiss,
            EnableMessageMarkdown = EnableMessageMarkdown,
            ValidationCallback = ValidationCallback,
        };
    }
}

/// <summary>
/// The result of a boolean interaction prompt.
/// </summary>
[AspireDto]
internal sealed class BoolInteractionResult
{
    /// <summary>
    /// Gets a value indicating whether the interaction was canceled by the user.
    /// </summary>
    public required bool Canceled { get; init; }

    /// <summary>
    /// Gets the value returned from the interaction. Not meaningful when <see cref="Canceled"/> is <see langword="true"/>.
    /// </summary>
    public bool Value { get; init; }

    internal static BoolInteractionResult From(InteractionResult<bool> result)
    {
        return new BoolInteractionResult
        {
            Canceled = result.Canceled,
            Value = !result.Canceled && result.Data,
        };
    }
}

/// <summary>
/// The result of a single-input interaction prompt.
/// </summary>
[AspireDto]
internal sealed class InputInteractionResult
{
    /// <summary>
    /// Gets a value indicating whether the interaction was canceled by the user.
    /// </summary>
    public required bool Canceled { get; init; }

    /// <summary>
    /// Gets the input returned from the interaction. Not present when <see cref="Canceled"/> is <see langword="true"/>.
    /// </summary>
    public InteractionInput? Input { get; init; }

    internal static InputInteractionResult From(InteractionResult<InteractionInput> result)
    {
        return new InputInteractionResult
        {
            Canceled = result.Canceled,
            Input = result.Canceled || result.Data is null ? null : InteractionExports.ToResultInput(result.Data),
        };
    }
}

/// <summary>
/// The result of a multi-input interaction prompt.
/// </summary>
/// <remarks>
/// Modeled as a handle (not a by-value DTO) so the returned inputs are surfaced as the
/// <see cref="InteractionInputCollection"/> handle. That lets polyglot callers reuse the same name-based
/// accessors (for example <c>result.inputs().value("color")</c>) that the validation and command-argument
/// collections already expose, instead of having to scan a serialized array by hand.
/// </remarks>
[AspireExport(ExposeProperties = true)]
internal sealed class InputsInteractionResult
{
    /// <summary>
    /// Gets a value indicating whether the interaction was canceled by the user.
    /// </summary>
    public required bool Canceled { get; init; }

    /// <summary>
    /// Gets the inputs returned from the interaction. Empty when <see cref="Canceled"/> is <see langword="true"/>.
    /// </summary>
    public required InteractionInputCollection Inputs { get; init; }

    internal static InputsInteractionResult From(InteractionResult<InteractionInputCollection> result)
    {
        // The engine returns the live input instances, which still carry the non-serializable dynamic-loading
        // callback on DynamicLoading. Project onto callback-free copies (ToResultInput) before wrapping them in a
        // fresh collection so the handle can be enumerated/serialized safely after the prompt completes.
        var inputs = result.Canceled || result.Data is null
            ? new InteractionInputCollection([])
            : new InteractionInputCollection(result.Data.Select(InteractionExports.ToResultInput).ToArray());

        return new InputsInteractionResult
        {
            Canceled = result.Canceled,
            Inputs = inputs,
        };
    }
}
