// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Aspire.Cli.Git;
using Aspire.Cli.Resources;

namespace Aspire.Cli.Configuration;

internal sealed class ConfigSource
{
    public const string SectionName = "configSource";

    // General configuration loading must tolerate incomplete advisory bindings.
    // Validate rejects missing values before a source operation uses them.
    [JsonPropertyName("repository")]
    public string Repository { get; init; } = string.Empty;

    [JsonPropertyName("path")]
    public string Path { get; init; } = string.Empty;

    [JsonPropertyName("ref")]
    public string? Ref { get; init; }

    [JsonPropertyName("appliedVersion")]
    public string? AppliedVersion { get; init; }

    internal string GetGitRef()
    {
        if (Ref is null)
        {
            return "HEAD";
        }

        return GitClient.IsCommitSha(Ref) || Ref.StartsWith("refs/heads/", StringComparison.Ordinal) ? Ref : $"refs/heads/{Ref}";
    }

    public static bool HasSameIdentity(ConfigSource first, ConfigSource second)
        => first.Repository == second.Repository
            && first.Path == second.Path
            && first.Ref == second.Ref;

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Repository) || string.IsNullOrWhiteSpace(Path))
        {
            throw new ConfigSourceException(ConfigSourceStrings.InvalidBinding);
        }
        if (!GitHelpers.IsValidRepository(Repository))
        {
            throw new ConfigSourceException(ConfigSourceStrings.InvalidRepository);
        }

        if (Path.Contains('\\') || Path.Contains(':')
            || Path.Any(char.IsControl)
            || Path.Split('/').Any(p => p is "" or "." or ".."))
        {
            throw new ConfigSourceException(ConfigSourceStrings.InvalidPath);
        }

        if (!GitHelpers.IsValidRef(Ref))
        {
            throw new ConfigSourceException(ConfigSourceStrings.InvalidRef);
        }
    }
}

internal sealed record ConfigSourceMetadata(ConfigSource Source, string Commit, string Version, DateTimeOffset FetchedAt);

internal sealed record ConfigSourceCheckMetadata(
    ConfigSource Source, string Commit, string Version, DateTimeOffset CheckedAt, string[]? ComparedAppliedVersions);

internal sealed record ConfigSourceSnapshot(ConfigSourceMetadata Metadata, JsonObject Configuration);

internal sealed class ConfigSourceException(string message) : Exception(message)
{
    // Captured fetch diagnostics are for user display only, never logs or telemetry.
    public string? GitOutput { get; init; }
}
