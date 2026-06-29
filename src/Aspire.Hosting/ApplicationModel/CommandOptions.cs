// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Hosting.ApplicationModel;

/// <summary>
/// Optional configuration for resource commands added with <see cref="ResourceBuilderExtensions.WithCommand{T}(Aspire.Hosting.ApplicationModel.IResourceBuilder{T}, string, string, Func{Aspire.Hosting.ApplicationModel.ExecuteCommandContext, Task{Aspire.Hosting.ApplicationModel.ExecuteCommandResult}}, Aspire.Hosting.ApplicationModel.CommandOptions?)"/>.
/// </summary>
/// <ats-summary>Optional configuration for resource commands.</ats-summary>
[AspireDto]
public class CommandOptions
{
    private IReadOnlyList<InteractionInput> _arguments = [];

    internal static CommandOptions Default { get; } = new();

    /// <summary>
    /// Optional description of the command, to be shown in the UI.
    /// Could be used as a tooltip. May be localized.
    /// </summary>
    public string? Description { get; set; }

    /// <summary>
    /// Obsolete optional parameter that configures the command in some way.
    /// Clients must return any value provided by the server when invoking the command.
    /// </summary>
    [Obsolete("Use Arguments to describe invocation arguments and ExecuteCommandContext.Arguments to read them.")]
    public object? Parameter { get; set; }

    /// <summary>
    /// Gets or sets the invocation arguments accepted by the command.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The list order is part of the command contract. CLI positional arguments are mapped to this list by index before the
    /// command executes. Clients that submit named argument payloads, such as Dashboard and MCP clients, map values by
    /// <see cref="InteractionInput.Name"/>.
    /// </para>
    /// </remarks>
    public IReadOnlyList<InteractionInput> Arguments
    {
        get => _arguments;
        set => _arguments = value ?? [];
    }

    /// <summary>
    /// Gets or sets the callback that validates invocation arguments before the command callback is executed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// When validation errors are added to the <see cref="InputsDialogValidationContext"/>, the command callback is not
    /// executed. Dashboard clients can display the errors next to the matching inputs, while API clients can report the same
    /// errors to callers.
    /// </para>
    /// </remarks>
    public Func<InputsDialogValidationContext, Task>? ValidateArguments { get; set; }

    /// <summary>
    /// Gets or sets where the command is visible to users and clients.
    /// </summary>
    /// <remarks>
    /// UI clients use the <see cref="ResourceCommandVisibility.UI"/> flag when displaying commands, and API
    /// clients use the <see cref="ResourceCommandVisibility.Api"/> flag when discovering commands. Visibility controls
    /// discovery and display, not authorization. Use <see cref="ResourceCommandVisibility.Api"/> without
    /// <see cref="ResourceCommandVisibility.UI"/> for headless or agent-oriented commands that should not be displayed
    /// in the dashboard UI.
    /// </remarks>
    public ResourceCommandVisibility Visibility { get; set; } = ResourceCommandVisibility.UI | ResourceCommandVisibility.Api;

    /// <summary>
    /// When a confirmation message is specified, the UI will prompt with an OK/Cancel dialog
    /// and the confirmation message before starting the command.
    /// </summary>
    public string? ConfirmationMessage { get; set; }

    /// <summary>
    /// The icon name for the command. The name should be a valid FluentUI icon name from <see href="https://aka.ms/fluentui-system-icons"/>.
    /// </summary>
    public string? IconName { get; set; }

    /// <summary>
    /// The icon variant.
    /// </summary>
    public IconVariant? IconVariant { get; set; }

    /// <summary>
    /// A flag indicating whether the command is highlighted in the UI.
    /// </summary>
    public bool IsHighlighted { get; set; }

    /// <summary>
    /// <para>A callback that is used to update the command state. The callback is executed when the command's resource snapshot is updated.</para>
    /// <para>If a callback isn't specified, the command is always enabled.</para>
    /// </summary>
    public Func<UpdateCommandStateContext, ResourceCommandState>? UpdateState { get; set; }

    /// <summary>
    /// Gets or sets options for displaying a progress dialog while the command is executing.
    /// </summary>
    /// <remarks>
    /// <para>
    /// When <see cref="CommandProgressOptions.Message"/> is not <see langword="null"/> or empty, a progress dialog
    /// is automatically shown while the command callback executes. The dialog closes when the command completes.
    /// </para>
    /// <para>
    /// When <see langword="null"/>, or when <see cref="CommandProgressOptions.Message"/> is <see langword="null"/> or empty,
    /// no progress dialog is shown and the command executes without visual feedback.
    /// </para>
    /// </remarks>
    public CommandProgressOptions? Progress { get; set; }
}

/// <summary>
/// Options for displaying a progress dialog while a command is executing.
/// </summary>
[AspireDto]
public class CommandProgressOptions
{
    /// <summary>
    /// Gets or sets the message to display in the progress dialog.
    /// </summary>
    /// <remarks>
    /// When not <see langword="null"/> or empty, a progress dialog is displayed while the command executes.
    /// </remarks>
    public string? Message { get; set; }

    /// <summary>
    /// Gets or sets the optional title of the progress dialog.
    /// </summary>
    public string? Title { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the cancel button is hidden in the progress dialog.
    /// </summary>
    /// <remarks>
    /// When <see langword="false"/> (the default), a cancel button is shown. Clicking it cancels the command via the
    /// <see cref="ExecuteCommandContext.CancellationToken"/>.
    /// When <see langword="true"/>, no cancel button is displayed and the user cannot cancel the operation from the dialog.
    /// </remarks>
    public bool HideCancelButton { get; set; }
}
