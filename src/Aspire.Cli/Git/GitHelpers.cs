// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Cli.Git;

internal static class GitHelpers
{
    internal static bool IsValidRepository(string repository)
    {
        if (string.IsNullOrWhiteSpace(repository))
        {
            return false;
        }

        // Git accepts https://host/repo, ssh://git@host/repo, git@host:repo, and local paths.
        // Require fully qualified local paths so a saved source cannot change repositories
        // when a later command runs in a different working directory.
        var isLocal = Path.IsPathFullyQualified(repository);
        var isUrl = IsValidRepositoryUrl(repository);
        var at = repository.IndexOf('@');
        var colon = repository.IndexOf(':');
        var isScp = at > 0 && colon > at + 1 && colon < repository.Length - 1
            && !repository.Contains("://", StringComparison.Ordinal)
            && !repository[..colon].Contains('/')
            && !repository.Contains('\\')
            && repository[0] != '-'
            && repository[..at].All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-')
            && Uri.CheckHostName(repository[(at + 1)..colon]) != UriHostNameType.Unknown;

        return !repository.Any(char.IsControl)
            && (isLocal || (!repository.Any(char.IsWhiteSpace)
                && !repository.Contains('?') && !repository.Contains('#')
                && !repository.StartsWith('-') && (isUrl || isScp)));
    }

    public static bool IsValidRepositoryUrl(string repository) =>
        !repository.Any(c => char.IsWhiteSpace(c) || char.IsControl(c))
        && !repository.Contains('\\')
        && !repository.Contains('?')
        && !repository.Contains('#')
        && Uri.TryCreate(repository, UriKind.Absolute, out var uri)
        && uri.Scheme is "https" or "ssh"
        && !string.IsNullOrEmpty(uri.Host)
        // HTTPS credentials belong in Git's credential helper; SSH URLs may contain a
        // username (ssh://git@host/repo), but must not embed a password.
        && (uri.Scheme == "ssh" ? !uri.UserInfo.Contains(':') : string.IsNullOrEmpty(uri.UserInfo));

    internal static bool IsValidRef(string? reference)
    {
        if (reference is null || GitClient.IsCommitSha(reference))
        {
            return true;
        }

        var branch = reference.StartsWith("refs/heads/", StringComparison.Ordinal) ? reference["refs/heads/".Length..] : reference;
        // Branch selectors follow git-check-ref-format rather than accepting revision
        // expressions, refspec operators, tags, or command-line options.
        // https://git-scm.com/docs/git-check-ref-format
        return branch is not ("" or "@" or "HEAD") && !branch.StartsWith('-') && !branch.StartsWith("refs/", StringComparison.Ordinal)
            && !branch.Contains("..", StringComparison.Ordinal) && !branch.Contains("@{", StringComparison.Ordinal)
            && !branch.Any(c => char.IsWhiteSpace(c) || char.IsControl(c) || c is '\\' or ':' or '?' or '*' or '[' or '~' or '^')
            && !branch.Split('/').Any(part => part.Length == 0 || part.StartsWith('.') || part.EndsWith('.')
                || part.EndsWith(".lock", StringComparison.OrdinalIgnoreCase));
    }
}
