// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Aspire.Cli.Configuration;

internal sealed class AspireConfigProvenance
{
    [JsonPropertyName("template")]
    [Description("The Git-backed template used to generate this app.")]
    public AspireConfigTemplateProvenance? Template { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

internal sealed class AspireConfigTemplateProvenance
{
    [JsonPropertyName("repository")]
    [Description("Source Git repository location, without embedded credentials.")]
    public string? Repository { get; set; }

    [JsonPropertyName("sha")]
    [Description("The resolved Git commit used for generation, not a mutable branch or tag.")]
    public string? Sha { get; set; }

    [JsonPropertyName("path")]
    [Description("Repository-relative directory containing the selected template's .template.config, with forward-slash separators.")]
    public string? Path { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}
