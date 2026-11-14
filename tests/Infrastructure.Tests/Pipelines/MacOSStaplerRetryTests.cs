// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.TestUtilities;
using Xunit;

namespace Infrastructure.Tests;

public sealed class MacOSStaplerRetryTests(ITestOutputHelper output)
{
    private const string ConnectionLost = "Error Domain=NSURLErrorDomain Code=-1005 \"The network connection was lost.\"";
    private const string NotNotarized = "The path has not been previously notarized.";

    [Theory]
    [InlineData("staple", 1, ConnectionLost, 0, new[] { 5 })]
    [InlineData("validate", 1, ConnectionLost, 0, new[] { 5 })]
    [InlineData("staple", 1, NotNotarized, 68, new int[0])]
    [InlineData("validate", 1, NotNotarized, 68, new int[0])]
    [InlineData("staple", 4, ConnectionLost, 68, new[] { 5, 15, 30 })]
    [InlineData("validate", 4, ConnectionLost, 68, new[] { 5, 15, 30 })]
    [RequiresTools(["bash"])]
    public async Task MacOSStaplerOperationsRetryOnlyConnectionLoss(
        string operation, int failures, string error, int expectedStatus, int[] expectedDelays)
    {
        using var workspace = TemporaryWorkspace.Create(output);
        var bin = workspace.CreateDirectory("commands").FullName;
        await File.WriteAllTextAsync(Path.Combine(workspace.Path, "attempt"), "0\n");
        await File.WriteAllTextAsync(Path.Combine(workspace.Path, "delays"), "");
        foreach (var name in new[] { "xcrun", "sleep" })
        {
            var stub = Path.Combine(bin, name);
            await ExecutableFile.WriteAsync(stub, """
                #!/usr/bin/env bash
                set -euo pipefail
                if [[ "${0##*/}" == sleep ]]; then
                    printf '%s\n' "$1" >> "$STAPLER_TEST_ROOT/delays"
                    exit 0
                fi
                read -r attempt < "$STAPLER_TEST_ROOT/attempt"
                attempt=$((attempt + 1))
                printf '%s\n' "$attempt" > "$STAPLER_TEST_ROOT/attempt"
                if [[ "$attempt" -le "$STAPLER_TEST_FAILURES" ]]; then
                    printf '%s\n' "$STAPLER_TEST_ERROR" >&2
                    exit 68
                fi
                printf 'Ticket verified.\n'
                """);
        }

        var app = Path.Combine(workspace.Path, "Aspire.app");
        var result = await TestProcessRunner.RunAsync(
            output,
            "bash",
            [Path.Combine(RepoRoot.Path, "tools", "CreateLayout", "macos-stapler-with-retry.sh"), operation, app],
            workspace.Path,
            new Dictionary<string, string>
            {
                ["PATH"] = bin + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH"),
                ["STAPLER_TEST_ROOT"] = workspace.Path,
                ["STAPLER_TEST_FAILURES"] = failures.ToString(),
                ["STAPLER_TEST_ERROR"] = error,
                ["TMPDIR"] = workspace.Path
            });

        Assert.Equal(expectedStatus, result.ExitCode);
        Assert.Equal(expectedDelays.Length + 1, int.Parse(await File.ReadAllTextAsync(Path.Combine(workspace.Path, "attempt"))));
        Assert.Equal(expectedDelays, (await File.ReadAllLinesAsync(Path.Combine(workspace.Path, "delays"))).Select(int.Parse));
    }
}
