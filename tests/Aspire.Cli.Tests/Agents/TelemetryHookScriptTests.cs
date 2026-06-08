// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using Aspire.Cli.Agents.Hooks;
using Aspire.Cli.Tests.Utils;
using Aspire.TestUtilities;
using Microsoft.AspNetCore.InternalTesting;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aspire.Cli.Tests.Agents;

/// <summary>
/// Behavior tests for the embedded telemetry hook scripts (<c>track-telemetry.sh</c> /
/// <c>track-telemetry.ps1</c>). The scripts are materialized through the real
/// <see cref="TelemetryHookInstaller"/> so the shipped resources are exercised, and a recording stub
/// substituted via the <c>ASPIRE_CLI_COMMAND</c> override captures the argument vector the script
/// would pass to <c>aspire agent telemetry</c> — or records nothing when the script classifies the
/// event as non-Aspire, opted out, or unparseable. Every case also asserts the hook prints exactly
/// <c>{"continue":true}</c>, the contract a PostToolUse hook must honor.
/// </summary>
public class TelemetryHookScriptTests(ITestOutputHelper outputHelper)
{
    private const string CaptureFileEnvName = "ASPIRE_HOOK_TEST_CAPTURE_FILE";
    private const string ContinueResponse = """{"continue":true}""";

    [Fact]
    [RequiresTools(["bash"])]
    [SkipOnPlatform(TestPlatforms.Windows, "The shell hook targets POSIX shells; the PowerShell hook covers Windows.")]
    public async Task Bash_SkillInvocation_Copilot_ForwardsSkillName()
    {
        var run = await RunBashHookAsync(
            """{"toolName":"skill","sessionId":"session-1","toolArgs":{"skill":"aspire"}}""",
            new() { ["COPILOT_CLI"] = "1" });

        AssertContinue(run);
        var args = AssertInvoked(run);
        AssertArg(args, "--event-type", "skill_invocation");
        AssertArg(args, "--client-name", "copilot-cli");
        AssertArg(args, "--skill-name", "aspire");
        AssertArg(args, "--session-id", "session-1");
    }

    [Fact]
    [RequiresTools(["bash"])]
    [SkipOnPlatform(TestPlatforms.Windows, "The shell hook targets POSIX shells; the PowerShell hook covers Windows.")]
    public async Task Bash_McpTool_Claude_ForwardsToolName()
    {
        var run = await RunBashHookAsync(
            """{"hook_event_name":"PostToolUse","tool_name":"mcp__aspire__list_resources"}""");

        AssertContinue(run);
        var args = AssertInvoked(run);
        AssertArg(args, "--event-type", "tool_invocation");
        AssertArg(args, "--client-name", "claude-code");
        AssertArg(args, "--tool-name", "mcp__aspire__list_resources");
    }

    [Fact]
    [RequiresTools(["bash"])]
    [SkipOnPlatform(TestPlatforms.Windows, "The shell hook targets POSIX shells; the PowerShell hook covers Windows.")]
    public async Task Bash_ReferenceFileRead_ForwardsRelativePath()
    {
        var run = await RunBashHookAsync(
            """{"hook_event_name":"PostToolUse","tool_name":"Read","tool_input":{"file_path":".agents/skills/aspire/references/deploy.md"}}""");

        AssertContinue(run);
        var args = AssertInvoked(run);
        AssertArg(args, "--event-type", "reference_file_read");
        // Only the repo-relative path after skills/<skill>/ is forwarded — never the absolute path.
        AssertArg(args, "--file-reference", "aspire/references/deploy.md");
    }

    [Fact]
    [RequiresTools(["bash"])]
    [SkipOnPlatform(TestPlatforms.Windows, "The shell hook targets POSIX shells; the PowerShell hook covers Windows.")]
    public async Task Bash_NonAspireToolOrFile_DoesNotInvokeCli()
    {
        var run = await RunBashHookAsync(
            """{"hook_event_name":"PostToolUse","tool_name":"Read","tool_input":{"file_path":"/home/user/notes.txt"}}""");

        AssertContinue(run);
        AssertNotInvoked(run);
    }

    [Fact]
    [RequiresTools(["bash"])]
    [SkipOnPlatform(TestPlatforms.Windows, "The shell hook targets POSIX shells; the PowerShell hook covers Windows.")]
    public async Task Bash_OptedOut_DoesNotInvokeCli()
    {
        var run = await RunBashHookAsync(
            """{"toolName":"skill","toolArgs":{"skill":"aspire"}}""",
            new() { ["COPILOT_CLI"] = "1", ["ASPIRE_CLI_TELEMETRY_OPTOUT"] = "1" });

        AssertContinue(run);
        AssertNotInvoked(run);
    }

