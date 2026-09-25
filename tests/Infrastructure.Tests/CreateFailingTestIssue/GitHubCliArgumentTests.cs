// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.TestTools;
using Xunit;

namespace Infrastructure.Tests;

/// <summary>
/// Guards the <c>gh</c> argument list built for GitHub API calls.
/// </summary>
/// <remarks>
/// The fixture-directory seam used by the other CreateFailingTestIssue tests returns canned content before
/// any process arguments are constructed, so those tests cannot observe this. These tests replace the
/// invoker itself, which is the only place the flag is visible. Both tests stay in one class so xUnit keeps
/// them in the same collection and never runs them concurrently against the static
/// <see cref="GitHubCli.GhInvokerOverride"/>.
/// </remarks>
public class GitHubCliArgumentTests
{
    [Fact]
    public async Task JobLogDownloadAllowsTerminalEscapeSequences()
    {
        var arguments = await CaptureArgumentsAsync(
            () => GitHubActionsApi.DownloadJobLogAsync("microsoft/aspire", 12345, CancellationToken.None));

        // CLI end-to-end job logs embed the raw terminal recording, so `gh` refuses to write them to a
        // non-TTY stdout without this flag and fails the whole call.
        Assert.Equal(
            ["api", "-H", "Accept: application/vnd.github+json", "--allow-escape-sequences", "repos/microsoft/aspire/actions/jobs/12345/logs"],
            arguments);
    }

    [Fact]
    public async Task OrdinaryApiCallsDoNotAllowTerminalEscapeSequences()
    {
        var arguments = await CaptureArgumentsAsync(
            () => GitHubCli.GetStringAsync("repos/microsoft/aspire/actions/runs/1", CancellationToken.None));

        // A JSON payload carrying terminal control characters is worth failing on, so the flag stays off
        // everywhere except the logs endpoint.
        Assert.Equal(
            ["api", "-H", "Accept: application/vnd.github+json", "repos/microsoft/aspire/actions/runs/1"],
            arguments);
    }

    private static async Task<IReadOnlyList<string>> CaptureArgumentsAsync(Func<Task<string>> call)
    {
        IReadOnlyList<string> captured = [];

        // The fixture seam returns canned content before argument construction, so clear it here to ensure
        // these tests exercise the production call path all the way down to the gh invoker.
        var previousFixtureDirectory = Environment.GetEnvironmentVariable("ASPIRE_FAILING_TEST_ISSUE_FIXTURE_DIR");
        Environment.SetEnvironmentVariable("ASPIRE_FAILING_TEST_ISSUE_FIXTURE_DIR", null);

        GitHubCli.GhInvokerOverride = (arguments, _) =>
        {
            captured = arguments;
            return Task.FromResult("{}");
        };

        try
        {
            await call();
        }
        finally
        {
            GitHubCli.GhInvokerOverride = null;
            Environment.SetEnvironmentVariable("ASPIRE_FAILING_TEST_ISSUE_FIXTURE_DIR", previousFixtureDirectory);
        }

        return captured;
    }
}
