// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Cli.Commands;

/// <summary>
/// Represents the outcome of a CLI command execution.
/// </summary>
internal sealed class CommandResult
{
    public int ExitCode { get; }

    public string? ErrorMessage { get; }

    public bool ShouldDisplayHelp { get; }

    public bool ShouldDisplayCancellationMessage { get; }

    private CommandResult(int exitCode, string? errorMessage = null, bool shouldDisplayHelp = false, bool shouldDisplayCancellationMessage = false)
    {
        ExitCode = exitCode;
        ErrorMessage = errorMessage;
        ShouldDisplayHelp = shouldDisplayHelp;
        ShouldDisplayCancellationMessage = shouldDisplayCancellationMessage;
    }

    public static CommandResult Success() => new(CliExitCodes.Success);

    public static CommandResult Failure(int exitCode, string? errorMessage = null) => new(exitCode, errorMessage);

    /// <summary>
    /// Indicates the command was cancelled by the user (e.g. Ctrl+C).
    /// <see cref="BaseCommand"/> displays the cancellation message centrally.
    /// </summary>
    public static CommandResult Cancelled(int exitCode = CliExitCodes.Cancelled) => new(exitCode, shouldDisplayCancellationMessage: true);

    /// <summary>
    /// Indicates the command should display help and return an invalid-command exit code.
    /// </summary>
    public static CommandResult DisplayHelp() => new(CliExitCodes.InvalidCommand, shouldDisplayHelp: true);

    /// <summary>
    /// Creates a result from a raw exit code with no error message.
    /// Useful when wrapping calls that return plain exit codes.
    /// </summary>
    public static CommandResult FromExitCode(int exitCode) => exitCode switch
    {
        CliExitCodes.Success => Success(),
        CliExitCodes.Cancelled => Cancelled(exitCode),
        _ => Failure(exitCode)
    };
}
