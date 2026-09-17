// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json.Serialization;

namespace Aspire.Cli.Backchannel;

/// <summary>
/// Represents a single line of output to be displayed, along with its associated stream (such as "stdout" or "stderr").
/// </summary>
internal sealed class DisplayLineState(string stream, string line)
{
    /// <summary>
    /// Gets the name of the stream the line belongs to (e.g., "stdout", "stderr").
    /// </summary>
    public string Stream { get; } = stream;

    /// <summary>
    /// Gets the content of the line to be displayed.
    /// </summary>
    public string Line { get; } = line;
}

/// <summary>
/// Specifies the type of input for a publishing prompt input.
/// </summary>
internal enum InputType
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
    /// A file input. Allows the user to select a file.
    /// </summary>
    File
}

internal sealed class EnvVar
{
    // Name of the environment variable
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    // Value of the environment variable
    [JsonPropertyName("value")]
    public string? Value { get; set; }
}

/// <summary>
/// Describes an action that an extension can attach to an interaction message.
/// </summary>
internal sealed class InteractionMessageAction
{
    private InteractionMessageAction(string displayName, string? command, string? filePath)
    {
        DisplayName = displayName;
        Command = command;
        FilePath = filePath;
    }

    [JsonPropertyName("displayName")]
    public string DisplayName { get; }

    [JsonPropertyName("command")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Command { get; }

    [JsonPropertyName("filePath")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? FilePath { get; }

    public static InteractionMessageAction ExecuteCommand(string displayName, string command)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
        ArgumentException.ThrowIfNullOrWhiteSpace(command);

        return new(displayName, command, filePath: null);
    }

    public static InteractionMessageAction OpenFile(string displayName, string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        return new(displayName, command: null, filePath);
    }
}

/// <summary>
/// Options passed when starting a debug session from the CLI to the extension.
/// </summary>
internal sealed class DebugSessionOptions
{
    internal const string ExplicitCliAppHostSelectionOrigin = "explicit-cli";
    internal const string DefaultDiscoveryAppHostSelectionOrigin = "default-discovery";

    /// <summary>
    /// Gets or sets the command type for the debug session (e.g., "run", "deploy", "publish", "do").
    /// </summary>
    [JsonPropertyName("command")]
    public string? Command { get; set; }

    /// <summary>
    /// Gets or sets additional arguments to pass to the command (e.g., step name for "do", unmatched tokens).
    /// </summary>
    [JsonPropertyName("args")]
    public string[]? Args { get; set; }

    /// <summary>
    /// Gets or sets environment variables to pass to the CLI process started by the debug session.
    /// </summary>
    [JsonPropertyName("env")]
    public Dictionary<string, string>? EnvironmentVariables { get; set; }

    /// <summary>
    /// Gets or sets a value describing how the AppHost was selected.
    /// </summary>
    [JsonPropertyName("appHostSelectionOrigin")]
    public string? AppHostSelectionOrigin { get; set; }
}

/// <summary>
/// Carries one structured AppHost log record to an extension debug session.
/// </summary>
internal sealed class ExtensionAppHostLogEntry
{
    [JsonPropertyName("generationId")]
    public required Guid GenerationId { get; set; }

    [JsonPropertyName("sequenceNumber")]
    public required long SequenceNumber { get; set; }

    [JsonPropertyName("logLevel")]
    public required string LogLevel { get; set; }

    [JsonPropertyName("message")]
    public required string Message { get; set; }

    [JsonPropertyName("categoryName")]
    public required string CategoryName { get; set; }

    [JsonPropertyName("eventId")]
    public required int EventId { get; set; }

    [JsonPropertyName("exception")]
    public string? Exception { get; set; }
}
