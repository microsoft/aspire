// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREINTERACTION001

using Microsoft.Extensions.DependencyInjection;
using PublicInputsDialogInteractionOptions = Aspire.Hosting.InputsDialogInteractionOptions;
using PublicInteractionOptions = Aspire.Hosting.InteractionOptions;
using PublicMessageBoxInteractionOptions = Aspire.Hosting.MessageBoxInteractionOptions;
using PublicNotificationInteractionOptions = Aspire.Hosting.NotificationInteractionOptions;

namespace Aspire.Hosting.Ats;

/// <summary>
/// ATS exports for interaction service operations.
/// </summary>
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
    /// Gets a value indicating whether the interaction service is available.
    /// </summary>
    /// <param name="interactionService">The interaction service handle.</param>
    /// <returns><see langword="true"/> when the interaction service can interact with the user; otherwise, <see langword="false"/>.</returns>
    [AspireExport("interactionServiceIsAvailable", MethodName = "isAvailable")]
    public static bool IsAvailable(this IInteractionService interactionService)
    {
        ArgumentNullException.ThrowIfNull(interactionService);

        return interactionService.IsAvailable;
    }

    /// <summary>
    /// Prompts the user for confirmation with a dialog.
    /// </summary>
    /// <param name="interactionService">The interaction service handle.</param>
    /// <param name="title">The title of the dialog.</param>
    /// <param name="message">The message to display in the dialog.</param>
    /// <param name="options">Optional configuration for the message box interaction.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The confirmation interaction result.</returns>
    [AspireExport]
    public static async Task<BooleanInteractionResult> PromptConfirmationAsync(
        this IInteractionService interactionService,
        string title,
        string message,
        MessageBoxInteractionOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(interactionService);

        var result = await interactionService.PromptConfirmationAsync(
            title,
            message,
            options?.ToMessageBoxInteractionOptions(),
            cancellationToken).ConfigureAwait(false);

        return BooleanInteractionResult.FromInteractionResult(result);
    }

    /// <summary>
    /// Prompts the user with a message box dialog.
    /// </summary>
    /// <param name="interactionService">The interaction service handle.</param>
    /// <param name="title">The title of the message box.</param>
    /// <param name="message">The message to display in the message box.</param>
    /// <param name="options">Optional configuration for the message box interaction.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The message box interaction result.</returns>
    [AspireExport]
    public static async Task<BooleanInteractionResult> PromptMessageBoxAsync(
        this IInteractionService interactionService,
        string title,
        string message,
        MessageBoxInteractionOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(interactionService);

        var result = await interactionService.PromptMessageBoxAsync(
            title,
            message,
            options?.ToMessageBoxInteractionOptions(),
            cancellationToken).ConfigureAwait(false);

        return BooleanInteractionResult.FromInteractionResult(result);
    }

    /// <summary>
    /// Prompts the user for a single text input.
    /// </summary>
    /// <param name="interactionService">The interaction service handle.</param>
    /// <param name="title">The title of the input dialog.</param>
    /// <param name="message">The message to display in the dialog.</param>
    /// <param name="inputLabel">The label for the input field.</param>
    /// <param name="placeHolder">The placeholder text for the input field.</param>
    /// <param name="options">Optional configuration for the input dialog interaction.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The input interaction result.</returns>
    [AspireExport]
    public static async Task<InputInteractionResult> PromptInputAsync(
        this IInteractionService interactionService,
        string title,
        string? message,
        string inputLabel,
        string placeHolder,
        InputsDialogInteractionOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(interactionService);

        var result = await interactionService.PromptInputAsync(
            title,
            message,
            inputLabel,
            placeHolder,
            options?.ToInputsDialogInteractionOptions(),
            cancellationToken).ConfigureAwait(false);

        return InputInteractionResult.FromInteractionResult(result);
    }

    /// <summary>
    /// Prompts the user for a single input using a specified <see cref="InteractionInput"/>.
    /// </summary>
    /// <param name="interactionService">The interaction service handle.</param>
    /// <param name="title">The title of the input dialog.</param>
    /// <param name="message">The message to display in the dialog.</param>
    /// <param name="input">The input configuration.</param>
    /// <param name="options">Optional configuration for the input dialog interaction.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The input interaction result.</returns>
    [AspireExport("promptInputWithInput", MethodName = "promptInputWithInputAsync")]
    public static async Task<InputInteractionResult> PromptInputWithInputAsync(
        this IInteractionService interactionService,
        string title,
        string? message,
        InteractionInput input,
        InputsDialogInteractionOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(interactionService);

        var result = await interactionService.PromptInputAsync(
            title,
            message,
            input,
            options?.ToInputsDialogInteractionOptions(),
            cancellationToken).ConfigureAwait(false);

        return InputInteractionResult.FromInteractionResult(result);
    }

    /// <summary>
    /// Prompts the user for multiple inputs.
    /// </summary>
    /// <param name="interactionService">The interaction service handle.</param>
    /// <param name="title">The title of the input dialog.</param>
    /// <param name="message">The message to display in the dialog.</param>
    /// <param name="inputs">The input configurations.</param>
    /// <param name="options">Optional configuration for the input dialog interaction.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The inputs interaction result.</returns>
    [AspireExport]
    public static async Task<InputsInteractionResult> PromptInputsAsync(
        this IInteractionService interactionService,
        string title,
        string? message,
        IReadOnlyList<InteractionInput> inputs,
        InputsDialogInteractionOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(interactionService);

        var result = await interactionService.PromptInputsAsync(
            title,
            message,
            inputs,
            options?.ToInputsDialogInteractionOptions(),
            cancellationToken).ConfigureAwait(false);

        return InputsInteractionResult.FromInteractionResult(result);
    }

    /// <summary>
    /// Prompts the user with a notification.
    /// </summary>
    /// <param name="interactionService">The interaction service handle.</param>
    /// <param name="title">The title of the notification.</param>
    /// <param name="message">The message to display in the notification.</param>
    /// <param name="options">Optional configuration for the notification interaction.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    /// <returns>The notification interaction result.</returns>
    [AspireExport]
    public static async Task<BooleanInteractionResult> PromptNotificationAsync(
        this IInteractionService interactionService,
        string title,
        string message,
        NotificationInteractionOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(interactionService);

        var result = await interactionService.PromptNotificationAsync(
            title,
            message,
            options?.ToNotificationInteractionOptions(),
            cancellationToken).ConfigureAwait(false);

        return BooleanInteractionResult.FromInteractionResult(result);
    }
}

