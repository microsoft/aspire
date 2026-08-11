// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Aspire.Cli.Projects;

internal sealed class AppHostLaunchSettings
{
    [JsonPropertyName("profiles")]
    public Dictionary<string, AppHostLaunchProfile> Profiles { get; set; } = [];
}

internal sealed class AppHostLaunchProfile
{
    [JsonPropertyName("commandName")]
    public string? CommandName { get; set; }

    [JsonPropertyName("commandLineArgs")]
    public string? CommandLineArgs { get; set; }

    [JsonPropertyName("applicationUrl")]
    public string? ApplicationUrl { get; set; }

    [JsonPropertyName("executablePath")]
    public string? ExecutablePath { get; set; }

    [JsonPropertyName("workingDirectory")]
    public string? WorkingDirectory { get; set; }

    [JsonPropertyName("environmentVariables")]
    public Dictionary<string, string> EnvironmentVariables { get; set; } = [];
}

[JsonSerializable(typeof(AppHostLaunchSettings))]
[JsonSourceGenerationOptions(ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true)]
internal sealed partial class AppHostLaunchSettingsSerializerContext : JsonSerializerContext
{
}
