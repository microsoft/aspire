// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Cli.Git;

internal static class GitHelpers
{
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
}