/// <summary>
/// Shared polyglot options for interaction prompts.
/// </summary>
[AspireDto]
internal class InteractionOptions
{
    /// <summary>
    /// Optional primary button text to override the default text.
    /// </summary>
    public string? PrimaryButtonText { get; init; }

    /// <summary>
    /// Optional secondary button text to override the default text.
    /// </summary>
    public string? SecondaryButtonText { get; init; }

    /// <summary>
    /// Gets a value indicating whether to show the secondary button.
    /// </summary>
    public bool? ShowSecondaryButton { get; init; }

    /// <summary>
    /// Gets a value indicating whether to show the dismiss button.
    /// </summary>
    public bool? ShowDismiss { get; init; }

    /// <summary>
    /// Gets a value indicating whether Markdown in the message is rendered.
    /// </summary>
    public bool? EnableMessageMarkdown { get; init; }

    internal void ApplyTo(PublicInteractionOptions options)
    {
        options.PrimaryButtonText = PrimaryButtonText;
        options.SecondaryButtonText = SecondaryButtonText;
        options.ShowSecondaryButton = ShowSecondaryButton;
        options.ShowDismiss = ShowDismiss;
        options.EnableMessageMarkdown = EnableMessageMarkdown;
    }
}

/// <summary>
/// Polyglot options for message box interactions.
/// </summary>
[AspireDto]
internal sealed class MessageBoxInteractionOptions : InteractionOptions
{
    /// <summary>
    /// Gets the intent of the message box.
    /// </summary>
    public MessageIntent? Intent { get; init; }

