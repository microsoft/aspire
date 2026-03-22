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
    Number
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
/// Options passed when starting a debug session from the CLI to the extension.
/// </summary>
internal sealed class DebugSessionOptions
{
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
}
