// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Aspire.Hosting.Dcp.Model;

internal static class ExecutableLaunchMode
{
    public const string Debug = "Debug";
    public const string NoDebug = "NoDebug";
}

/// <summary>
/// Base properties for all executable launch configurations.
/// </summary>
/// <param name="type">Launch configuration type indicator.</param>
internal class ExecutableLaunchConfiguration(string type)
{
    /// <summary>
    /// The launch configuration type indicator.
    /// </summary>
    [JsonPropertyName("type")]
    public string Type { get; set; } = type;

    /// <summary>
    /// Specifies the launch mode. Currently supported modes are Debug (run the project under the debugger) and NoDebug (run the project without debugging).
    /// </summary>
    [JsonPropertyName("mode")]
    public string Mode { get; set; } = System.Diagnostics.Debugger.IsAttached ? ExecutableLaunchMode.Debug : ExecutableLaunchMode.NoDebug;

    /// <summary>
    /// Optional action to take when the server is ready.
    /// </summary>
    [JsonPropertyName("serverReadyAction")]
    public ServerReadyAction? ServerReadyAction { get; set; }
}

internal sealed class ProjectLaunchConfiguration() : ExecutableLaunchConfiguration("project")
{
    [JsonPropertyName("launch_profile")]
    public string LaunchProfile { get; set; } = string.Empty;

    [JsonPropertyName("disable_launch_profile")]
    public bool DisableLaunchProfile { get; set; } = false;

    [JsonPropertyName("project_path")]
    public string ProjectPath { get; set; } = string.Empty;
}

/// <summary>
/// Known server ready action kinds.
/// </summary>
internal enum ServerReadyActionKind
{
    OpenExternally,
    DebugWithChrome,
    DebugWithEdge,
    StartDebugging
}

/// <summary>
/// A string wrapper for server ready actions that preserves unknown values.
/// </summary>
internal readonly record struct ServerReadyActionAction
{
    public ServerReadyActionAction(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        Value = value;
    }

    public string Value { get; }

    public static ServerReadyActionAction OpenExternally { get; } = new("openExternally");
    public static ServerReadyActionAction DebugWithChrome { get; } = new("debugWithChrome");
    public static ServerReadyActionAction DebugWithEdge { get; } = new("debugWithEdge");
    public static ServerReadyActionAction StartDebugging { get; } = new("startDebugging");

    public static ServerReadyActionAction FromKind(ServerReadyActionKind kind) => kind switch
    {
        ServerReadyActionKind.OpenExternally => OpenExternally,
        ServerReadyActionKind.DebugWithChrome => DebugWithChrome,
        ServerReadyActionKind.DebugWithEdge => DebugWithEdge,
        ServerReadyActionKind.StartDebugging => StartDebugging,
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    public bool TryGetKind(out ServerReadyActionKind kind)
    {
        kind = Value switch
        {
            "openExternally" => ServerReadyActionKind.OpenExternally,
            "debugWithChrome" => ServerReadyActionKind.DebugWithChrome,
            "debugWithEdge" => ServerReadyActionKind.DebugWithEdge,
            "startDebugging" => ServerReadyActionKind.StartDebugging,
            _ => default
        };

        return Value is "openExternally" or "debugWithChrome" or "debugWithEdge" or "startDebugging";
    }

    public override string ToString() => Value;
}

/// <summary>
/// Serializes <see cref="ServerReadyActionAction"/> as a string value.
/// </summary>
internal sealed class ServerReadyActionActionJsonConverter : JsonConverter<ServerReadyActionAction?>
{
    public override ServerReadyActionAction? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType is JsonTokenType.Null)
        {
            return null;
        }

        if (reader.TokenType is not JsonTokenType.String)
        {
            throw new JsonException($"Expected string or null for {nameof(ServerReadyAction.Action)}.");
        }

        var value = reader.GetString();
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new JsonException($"{nameof(ServerReadyAction.Action)} cannot be empty.");
        }

        return new ServerReadyActionAction(value);
    }

    public override void Write(Utf8JsonWriter writer, ServerReadyActionAction? value, JsonSerializerOptions options)
    {
        if (value is null)
        {
            writer.WriteNullValue();
            return;
        }

        writer.WriteStringValue(value.Value.Value);
    }
}

/// <summary>
/// Optional server ready behavior for a launch configuration.
/// </summary>
internal sealed class ServerReadyAction
{
    [JsonPropertyName("action")]
    [JsonConverter(typeof(ServerReadyActionActionJsonConverter))]
    public ServerReadyActionAction? Action { get; set; }

    [JsonPropertyName("pattern")]
    public string? Pattern { get; set; }

    [JsonPropertyName("uriFormat")]
    public string? UriFormat { get; set; }

    [JsonPropertyName("webRoot")]
    public string? WebRoot { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("config")]
    public JsonObject? Config { get; set; }

    [JsonPropertyName("killOnServerStop")]
    public bool? KillOnServerStop { get; set; }
}
