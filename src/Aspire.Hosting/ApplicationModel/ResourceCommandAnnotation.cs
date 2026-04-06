// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Aspire.Hosting.ApplicationModel;

/// <summary>
/// Represents a command annotation for a resource.
/// </summary>
[DebuggerDisplay("Type = {GetType().Name,nq}, Name = {Name}")]
public sealed class ResourceCommandAnnotation : IResourceAnnotation
{
    /// <summary>
    /// Initializes a new instance of the <see cref="ResourceCommandAnnotation"/> class.
    /// </summary>
    public ResourceCommandAnnotation(
        string name,
        string displayName,
        Func<UpdateCommandStateContext, ResourceCommandState> updateState,
        Func<ExecuteCommandContext, Task<ExecuteCommandResult>> executeCommand,
        string? displayDescription,
        object? parameter,
        string? confirmationMessage,
        string? iconName,
        IconVariant? iconVariant,
        bool isHighlighted)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(displayName);
        ArgumentNullException.ThrowIfNull(updateState);
        ArgumentNullException.ThrowIfNull(executeCommand);

        Name = name;
        DisplayName = displayName;
        UpdateState = updateState;
        ExecuteCommand = executeCommand;
        DisplayDescription = displayDescription;
        Parameter = parameter;
        ConfirmationMessage = confirmationMessage;
        IconName = iconName;
        IconVariant = iconVariant;
        IsHighlighted = isHighlighted;
    }

    /// <summary>
    /// The name of command. The name uniquely identifies the command.
    /// </summary>
    public string Name { get; }

    /// <summary>
    /// The display name visible in UI.
    /// </summary>
    public string DisplayName { get; }

    /// <summary>
    /// A callback that is used to update the command state.
    /// The callback is executed when the command's resource snapshot is updated.
    /// </summary>
    public Func<UpdateCommandStateContext, ResourceCommandState> UpdateState { get; }

    /// <summary>
    /// A callback that is executed when the command is executed.
    /// The result is used to indicate success or failure in the UI.
    /// </summary>
    public Func<ExecuteCommandContext, Task<ExecuteCommandResult>> ExecuteCommand { get; }

    /// <summary>
    /// Optional description of the command, to be shown in the UI.
    /// Could be used as a tooltip. May be localized.
    /// </summary>
    public string? DisplayDescription { get; }

    /// <summary>
    /// Optional parameter that configures the command in some way.
    /// Clients must return any value provided by the server when invoking the command.
    /// </summary>
    public object? Parameter { get; }

    /// <summary>
    /// When a confirmation message is specified, the UI will prompt with an OK/Cancel dialog
    /// and the confirmation message before starting the command.
    /// </summary>
    public string? ConfirmationMessage { get; }

    /// <summary>
    /// The icon name for the command. The name should be a valid FluentUI icon name. https://aka.ms/fluentui-system-icons
    /// </summary>
    public string? IconName { get; }

    /// <summary>
    /// The icon variant for the command.
    /// </summary>
    public IconVariant? IconVariant { get; }

    /// <summary>
    /// A flag indicating whether the command is highlighted in the UI.
    /// </summary>
    public bool IsHighlighted { get; }
}

/// <summary>
/// The icon variant.
/// </summary>
public enum IconVariant
{
    /// <summary>
    /// Regular variant of icons.
    /// </summary>
    Regular,
    /// <summary>
    /// Filled variant of icons.
    /// </summary>
    Filled
}

/// <summary>
/// Specifies the format of a command result.
/// </summary>
public enum CommandResultFormat
{
    /// <summary>
    /// Plain text result.
    /// </summary>
    Text,

    /// <summary>
    /// JSON result.
    /// </summary>
    Json
}

/// <summary>
/// A factory for <see cref="ExecuteCommandResult"/>.
/// </summary>
public static class CommandResults
{
    /// <summary>
    /// Produces a success result.
    /// </summary>
    public static ExecuteCommandResult Success() => new() { Success = true };

