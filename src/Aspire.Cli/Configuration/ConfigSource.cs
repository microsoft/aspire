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

        return IsCommit(Ref) || Ref.StartsWith("refs/heads/", StringComparison.Ordinal) ? Ref : $"refs/heads/{Ref}";
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
        // Git accepts https://host/repo, ssh://git@host/repo, git@host:repo, and local paths.
        // Require fully qualified local paths so a saved binding cannot change repositories
        // when a later command runs in a different working directory.
        var isLocal = System.IO.Path.IsPathFullyQualified(Repository);
        var isUrl = GitHelpers.IsValidRepositoryUrl(Repository);
        var at = Repository.IndexOf('@');
        var colon = Repository.IndexOf(':');
        var isScp = at > 0 && colon > at + 1 && colon < Repository.Length - 1
            && !Repository.Contains("://", StringComparison.Ordinal)
            && !Repository[..colon].Contains('/')
            && !Repository.Contains('\\')
            && Repository[0] != '-'
            && Repository[..at].All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-')
            && Uri.CheckHostName(Repository[(at + 1)..colon]) != UriHostNameType.Unknown;
        if (Repository.Any(char.IsControl)
            || (!isLocal && (Repository.Any(char.IsWhiteSpace)
                || Repository.Contains('?') || Repository.Contains('#')
                || Repository.StartsWith('-') || (!isUrl && !isScp))))
        {
            throw new ConfigSourceException(ConfigSourceStrings.InvalidRepository);
        }

        if (Path.Contains('\\') || Path.Contains(':')
            || Path.Any(char.IsControl)
            || Path.Split('/').Any(p => p is "" or "." or ".."))
        {
            throw new ConfigSourceException(ConfigSourceStrings.InvalidPath);
        }

        if (Ref is not null && !IsCommit(Ref))
        {
            var branch = Ref.StartsWith("refs/heads/", StringComparison.Ordinal) ? Ref["refs/heads/".Length..] : Ref;
            // Branch selectors follow git-check-ref-format rather than accepting revision
            // expressions, refspec operators, tags, or command-line options.
            // https://git-scm.com/docs/git-check-ref-format
            if (branch is "" or "@" or "HEAD" || branch.StartsWith('-') || branch.StartsWith("refs/", StringComparison.Ordinal)
                || branch.Contains("..", StringComparison.Ordinal) || branch.Contains("@{", StringComparison.Ordinal)
                || branch.Any(c => char.IsWhiteSpace(c) || char.IsControl(c) || c is '\\' or ':' or '?' or '*' or '[' or '~' or '^')
                || branch.Split('/').Any(part => part.Length == 0 || part.StartsWith('.') || part.EndsWith('.')
                    || part.EndsWith(".lock", StringComparison.OrdinalIgnoreCase)))
            {
                throw new ConfigSourceException(ConfigSourceStrings.InvalidRef);
            }
        }
    }

    private static bool IsCommit(string value) => value.Length is 40 or 64 && value.All(char.IsAsciiHexDigit);
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
