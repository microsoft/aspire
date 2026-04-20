// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting;

#pragma warning disable ASPIREINTERACTION001 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.

/// <summary>
/// A service to interact with the current development environment.
/// </summary>
[Experimental(InteractionService.DiagnosticId, UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
public interface IInteractionService
{
    /// <summary>
    /// Gets a value indicating whether the interaction service is available. If <c>false</c>,
    /// this service is not available to interact with the user and service methods will throw
    /// an exception.
    /// </summary>
    bool IsAvailable { get; }

    /// <summary>
    /// Prompts the user for confirmation with a dialog.
    /// </summary>
    /// <param name="title">The title of the dialog.</param>
    /// <param name="message">The message to display in the dialog.</param>
    /// <param name="options">Optional configuration for the message box interaction.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>
    /// An <see cref="InteractionResult{T}"/> containing <c>true</c> if the user confirmed, <c>false</c> otherwise.
    /// </returns>
    Task<InteractionResult<bool>> PromptConfirmationAsync(string title, string message, MessageBoxInteractionOptions? options = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Prompts the user with a message box dialog.
    /// </summary>
    /// <param name="title">The title of the message box.</param>
    /// <param name="message">The message to display in the message box.</param>
    /// <param name="options">Optional configuration for the message box interaction.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>
    /// An <see cref="InteractionResult{T}"/> containing <c>true</c> if the user accepted, <c>false</c> otherwise.
    /// </returns>
    Task<InteractionResult<bool>> PromptMessageBoxAsync(string title, string message, MessageBoxInteractionOptions? options = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Prompts the user for a single text input.
    /// </summary>
    /// <param name="title">The title of the input dialog.</param>
    /// <param name="message">The message to display in the dialog.</param>
    /// <param name="inputLabel">The label for the input field.</param>
    /// <param name="placeHolder">The placeholder text for the input field.</param>
    /// <param name="options">Optional configuration for the input dialog interaction.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>
    /// An <see cref="InteractionResult{T}"/> containing the user's input.
    /// </returns>
    Task<InteractionResult<InteractionInput>> PromptInputAsync(string title, string? message, string inputLabel, string placeHolder, InputsDialogInteractionOptions? options = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Prompts the user for a single input using a specified <see cref="InteractionInput"/>.
    /// </summary>
    /// <param name="title">The title of the input dialog.</param>
    /// <param name="message">The message to display in the dialog.</param>
    /// <param name="input">The input configuration.</param>
    /// <param name="options">Optional configuration for the input dialog interaction.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>
    /// An <see cref="InteractionResult{T}"/> containing the user's input.
    /// </returns>
    Task<InteractionResult<InteractionInput>> PromptInputAsync(string title, string? message, InteractionInput input, InputsDialogInteractionOptions? options = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Prompts the user for multiple inputs.
    /// </summary>
    /// <param name="title">The title of the input dialog.</param>
    /// <param name="message">The message to display in the dialog.</param>
    /// <param name="inputs">A collection of <see cref="InteractionInput"/> to prompt for.</param>
    /// <param name="options">Optional configuration for the input dialog interaction.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>
    /// An <see cref="InteractionResult{T}"/> containing the user's inputs as an <see cref="InteractionInputCollection"/>.
    /// </returns>
    Task<InteractionResult<InteractionInputCollection>> PromptInputsAsync(string title, string? message, IReadOnlyList<InteractionInput> inputs, InputsDialogInteractionOptions? options = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Prompts the user with a notification.
    /// </summary>
    /// <param name="title">The title of the notification.</param>
    /// <param name="message">The message to display in the notification.</param>
    /// <param name="options">Optional configuration for the notification interaction.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>
    /// An <see cref="InteractionResult{T}"/> containing <c>true</c> if the user accepted, <c>false</c> otherwise.
    /// </returns>
    Task<InteractionResult<bool>> PromptNotificationAsync(string title, string message, NotificationInteractionOptions? options = null, CancellationToken cancellationToken = default);
}

internal record QueueLoadOptions(
    ILogger Logger,
    CancellationToken CancellationToken,
    InteractionInput Input,
    InteractionInputCollection AllInputs,
    IServiceProvider ServiceProvider);

internal sealed class InputLoadingState(InputLoadOptions options)
{
    private readonly InputLoadOptions _options = options;
    private readonly object _lock = new object();

    private Task? _currentTask;
    private CancellationTokenSource? _currentCts;
    private bool _isNextQueued;

    public bool Loading { get; private set; }

    public Action<InteractionInput>? OnLoadComplete { get; init; }

    public void QueueLoad(QueueLoadOptions options)
    {
        lock (_lock)
        {
            // Already queued but not yet started — ignore new call
            if (_isNextQueued)
            {
                return;
            }

            if (_currentTask == null || _currentTask.IsCompleted)
            {
                StartNewTask(options);
                return;
            }

            // A task is running — cancel and queue restart
            _currentCts?.Cancel();
            _isNextQueued = true;

            // Queue continuation once current completes
            _currentTask.ContinueWith(_ =>
            {
                lock (_lock)
                {
                    if (_isNextQueued)
                    {
                        _isNextQueued = false;
                        StartNewTask(options);
                    }
                }
            }, TaskScheduler.Default);
        }
    }

    private void StartNewTask(QueueLoadOptions options)
    {
        Debug.Assert(Monitor.IsEntered(_lock));

        Loading = true;

        _currentCts = CancellationTokenSource.CreateLinkedTokenSource(options.CancellationToken);
        var currentToken = _currentCts.Token;

        _currentTask = Task.Run(async () =>
        {
            try
            {
                await _options.LoadCallback(new LoadInputContext
                {
                    AllInputs = options.AllInputs,
                    Input = options.Input,
                    Services = options.ServiceProvider,
                    CancellationToken = currentToken
                }).ConfigureAwait(false);
                lock (_lock)
                {
                    Loading = false;
                }

                OnLoadComplete?.Invoke(options.Input);
            }
            catch (OperationCanceledException)
            {
                // Ignore.
            }
            catch (Exception ex)
            {
                options.Logger.LogError(ex, "Error loading options for input '{InputName}'.", options.Input.Name);
            }
        }, currentToken);
    }
}

/// <summary>
/// Represents configuration options for dynamically loading input data.
/// </summary>
/// <remarks>
/// Use this class to specify how and when dynamic input data should be loaded. This type is intended for advanced
/// scenarios where input loading behavior must be customized.
/// </remarks>
[Experimental(InteractionService.DiagnosticId, UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
public sealed class InputLoadOptions
{
    /// <summary>
    /// Gets the callback function that is invoked to perform a load operation using the specified input context.
    /// </summary>
    public required Func<LoadInputContext, Task> LoadCallback { get; init; }

    /// <summary>
    /// Gets a value indicating whether <see cref="LoadCallback"/> should always be executed at the start of the input prompt.
    /// </summary>
    /// <remarks>
    /// <see cref="LoadCallback"/> is executed at the start of the input prompt except when it depends on other inputs with <see cref="DependsOnInputs"/>.
    /// Setting this to <c>true</c> forces the load to always occur at the start of the prompt, regardless of dependencies.
    /// </remarks>
    public bool AlwaysLoadOnStart { get; init; }

    /// <summary>
    /// Gets the list of input names that this input depends on. <see cref="LoadCallback"/> is executed
    /// whenever any of the specified inputs change.
    /// </summary>
    public IReadOnlyList<string>? DependsOnInputs { get; init; }
}

/// <summary>
/// The context for dynamic input loading. Used with <see cref="InputLoadOptions.LoadCallback"/>.
/// </summary>
[Experimental(InteractionService.DiagnosticId, UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
public sealed class LoadInputContext
{
    /// <summary>
    /// Gets the loading input. This is the target of <see cref="InputLoadOptions"/>.
    /// </summary>
    public required InteractionInput Input { get; init; }

    /// <summary>
    /// Gets the collection of all <see cref="InteractionInput"/> in this prompt.
    /// </summary>
    public required InteractionInputCollection AllInputs { get; init; }

    /// <summary>
    /// Gets the service provider.
    /// </summary>
    public required IServiceProvider Services { get; init; }

    /// <summary>
    /// Gets the <see cref="CancellationToken"/>.
    /// </summary>
    public required CancellationToken CancellationToken { get; init; }
}

/// <summary>
/// Represents an input for an interaction.
/// </summary>
[Experimental(InteractionService.DiagnosticId, UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
[DebuggerDisplay("Name = {Name}, InputType = {InputType}, Required = {Required}, Value = {Value}")]
public sealed class InteractionInput
{
    internal string EffectiveLabel => string.IsNullOrWhiteSpace(Label) ? Name : Label;
    internal InputLoadingState? DynamicLoadingState { get; set; }
    internal List<string> ValidationErrors { get; } = [];

    /// <summary>
    /// Gets or sets the name for the input. Used for accessing inputs by name from a keyed collection.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// Gets or sets the label for the input. If not specified, the name will be used as the label.
    /// </summary>
    public string? Label { get; init; }

    /// <summary>
    /// Gets or sets the description for the input.
    /// </summary>
    public string? Description { get; init; }

    /// <summary>
    /// Gets or sets a value indicating whether the description should be rendered as Markdown.
    /// Setting this to <c>true</c> allows a description to contain Markdown elements such as links, text decoration and lists.
    /// </summary>
    public bool EnableDescriptionMarkdown { get; init; }

    /// <summary>
    /// Gets or sets the type of the input.
    /// </summary>
    public required InputType InputType { get; init; }

    /// <summary>
    /// Gets or sets a value indicating whether the input is required.
    /// </summary>
    public bool Required { get; init; }

    /// <summary>
    /// Gets or sets the options for the input. Only used by <see cref="InputType.Choice"/> inputs.
    /// </summary>
    public IReadOnlyList<KeyValuePair<string, string>>? Options { get; set; }

    /// <summary>
    /// Gets the <see cref="InputLoadOptions"/> for the input.
    /// Dynamic loading is used to load data and update inputs after a prompt has started.
    /// It can also be used to reload data and update inputs after a dependant input has changed.
    /// </summary>
    public InputLoadOptions? DynamicLoading { get; init; }

    /// <summary>
    /// Gets or sets the value of the input.
    /// </summary>
    public string? Value { get; set; }

    /// <summary>
    /// Gets the placeholder text for the input.
    /// </summary>
    public string? Placeholder { get; init; }

    /// <summary>
    /// Gets a value indicating whether a custom choice is allowed. Only used by <see cref="InputType.Choice"/> inputs.
    /// </summary>
    public bool AllowCustomChoice { get; init; }

    /// <summary>
    /// Gets or sets a value indicating whether a custom choice is allowed. Only used by <see cref="InputType.Choice"/> inputs.
    /// </summary>
    public bool Disabled { get; set; }

    /// <summary>
    /// gets or sets the maximum length for text inputs.
    /// </summary>
    public int? MaxLength
    {
        get => field;
        init
        {
            if (value is { } v)
            {
                ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(v, 0);
            }

            field = value;
        }
    }

    /// <summary>
    /// Gets or sets the maximum file size in bytes for <see cref="InputType.FileChooser"/> inputs.
    /// If not specified, defaults to 1 MB.
    /// </summary>
    public long? MaxFileSize
    {
        get => field;
        init
        {
            if (value is { } v)
            {
                ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(v, 0);
            }

            field = value;
        }
    }
}

/// <summary>
/// A collection of interaction inputs that supports both indexed and name-based access.
/// </summary>
[Experimental(InteractionService.DiagnosticId, UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
[DebuggerDisplay("Count = {Count}")]
public sealed class InteractionInputCollection : IReadOnlyList<InteractionInput>
{
    private readonly List<InteractionInput> _inputs;
    private readonly Dictionary<string, InteractionInput> _inputsByName;

    /// <summary>
    /// Initializes a new instance of the <see cref="InteractionInputCollection"/> class.
    /// </summary>
    /// <param name="inputs">The collection of interaction inputs to wrap.</param>
    public InteractionInputCollection(IReadOnlyList<InteractionInput> inputs)
    {
        var inputsByName = new Dictionary<string, InteractionInput>(StringComparers.InteractionInputName);
        var usedNames = new HashSet<string>(StringComparers.InteractionInputName);

        // Check for duplicate names
        foreach (var input in inputs)
        {
            if (!usedNames.Add(input.Name))
            {
                throw new InvalidOperationException($"Duplicate input name '{input.Name}' found. Input names must be unique.");
            }
            inputsByName[input.Name] = input;
        }

        _inputs = inputs.ToList();
        _inputsByName = inputsByName;
    }

    /// <summary>
    /// Gets an input by its name.
    /// </summary>
    /// <param name="name">The name of the input.</param>
    /// <returns>The input with the specified name.</returns>
    /// <exception cref="KeyNotFoundException">Thrown when no input with the specified name exists.</exception>
    public InteractionInput this[string name]
    {
        get
        {
            if (_inputsByName.TryGetValue(name, out var input))
            {
                return input;
            }
            throw new KeyNotFoundException($"No input with name '{name}' was found.");
        }
    }

    /// <summary>
    /// Gets an input by its index.
    /// </summary>
    /// <param name="index">The zero-based index of the input.</param>
    /// <returns>The input at the specified index.</returns>
    public InteractionInput this[int index] => _inputs[index];

    /// <summary>
    /// Gets the number of inputs in the collection.
    /// </summary>
    public int Count => _inputs.Count;

    /// <summary>
    /// Tries to get an input by its name.
    /// </summary>
    /// <param name="name">The name of the input.</param>
    /// <param name="input">When this method returns, contains the input with the specified name, if found; otherwise, null.</param>
    /// <returns>true if an input with the specified name was found; otherwise, false.</returns>
    public bool TryGetByName(string name, [NotNullWhen(true)] out InteractionInput? input)
    {
        return _inputsByName.TryGetValue(name, out input);
    }

    /// <summary>
    /// Determines whether the collection contains an input with the specified name.
    /// </summary>
    /// <param name="name">The name to locate in the collection.</param>
    /// <returns>true if the collection contains an input with the specified name; otherwise, false.</returns>
    public bool ContainsName(string name)
    {
        return _inputsByName.ContainsKey(name);
    }

    /// <summary>
    /// Gets the names of all inputs in the collection.
    /// </summary>
    public IEnumerable<string> Names => _inputsByName.Keys;

    /// <summary>
    /// Returns an enumerator that iterates through the collection.
    /// </summary>
    /// <returns>An enumerator that can be used to iterate through the collection.</returns>
    public IEnumerator<InteractionInput> GetEnumerator() => _inputs.GetEnumerator();

    /// <summary>
    /// Returns an enumerator that iterates through the collection.
    /// </summary>
    /// <returns>An enumerator that can be used to iterate through the collection.</returns>
    IEnumerator IEnumerable.GetEnumerator() => _inputs.GetEnumerator();

    internal int IndexOf(InteractionInput input) => _inputs.IndexOf(input);
}

/// <summary>
/// Specifies the type of input for an <see cref="InteractionInput"/>.
/// </summary>
[Experimental(InteractionService.DiagnosticId, UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
public enum InputType
{
    /// <summary>
    /// A single-line text input.
    /// </summary>
    Text,
    /// <summary>
    /// A secure text input.
    /// </summary>
    SecretText,
    /// <summary>
    /// A choice input. Selects from a list of options.
    /// </summary>
    Choice,
    /// <summary>
    /// A boolean input.
    /// </summary>
    Boolean,
    /// <summary>
    /// A numeric input.
    /// </summary>
    Number,
    /// <summary>
    /// A file chooser input. Allows the user to select a file using the OS/browser file picker.
    /// </summary>
    FileChooser
}

/// <summary>
/// Options for configuring an inputs dialog interaction.
/// </summary>
[Experimental(InteractionService.DiagnosticId, UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
public class InputsDialogInteractionOptions : InteractionOptions
{
    internal static new InputsDialogInteractionOptions Default { get; } = new();

    /// <summary>
    /// Gets or sets the validation callback for the inputs dialog. This callback is invoked when the user submits the dialog.
    /// If validation errors are added to the <see cref="InputsDialogValidationContext"/>, the dialog will not close and the user will be prompted to correct the errors.
    /// </summary>
    public Func<InputsDialogValidationContext, Task>? ValidationCallback { get; set; }
}

/// <summary>
/// Represents the context for validating inputs in an inputs dialog interaction.
/// </summary>
[Experimental(InteractionService.DiagnosticId, UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
public sealed class InputsDialogValidationContext
{
    internal bool HasErrors { get; private set; }

    /// <summary>
    /// Gets the inputs that are being validated.
    /// </summary>
    public required InteractionInputCollection Inputs { get; init; }

    /// <summary>
    /// Gets the cancellation token for the validation operation.
    /// </summary>
    public required CancellationToken CancellationToken { get; init; }

    /// <summary>
    /// Gets the service provider for resolving services during validation.
    /// </summary>
    public required IServiceProvider Services { get; init; }

    /// <summary>
    /// Adds a validation error for the specified input.
    /// </summary>
    /// <param name="input">The input to add a validation error for.</param>
    /// <param name="errorMessage">The error message to add.</param>
    public void AddValidationError(InteractionInput input, string errorMessage)
    {
        ArgumentNullException.ThrowIfNull(input, nameof(input));

        if (string.IsNullOrEmpty(errorMessage))
        {
            throw new ArgumentException("Error message cannot be null or empty.", nameof(errorMessage));
        }

        input.ValidationErrors.Add(errorMessage);
        HasErrors = true;
    }
}

/// <summary>
/// Options for configuring a message box interaction.
/// </summary>
[Experimental(InteractionService.DiagnosticId, UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
public class MessageBoxInteractionOptions : InteractionOptions
{
    internal static MessageBoxInteractionOptions CreateDefault() => new();

    /// <summary>
    /// Gets or sets the intent of the message box.
    /// </summary>
    public MessageIntent? Intent { get; set; }
}

/// <summary>
/// Options for configuring a notification interaction.
/// </summary>
[Experimental(InteractionService.DiagnosticId, UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
public class NotificationInteractionOptions : InteractionOptions
{
    internal static NotificationInteractionOptions CreateDefault() => new();

    /// <summary>
    /// Gets or sets the intent of the notification.
    /// </summary>
    public MessageIntent? Intent { get; set; }

    /// <summary>
    /// Gets or sets the text for a link in the notification.
    /// </summary>
    public string? LinkText { get; set; }

    /// <summary>
    /// Gets or sets the URL for the link in the notification.
    /// </summary>
    public string? LinkUrl { get; set; }
}

/// <summary>
/// Specifies the intent or purpose of a message in an interaction.
/// </summary>
[Experimental(InteractionService.DiagnosticId, UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
public enum MessageIntent
{
    /// <summary>
    /// No specific intent.
    /// </summary>
    None = 0,
    /// <summary>
    /// Indicates a successful operation.
    /// </summary>
    Success = 1,
    /// <summary>
    /// Indicates a warning.
    /// </summary>
    Warning = 2,
    /// <summary>
    /// Indicates an error.
    /// </summary>
    Error = 3,
    /// <summary>
    /// Provides informational content.
    /// </summary>
    Information = 4,
    /// <summary>
    /// Requests confirmation from the user.
    /// </summary>
    Confirmation = 5
}

/// <summary>
/// Optional configuration for interactions added with <see cref="InteractionService"/>.
/// </summary>
[Experimental(InteractionService.DiagnosticId, UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
public class InteractionOptions
{
    internal static InteractionOptions Default { get; } = new();

    /// <summary>
    /// Optional primary button text to override the default text.
    /// </summary>
    public string? PrimaryButtonText { get; set; }

    /// <summary>
    /// Optional secondary button text to override the default text.
    /// </summary>
    public string? SecondaryButtonText { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether show the secondary button.
    /// </summary>
    public bool? ShowSecondaryButton { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether show the dismiss button.
    /// </summary>
    public bool? ShowDismiss { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether Markdown in the message is rendered.
    /// Setting this to <c>true</c> allows a message to contain Markdown elements such as links, text decoration and lists.
    /// </summary>
    public bool? EnableMessageMarkdown { get; set; }
}

/// <summary>
/// Provides a set of static methods for the <see cref="InteractionResult{T}"/>.
/// </summary>
public static class InteractionResult
{
    /// <summary>
    /// Creates a new <see cref="InteractionResult{T}"/> with the specified result and a flag indicating that the interaction was not canceled.
    /// </summary>
    /// <typeparam name="T">The type of the data associated with the interaction result.</typeparam>
    /// <param name="result">The data returned from the interaction.</param>
    /// <returns>The new <see cref="InteractionResult{T}"/>.</returns>
    public static InteractionResult<T> Ok<T>(T result)
    {
        return new InteractionResult<T>(result, canceled: false);
    }

    /// <summary>
    /// Creates an <see cref="InteractionResult{T}"/> indicating a canceled interaction.
    /// </summary>
    /// <typeparam name="T">The type of the data associated with the interaction result.</typeparam>
    /// <param name="data">Optional data to include with the interaction result. Defaults to the default value of type <typeparamref
    /// name="T"/> if not provided.</param>
    /// <returns>
    /// An <see cref="InteractionResult{T}"/> with the <c>canceled</c> flag set to <see langword="true"/> and containing
    /// the specified data.
    /// </returns>
    public static InteractionResult<T> Cancel<T>(T? data = default)
    {
        return new InteractionResult<T>(data ?? default, canceled: true);
    }
}

/// <summary>
/// Represents the result of an interaction.
/// </summary>
[Experimental(InteractionService.DiagnosticId, UrlFormat = "https://aka.ms/aspire/diagnostics/{0}")]
public class InteractionResult<T>
{
    /// <summary>
    /// The data returned from the interaction. Won't have a useful value if the interaction was canceled.
    /// </summary>
    public T? Data { get; }

    /// <summary>
    /// A flag indicating whether the interaction was canceled by the user.
    /// </summary>
    [MemberNotNullWhen(false, nameof(Data))]
    public bool Canceled { get; }

    internal InteractionResult(T? data, bool canceled)
    {
        Data = data;
        Canceled = canceled;
    }
}

#pragma warning restore ASPIREINTERACTION001 // Type is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.