    [Fact]
    [RequiresTools(["bash"])]
    [SkipOnPlatform(TestPlatforms.Windows, "The shell hook targets POSIX shells; the PowerShell hook covers Windows.")]
    public async Task Bash_UnrecognizedPayload_DoesNotInvokeCli()
    {
        // No tool fields the sed extraction can recognize -> nothing to classify, hook stays silent.
        var run = await RunBashHookAsync("not-json-at-all");

        AssertContinue(run);
        AssertNotInvoked(run);
    }

    [Fact]
    [RequiresTools(["pwsh"])]
    public async Task Pwsh_SkillInvocation_Copilot_ForwardsSkillName()
    {
        var run = await RunPwshHookAsync(
            """{"toolName":"skill","sessionId":"session-1","toolArgs":{"skill":"aspire"}}""",
            new() { ["COPILOT_CLI"] = "1" });

        AssertContinue(run);
        var args = AssertInvoked(run);
        AssertArg(args, "--event-type", "skill_invocation");
        AssertArg(args, "--client-name", "copilot-cli");
        AssertArg(args, "--skill-name", "aspire");
        AssertArg(args, "--session-id", "session-1");
    }

    [Fact]
    [RequiresTools(["pwsh"])]
    public async Task Pwsh_McpTool_Claude_ForwardsToolName()
    {
        var run = await RunPwshHookAsync(
            """{"hook_event_name":"PostToolUse","tool_name":"mcp__aspire__list_resources"}""");

        AssertContinue(run);
        var args = AssertInvoked(run);
        AssertArg(args, "--event-type", "tool_invocation");
        AssertArg(args, "--client-name", "claude-code");
        AssertArg(args, "--tool-name", "mcp__aspire__list_resources");
    }

    [Fact]
    [RequiresTools(["pwsh"])]
    public async Task Pwsh_NonAspireTool_DoesNotInvokeCli()
    {
        var run = await RunPwshHookAsync(
            """{"hook_event_name":"PostToolUse","tool_name":"read_file","tool_input":{"path":"/tmp/notes.txt"}}""");

        AssertContinue(run);
        AssertNotInvoked(run);
    }

    [Fact]
    [RequiresTools(["pwsh"])]
    public async Task Pwsh_OptedOut_DoesNotInvokeCli()
    {
        var run = await RunPwshHookAsync(
            """{"toolName":"skill","toolArgs":{"skill":"aspire"}}""",
            new() { ["COPILOT_CLI"] = "1", ["ASPIRE_CLI_TELEMETRY_OPTOUT"] = "true" });

        AssertContinue(run);
        AssertNotInvoked(run);
    }

    [Fact]
    [RequiresTools(["pwsh"])]
    public async Task Pwsh_MalformedJson_DoesNotInvokeCli()
    {
        var run = await RunPwshHookAsync("{ this is not valid json");

        AssertContinue(run);
        AssertNotInvoked(run);
    }

    private async Task<HookRun> RunBashHookAsync(string payload, Dictionary<string, string?>? extraEnv = null)
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var scripts = await MaterializeScriptsAsync(workspace).DefaultTimeout();
        var capturePath = Path.Combine(workspace.WorkspaceRoot.FullName, "capture.txt");
        var recorderPath = CreateBashRecorder(workspace.WorkspaceRoot.FullName);

        var result = RunProcess("bash", [scripts.ShellScriptPath], payload, BuildEnvironment(recorderPath, capturePath, extraEnv));

