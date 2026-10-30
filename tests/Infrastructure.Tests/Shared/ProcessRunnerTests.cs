// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.TestUtilities;
using Xunit;

namespace Infrastructure.Tests;

[Trait("Category", "AgenticWorkflow")]
public sealed class ProcessRunnerTests(ITestOutputHelper output)
{
    [Fact]
    [RequiresTools(["bash"])]
    public async Task CapturesStreamsExitCodeAndInvocationContext()
    {
        using var workspace = TemporaryWorkspace.Create(output);
        File.WriteAllText(Path.Combine(workspace.Path, "marker.txt"), "working directory");

        var result = await ProcessRunner.RunAsync(
            output,
            "bash",
            ["-c", """printf '%s\n' "$1" "$(cat marker.txt)"; printf 'error' >&2; exit 7""", "test", "argument with spaces"],
            workspace.Path);

        Assert.Equal(7, result.ExitCode);
        Assert.Equal("argument with spaces\nworking directory\n", result.StandardOutput);
        Assert.Equal("error", result.StandardError);
        Assert.Equal(result.StandardOutput + result.StandardError, result.Output);
    }

    [Fact]
    [RequiresTools(["bash"])]
    public async Task AppliesEnvironmentOverridesOnTopOfInheritedEnvironment()
    {
        using var workspace = TemporaryWorkspace.Create(output);

        var result = await ProcessRunner.RunAsync(
            output,
            "bash",
            // HOME is never synthesized by bash itself, so it is only set when the
            // parent environment was inherited (PATH lookup would still succeed via
            // bash's built-in default search path).
            ["-c", """printf '%s|%s' "$RUNNER_TEST_VALUE" "${HOME:+inherited-home}" """],
            workspace.Path,
            new Dictionary<string, string> { ["RUNNER_TEST_VALUE"] = "override" });

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("override|inherited-home", result.StandardOutput);
    }

    [Fact]
    [RequiresTools(["bash"])]
    public async Task DrainsBothOutputStreamsConcurrently()
    {
        using var workspace = TemporaryWorkspace.Create(output);

        var result = await ProcessRunner.RunAsync(
            output,
            "bash",
            ["-c", """printf '%262144s' ''; printf '%262144s' '' >&2"""],
            workspace.Path);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(new string(' ', 256 * 1024), result.StandardOutput);
        Assert.Equal(new string(' ', 256 * 1024), result.StandardError);
    }
}
