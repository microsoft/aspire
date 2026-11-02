// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Cli.Git;

internal sealed class GitReferenceException(string reference)
    : Exception($"Could not resolve Git reference '{reference}' to a commit.")
{
    public string Reference { get; } = reference;
}
