// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.TestTools;
using Xunit;

namespace Infrastructure.Tests;

/// <summary>
/// Guards the <c>gh</c> argument list built for GitHub API calls.
/// </summary>
public class GitHubCliArgumentTests
{
    [Fact]
    public void JobLogDownloadAllowsTerminalEscapeSequences()
    {
        var arguments = GitHubCli.BuildApiArguments(
            "repos/microsoft/aspire/actions/jobs/12345/logs",
            allowEscapeSequences: true);

        // CLI end-to-end job logs embed the raw terminal recording, so `gh` refuses to write them to a
        // non-TTY stdout without this flag and fails the whole call.
        Assert.Equal(
            ["api", "-H", "Accept: application/vnd.github+json", "--allow-escape-sequences", "repos/microsoft/aspire/actions/jobs/12345/logs"],
            arguments);
    }

    [Fact]
    public void OrdinaryApiCallsDoNotAllowTerminalEscapeSequences()
    {
        var arguments = GitHubCli.BuildApiArguments(
            "repos/microsoft/aspire/actions/runs/1",
            allowEscapeSequences: false);

        // A JSON payload carrying terminal control characters is worth failing on, so the flag stays off
        // everywhere except the logs endpoint.
        Assert.Equal(
            ["api", "-H", "Accept: application/vnd.github+json", "repos/microsoft/aspire/actions/runs/1"],
            arguments);
    }
}