    internal PublicMessageBoxInteractionOptions ToMessageBoxInteractionOptions()
    {
        var options = new PublicMessageBoxInteractionOptions
        {
            Intent = Intent
        };

        ApplyTo(options);
        return options;
    }
}

/// <summary>
/// Polyglot options for inputs dialog interactions.
/// </summary>
[AspireDto]
internal sealed class InputsDialogInteractionOptions : InteractionOptions
{
    /// <summary>
    /// Gets the validation callback for the inputs dialog.
    /// </summary>
    public Func<InputsDialogValidationContext, Task>? ValidationCallback { get; init; }

    internal PublicInputsDialogInteractionOptions ToInputsDialogInteractionOptions()
    {
        var options = new PublicInputsDialogInteractionOptions
        {
            ValidationCallback = ValidationCallback
        };

        ApplyTo(options);
        return options;
    }
}

/// <summary>
/// Polyglot options for notification interactions.
/// </summary>
[AspireDto]
internal sealed class NotificationInteractionOptions : InteractionOptions
{
    /// <summary>
    /// Gets the intent of the notification.
    /// </summary>
    public MessageIntent? Intent { get; init; }

    /// <summary>
    /// Gets the text for a link in the notification.
    /// </summary>
    public string? LinkText { get; init; }

    /// <summary>
    /// Gets the URL for the link in the notification.
    /// </summary>
    public string? LinkUrl { get; init; }

    internal PublicNotificationInteractionOptions ToNotificationInteractionOptions()
    {
        var options = new PublicNotificationInteractionOptions
        {
            Intent = Intent,
            LinkText = LinkText,
            LinkUrl = LinkUrl
        };

        ApplyTo(options);
        return options;
    }
}

/// <summary>
/// The result of a boolean interaction prompt.
/// </summary>
[AspireDto]
internal sealed class BooleanInteractionResult
{
    /// <summary>
    /// The data returned from the interaction. The value is <see langword="null"/> when the interaction was canceled.
    /// </summary>
    public bool? Data { get; init; }

    /// <summary>
    /// A flag indicating whether the interaction was canceled by the user.
    /// </summary>
    public required bool Canceled { get; init; }

    internal static BooleanInteractionResult FromInteractionResult(InteractionResult<bool> result)
    {
        return new BooleanInteractionResult
        {
            Data = result.Canceled ? null : result.Data,
            Canceled = result.Canceled
        };
    }
}

/// <summary>
/// The result of a single input interaction prompt.
/// </summary>
[AspireDto]
internal sealed class InputInteractionResult
{
    /// <summary>
    /// The data returned from the interaction. The value is <see langword="null"/> when the interaction was canceled.
    /// </summary>
    public InteractionInput? Data { get; init; }

    /// <summary>
    /// A flag indicating whether the interaction was canceled by the user.
    /// </summary>
    public required bool Canceled { get; init; }

    internal static InputInteractionResult FromInteractionResult(InteractionResult<InteractionInput> result)
    {
        return new InputInteractionResult
        {
            Data = result.Canceled ? null : result.Data,
            Canceled = result.Canceled
        };
    }
}

/// <summary>
/// The result of a multi-input interaction prompt.
/// </summary>
[AspireDto]
internal sealed class InputsInteractionResult
{
    /// <summary>
    /// The data returned from the interaction. The value is <see langword="null"/> when the interaction was canceled.
    /// </summary>
    public InteractionInputCollection? Data { get; init; }

    /// <summary>
    /// A flag indicating whether the interaction was canceled by the user.
    /// </summary>
    public required bool Canceled { get; init; }

    internal static InputsInteractionResult FromInteractionResult(InteractionResult<InteractionInputCollection> result)
    {
        return new InputsInteractionResult
        {
            Data = result.Canceled ? null : result.Data,
            Canceled = result.Canceled
        };
    }
}
