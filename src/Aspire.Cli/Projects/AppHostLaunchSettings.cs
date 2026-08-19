// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Aspire.Cli.Projects;

// Keep the Project-profile properties aligned with the SDK model even when direct launch does not
// consume them. Strong typing makes malformed SDK-known values fail deserialization so the CLI
// falls back to `dotnet run` for its diagnostic and launch behavior.
// https://github.com/dotnet/sdk/tree/main/src/Microsoft.DotNet.ProjectTools/LaunchSettings
internal sealed class AppHostLaunchProfile
{
    [JsonPropertyName("commandName")]
    public string? CommandName { get; set; }

    [JsonPropertyName("dotnetRunMessages")]
    public bool DotNetRunMessages { get; set; }

    [JsonPropertyName("commandLineArgs")]
    public string? CommandLineArgs { get; set; }

    [JsonPropertyName("launchBrowser")]
    public bool LaunchBrowser { get; set; }

    [JsonPropertyName("launchUrl")]
    public string? LaunchUrl { get; set; }

    [JsonPropertyName("applicationUrl")]
    public string? ApplicationUrl { get; set; }

    [JsonPropertyName("executablePath")]
    public string? ExecutablePath { get; set; }

    [JsonPropertyName("workingDirectory")]
    public string? WorkingDirectory { get; set; }

    [JsonPropertyName("environmentVariables")]
    public Dictionary<string, string> EnvironmentVariables { get; set; } = [];
}

[JsonSerializable(typeof(AppHostLaunchProfile))]
[JsonSourceGenerationOptions(ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true)]
internal sealed partial class AppHostLaunchSettingsSerializerContext : JsonSerializerContext
{
}