        return new HookRun(result, ReadCapturedArgs(capturePath));
    }

    private async Task<HookRun> RunPwshHookAsync(string payload, Dictionary<string, string?>? extraEnv = null)
    {
        using var workspace = TemporaryWorkspace.Create(outputHelper);
        var scripts = await MaterializeScriptsAsync(workspace).DefaultTimeout();
        var capturePath = Path.Combine(workspace.WorkspaceRoot.FullName, "capture.txt");
        var recorderPath = CreatePwshRecorder(workspace.WorkspaceRoot.FullName);

        // -ExecutionPolicy Bypass so the locally created hook and recorder run on Windows agents.
        var result = RunProcess("pwsh", ["-NoProfile", "-ExecutionPolicy", "Bypass", "-File", scripts.PowerShellScriptPath], payload, BuildEnvironment(recorderPath, capturePath, extraEnv));

        return new HookRun(result, ReadCapturedArgs(capturePath));
    }

    private static async Task<TelemetryHookScripts> MaterializeScriptsAsync(TemporaryWorkspace workspace)
    {
        var home = workspace.CreateDirectory("home");
        var executionContext = TestExecutionContextHelper.CreateExecutionContext(workspace.WorkspaceRoot, homeDirectory: home);
        var installer = new TelemetryHookInstaller(executionContext, NullLogger<TelemetryHookInstaller>.Instance);
        return await installer.EnsureInstalledAsync(CancellationToken.None);
    }

    private static string CreateBashRecorder(string directory)
    {
        var path = Path.Combine(directory, "recorder.sh");
        // Writes each received argument on its own line so assertions can match discrete tokens.
        File.WriteAllText(path, "#!/bin/bash\nprintf '%s\\n' \"$@\" > \"$ASPIRE_HOOK_TEST_CAPTURE_FILE\"\n");
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        }

        return path;
    }

    private static string CreatePwshRecorder(string directory)
    {
        var path = Path.Combine(directory, "recorder.ps1");
        // A simple (non-advanced) script collects every argument, including --flag tokens, into $args.
        File.WriteAllText(path, "$args | Set-Content -LiteralPath $env:ASPIRE_HOOK_TEST_CAPTURE_FILE\n");
        return path;
    }

    private static Dictionary<string, string?> BuildEnvironment(string recorderPath, string capturePath, Dictionary<string, string?>? extraEnv)
    {
        var environment = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["ASPIRE_CLI_COMMAND"] = recorderPath,
            [CaptureFileEnvName] = capturePath
        };

        if (extraEnv is not null)
        {
            foreach (var pair in extraEnv)
            {
                environment[pair.Key] = pair.Value;
            }
        }

        return environment;
    }

    private static string[]? ReadCapturedArgs(string capturePath)
        => File.Exists(capturePath)
            ? File.ReadAllLines(capturePath).Where(line => line.Length > 0).ToArray()
            : null;

    private static void AssertContinue(HookRun run)
        => Assert.Equal(ContinueResponse, run.Result.StdOut.Trim());

    private static string[] AssertInvoked(HookRun run)
    {
        Assert.NotNull(run.CapturedArgs);
        Assert.Equal("agent", run.CapturedArgs![0]);
        Assert.Equal("telemetry", run.CapturedArgs[1]);
        return run.CapturedArgs;
    }

    private static void AssertNotInvoked(HookRun run)
        => Assert.Null(run.CapturedArgs);

    private static void AssertArg(string[] args, string name, string value)
    {
        var index = Array.IndexOf(args, name);
        Assert.True(index >= 0 && index + 1 < args.Length, $"Expected '{name} {value}' in [{string.Join(' ', args)}]");
        Assert.Equal(value, args[index + 1]);
    }

    private static ProcessResult RunProcess(string fileName, IReadOnlyList<string> arguments, string stdinPayload, Dictionary<string, string?> environment)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };

        foreach (var argument in arguments)
        {
            psi.ArgumentList.Add(argument);
        }

        // Clear ambient values so the host environment can't change client detection or opt-out.
        psi.Environment.Remove("COPILOT_CLI");
        psi.Environment.Remove("ASPIRE_CLI_TELEMETRY_OPTOUT");
        foreach (var pair in environment)
        {
            if (pair.Value is null)
            {
                psi.Environment.Remove(pair.Key);
            }
            else
            {
                psi.Environment[pair.Key] = pair.Value;
            }
        }

        using var process = Process.Start(psi)!;

        try
        {
            process.StandardInput.Write(stdinPayload);
            process.StandardInput.Close();
        }
        catch (IOException)
        {
            // The hook may exit before reading stdin (for example on the opt-out path), which closes
            // the pipe early. That is expected; the captured output below is what matters.
        }

        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            process.WaitForExitAsync(timeout.Token).GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
            }

            throw new TimeoutException($"Hook process '{fileName}' did not exit within 30 seconds.");
        }

        var stdout = stdoutTask.GetAwaiter().GetResult();
        var stderr = stderrTask.GetAwaiter().GetResult();
        return new ProcessResult(process.ExitCode, stdout, stderr);
    }

    private sealed record HookRun(ProcessResult Result, string[]? CapturedArgs);

    private sealed record ProcessResult(int ExitCode, string StdOut, string StdErr);
}