    /// <summary>
    /// Produces a success result with result data.
    /// </summary>
    /// <param name="result">The result data.</param>
    /// <param name="resultFormat">The format of the result data. Defaults to <see cref="CommandResultFormat.Text"/>.</param>
    public static ExecuteCommandResult Success(string result, CommandResultFormat resultFormat = CommandResultFormat.Text) => new() { Success = true, Result = result, ResultFormat = resultFormat };

    /// <summary>
    /// Produces an unsuccessful result with an error message.
    /// </summary>
    /// <param name="errorMessage">An optional error message.</param>
    public static ExecuteCommandResult Failure(string? errorMessage = null) => new() { Success = false, ErrorMessage = errorMessage };

    /// <summary>
    /// Produces an unsuccessful result with an error message and result data.
    /// </summary>
    /// <param name="errorMessage">The error message.</param>
    /// <param name="result">The result data.</param>
    /// <param name="resultFormat">The format of the result data. Defaults to <see cref="CommandResultFormat.Text"/>.</param>
    public static ExecuteCommandResult Failure(string errorMessage, string result, CommandResultFormat resultFormat = CommandResultFormat.Text) => new() { Success = false, ErrorMessage = errorMessage, Result = result, ResultFormat = resultFormat };

    /// <summary>
    /// Produces a canceled result.
    /// </summary>
    public static ExecuteCommandResult Canceled() => new() { Success = false, Canceled = true };

    /// <summary>
    /// Produces an unsuccessful result from an <see cref="Exception"/>. <see cref="Exception.Message"/> is used as the error message.
    /// </summary>
    /// <param name="exception">The exception to get the error message from.</param>
    public static ExecuteCommandResult Failure(Exception exception) => Failure(exception.Message);
}

/// <summary>
/// The result of executing a command. Returned from <see cref="ResourceCommandAnnotation.ExecuteCommand"/>.
/// </summary>
[AspireDto]
public sealed class ExecuteCommandResult
{
    /// <summary>
    /// A flag that indicates whether the command was successful.
    /// </summary>
    public required bool Success { get; init; }

    /// <summary>
    /// A flag that indicates whether the command was canceled by the user.
    /// </summary>
    public bool Canceled { get; init; }

    /// <summary>
    /// An optional error message that can be set when the command is unsuccessful.
    /// </summary>
    public string? ErrorMessage { get; init; }

    /// <summary>
    /// An optional result value produced by the command.
    /// </summary>
    public string? Result { get; init; }

    /// <summary>
    /// The format of the <see cref="Result"/> value.
    /// </summary>
    public CommandResultFormat? ResultFormat { get; init; }
}

/// <summary>
/// Context for <see cref="ResourceCommandAnnotation.UpdateState"/>.
/// </summary>
[AspireExport(ExposeProperties = true)]
public sealed class UpdateCommandStateContext
{
    /// <summary>
    /// The resource snapshot.
    /// </summary>
    public required CustomResourceSnapshot ResourceSnapshot { get; init; }

    /// <summary>
    /// The service provider.
    /// </summary>
    public required IServiceProvider ServiceProvider { get; init; }
}

/// <summary>
/// Context for <see cref="ResourceCommandAnnotation.ExecuteCommand"/>.
/// </summary>
[AspireExport(ExposeProperties = true)]
public sealed class ExecuteCommandContext
{
    /// <summary>
    /// The service provider.
    /// </summary>
    public required IServiceProvider ServiceProvider { get; init; }

    /// <summary>
    /// The resource name.
    /// </summary>
    public required string ResourceName { get; init; }

    /// <summary>
    /// The cancellation token.
    /// </summary>
    public required CancellationToken CancellationToken { get; init; }

    /// <summary>
    /// The logger for the resource.
    /// </summary>
    public required ILogger Logger { get; init; }
}
