// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.TestTools;
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

        var result = await TestProcessRunner.RunAsync(
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

        var result = await TestProcessRunner.RunAsync(
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

        var result = await TestProcessRunner.RunAsync(
            output,
            "bash",
            ["-c", """printf '%262144s' ''; printf '%262144s' '' >&2"""],
            workspace.Path);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(new string(' ', 256 * 1024), result.StandardOutput);
        Assert.Equal(new string(' ', 256 * 1024), result.StandardError);
    }

    [Fact]
    [RequiresTools(["bash"])]
    public async Task SharedExecutionStreamsBinaryOutputWithoutTextDecoding()
    {
        using var workspace = TemporaryWorkspace.Create(output);
        var file = Path.Combine(workspace.Path, "artifact.zip");
        var result = await ProcessRunner.RunAsync(
            "bash",
            ["-c", """printf '\000\377\200PK'; printf 'diagnostic' >&2; exit 7"""],
            workspace.Path,
            outputFile: file);

        Assert.Equal(7, result.ExitCode);
        Assert.Equal("", result.Output);
        Assert.Equal("diagnostic", result.Error);
        Assert.Equal(new byte[] { 0, 255, 128, 80, 75 }, await File.ReadAllBytesAsync(file));
    }

    [Fact]
    [RequiresTools(["bash"])]
    public async Task SharedExecutionUsesExplicitSearchPathWithoutMutatingParent()
    {
        using var workspace = TemporaryWorkspace.Create(output);
        var originalPath = Environment.GetEnvironmentVariable("PATH");
        var executable = OperatingSystem.IsWindows() ? "bash.exe" : "bash";
        var directory = originalPath!.Split(Path.PathSeparator)
            .First(path => File.Exists(Path.Combine(path, executable)));

        var result = await ProcessRunner.RunAsync(
            executable,
            ["-c", "printf override"],
            workspace.Path,
            new Dictionary<string, string> { ["PATH"] = directory });

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("override", result.Output);
        Assert.Equal(originalPath, Environment.GetEnvironmentVariable("PATH"));
    }

    [Fact]
    [RequiresTools(["dotnet"])]
    public async Task SharedExecutionResolvesExtensionlessCommandsWithAPathOverride()
    {
        using var workspace = TemporaryWorkspace.Create(output);
        var originalPath = Environment.GetEnvironmentVariable("PATH");
        var executable = OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet";
        var directory = originalPath!.Split(Path.PathSeparator)
            .First(path => File.Exists(Path.Combine(path, executable)));

        var result = await ProcessRunner.RunAsync(
            "dotnet",
            ["--version"],
            workspace.Path,
            new Dictionary<string, string> { ["PATH"] = directory });

        Assert.Equal(0, result.ExitCode);
        Assert.Matches(@"^\d+\.\d+\.\d+", result.Output);
        Assert.Equal(originalPath, Environment.GetEnvironmentVariable("PATH"));
    }
}
