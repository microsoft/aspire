// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json.Serialization;

namespace Aspire.Hosting.Dcp.Model.V2;

// These contracts follow https://github.com/microsoft/dcp/tree/c5edc40a38f023c1cb24a8a46c7eed1932c15798/api/v2.
// Keep version-specific fragments separate from V1: their wire contracts evolve independently.
internal static class Dcp
{
    public static GroupVersion GroupVersion { get; } = new()
    {
        Group = Model.Dcp.GroupName,
        Version = "v2"
    };
}

internal sealed class EnvVar
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("value")]
    public string Value { get; set; } = "";
}

internal sealed class Label
{
    [JsonPropertyName("key")]
    public string Key { get; set; } = "";

    [JsonPropertyName("value")]
    public string Value { get; set; } = "";
}

internal sealed class AmbientEnvironment
{
    // Omission means Inherit; DoNotInherit must be sent explicitly.
    [JsonPropertyName("behavior")]
    public string? Behavior { get; set; }
}

internal static class EnvironmentBehavior
{
    public const string Inherit = "Inherit";
    public const string DoNotInherit = "DoNotInherit";
}

internal sealed class LogSource
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("uid")]
    public string Uid { get; set; } = "";

    [JsonPropertyName("containerID")]
    public string? ContainerId { get; set; }

    [JsonPropertyName("stdoutFile")]
    public string? StdoutFile { get; set; }

    [JsonPropertyName("stderrFile")]
    public string? StderrFile { get; set; }

    [JsonPropertyName("raw")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Raw { get; set; }

    [JsonPropertyName("available")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Available { get; set; }

    [JsonPropertyName("complete")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Complete { get; set; }
}

internal static class Logs
{
    public const string SubResourceName = "log";
    public const string Stdout = "stdout";
    public const string Stderr = "stderr";
    public const string System = "system";
    public const string StartupStdout = "startup_stdout";
    public const string StartupStderr = "startup_stderr";
}
