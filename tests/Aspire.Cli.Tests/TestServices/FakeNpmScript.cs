// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Globalization;

namespace Aspire.Cli.Tests.TestServices;

internal sealed class FakeNpmScript : IDisposable
{
    private const int DefaultMaxPolls = 50;
    private static readonly TimeSpan s_holderStartupWaitBound = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan s_holderShutdownWaitBound = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan s_holderObservationPollInterval = TimeSpan.FromMilliseconds(10);
    private static readonly string[] s_expectedHolderProcessNames = OperatingSystem.IsWindows()
        ? ["powershell", "pwsh"]
        : ["sh", "bash", "dash"];

    private readonly ITestOutputHelper _outputHelper;
    private readonly string _output;
    private readonly bool _delayParentExitUntilReleased;
    private readonly bool _ignoreReleaseFile;
    private readonly bool _ignoreForceExitFile;
    private readonly bool _publishMutablePidOnRelease;
    private Process? _holderProcess;
    private bool _released;
    private bool _disposed;

    private FakeNpmScript(
        ITestOutputHelper outputHelper,
        TemporaryWorkspace workspace,
        string output,
        bool delayParentExitUntilReleased,
        bool ignoreReleaseFile,
        bool ignoreForceExitFile,
        bool publishMutablePidOnRelease)
    {
        _outputHelper = outputHelper;
        Workspace = workspace;
        _output = output;
        _delayParentExitUntilReleased = delayParentExitUntilReleased;
        _ignoreReleaseFile = ignoreReleaseFile;
        _ignoreForceExitFile = ignoreForceExitFile;
        _publishMutablePidOnRelease = publishMutablePidOnRelease;

        ParentReadyFile = Path.Combine(Workspace.WorkspaceRoot.FullName, "npm-parent-ready");
        ParentExitedFile = Path.Combine(Workspace.WorkspaceRoot.FullName, "npm-parent-exited");
        ParentReleaseFile = Path.Combine(Workspace.WorkspaceRoot.FullName, "npm-parent-release");
        ReleaseFile = Path.Combine(Workspace.WorkspaceRoot.FullName, "npm-release");
        ForceExitFile = Path.Combine(Workspace.WorkspaceRoot.FullName, "npm-force-exit");
        HolderIdentityFile = Path.Combine(Workspace.WorkspaceRoot.FullName, "npm-holder.identity");
        HolderForceExitAcknowledgedFile = Path.Combine(Workspace.WorkspaceRoot.FullName, "npm-holder-force-exit-ack");
        HolderExitedFile = Path.Combine(Workspace.WorkspaceRoot.FullName, "npm-holder-exited");
        HolderPidFile = Path.Combine(Workspace.WorkspaceRoot.FullName, "npm-holder.pid");
        HolderReleasePidFile = Path.Combine(Workspace.WorkspaceRoot.FullName, "npm-holder.release-pid");
        ScriptPath = OperatingSystem.IsWindows()
            ? Path.Combine(Workspace.WorkspaceRoot.FullName, "npm.cmd")
            : Path.Combine(Workspace.WorkspaceRoot.FullName, "npm");
        HolderScriptPath = OperatingSystem.IsWindows()
            ? Path.Combine(Workspace.WorkspaceRoot.FullName, "npm-holder.ps1")
            : Path.Combine(Workspace.WorkspaceRoot.FullName, "npm-holder.sh");
    }

    public TemporaryWorkspace Workspace { get; }

    public string ScriptPath { get; }

    public string HolderScriptPath { get; }

    public string ParentReadyFile { get; }

    public string ParentExitedFile { get; }

    public string ParentReleaseFile { get; }

    public string ReleaseFile { get; }

    public string ForceExitFile { get; }

    public string HolderIdentityFile { get; }

    public string HolderForceExitAcknowledgedFile { get; }

    public string HolderExitedFile { get; }

    public string HolderPidFile { get; }

    public string HolderReleasePidFile { get; }

    internal static bool IsExpectedHolderProcessName(string processName, string holderScriptPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(processName);
        ArgumentException.ThrowIfNullOrWhiteSpace(holderScriptPath);

        var normalizedProcessName = Path.GetFileName(processName);
        if (string.IsNullOrWhiteSpace(normalizedProcessName))
        {
            normalizedProcessName = processName;
        }

        if (s_expectedHolderProcessNames.Contains(normalizedProcessName, StringComparer.OrdinalIgnoreCase))
        {
            return true;
        }

        if (OperatingSystem.IsWindows())
        {
            return false;
        }

        return string.Equals(
            normalizedProcessName,
            Path.GetFileName(holderScriptPath),
            StringComparison.OrdinalIgnoreCase);
    }

    public static FakeNpmScript BuildExitThenHoldOutputOpen(ITestOutputHelper outputHelper, string output)
    {
        return Build(outputHelper, output, delayParentExitUntilReleased: false, ignoreReleaseFile: false, ignoreForceExitFile: false, publishMutablePidOnRelease: false);
    }

    public static FakeNpmScript BuildWaitForParentReleaseThenHoldOutputOpen(ITestOutputHelper outputHelper, string output)
    {
        return Build(outputHelper, output, delayParentExitUntilReleased: true, ignoreReleaseFile: false, ignoreForceExitFile: false, publishMutablePidOnRelease: false);
    }

    public static FakeNpmScript BuildExitThenIgnoreRelease(ITestOutputHelper outputHelper, string output)
    {
        return Build(outputHelper, output, delayParentExitUntilReleased: false, ignoreReleaseFile: true, ignoreForceExitFile: false, publishMutablePidOnRelease: false);
    }

    public static FakeNpmScript BuildExitThenIgnoreReleaseAndForceExit(ITestOutputHelper outputHelper, string output)
    {
        return Build(outputHelper, output, delayParentExitUntilReleased: false, ignoreReleaseFile: true, ignoreForceExitFile: true, publishMutablePidOnRelease: false);
    }

    public static FakeNpmScript BuildExitThenPublishMutablePidOnRelease(ITestOutputHelper outputHelper, string output)
    {
        return Build(outputHelper, output, delayParentExitUntilReleased: false, ignoreReleaseFile: false, ignoreForceExitFile: false, publishMutablePidOnRelease: true);
    }

    private static FakeNpmScript Build(
        ITestOutputHelper outputHelper,
        string output,
        bool delayParentExitUntilReleased,
        bool ignoreReleaseFile,
        bool ignoreForceExitFile,
        bool publishMutablePidOnRelease)
    {
        var workspace = TemporaryWorkspace.CreateForCli(outputHelper);
        var script = new FakeNpmScript(outputHelper, workspace, output, delayParentExitUntilReleased, ignoreReleaseFile, ignoreForceExitFile, publishMutablePidOnRelease);
        script.WriteScripts();
        return script;
    }

    public void ApplyEnvironment(ProcessStartInfo startInfo)
    {
        ArgumentNullException.ThrowIfNull(startInfo);

        foreach (var pair in BuildProcessEnvironment())
        {
            startInfo.Environment[pair.Key] = pair.Value;
        }
    }

    public IDisposable UseEnvironment()
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        var overrides = new List<IDisposable>
        {
            new EnvVarOverride("PATH", $"{Workspace.WorkspaceRoot.FullName}{Path.PathSeparator}{path}")
        };

        foreach (var pair in BuildProcessEnvironment())
        {
            overrides.Add(new EnvVarOverride(pair.Key, pair.Value));
        }

        if (OperatingSystem.IsWindows())
        {
            var pathExtensions = Environment.GetEnvironmentVariable("PATHEXT");
            overrides.Add(new EnvVarOverride("PATHEXT", string.IsNullOrEmpty(pathExtensions) ? ".CMD" : $".CMD;{pathExtensions}"));
        }

        return new EnvironmentOverrideScope(overrides);
    }

    public Task WaitForParentReadyAsync(CancellationToken cancellationToken = default)
    {
        return WaitForFileAsync(ParentReadyFile, cancellationToken);
    }

    public Task WaitForParentExitMarkerAsync(CancellationToken cancellationToken = default)
    {
        return WaitForFileAsync(ParentExitedFile, cancellationToken);
    }

    public async Task WaitForParentExitAsync(CancellationToken cancellationToken = default)
    {
        await WaitForParentExitMarkerAsync(cancellationToken).ConfigureAwait(false);

        // Capture the holder immediately after the parent exits and before callers can mutate the
        // identity file. This pins the exact helper instance without starting an observation timeout
        // before the fake npm process has even launched.
        _ = await TryGetVerifiedHolderProcessAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task ReleaseParentExitAsync(CancellationToken cancellationToken = default)
    {
        File.WriteAllText(ParentReleaseFile, string.Empty);
        await WaitForParentExitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<Process> WaitForHolderProcessAsync(CancellationToken cancellationToken = default)
    {
        var holderProcess = await TryGetVerifiedHolderProcessAsync(cancellationToken).ConfigureAwait(false);
        if (holderProcess is null)
        {
            throw new InvalidOperationException("The fake npm holder process never started.");
        }

        return holderProcess;
    }

    public async Task ReleaseAsync(CancellationToken cancellationToken = default)
    {
        var holderProcess = await TryGetVerifiedHolderProcessAsync(cancellationToken).ConfigureAwait(false);

        if (!_released)
        {
            _released = true;
            File.WriteAllText(ReleaseFile, string.Empty);
        }

        await EnsureHolderStoppedAsync(holderProcess, cancellationToken).ConfigureAwait(false);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        try
        {
            ReleaseAsync().GetAwaiter().GetResult();
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or OperationCanceledException or TimeoutException)
        {
            _outputHelper.WriteLine($"[FakeNpmScript] Failed to stop holder process cleanly: {ex.Message}");
        }
        finally
        {
            _holderProcess?.Dispose();
            Workspace.Dispose();
        }
    }

    private async Task EnsureHolderStoppedAsync(Process? holderProcess, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
        {
            await EnsureHolderStoppedOnUnixAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        await EnsureHolderStoppedOnWindowsAsync(holderProcess, cancellationToken).ConfigureAwait(false);
    }

    private async Task EnsureHolderStoppedOnUnixAsync(CancellationToken cancellationToken)
    {
        // Process handles do not pin PID identity on Unix. Once we've signalled release, a cooperative
        // helper must acknowledge and exit on its own so cleanup never sends a late kill to a reused PID.
        if (await TryWaitForFileAsync(HolderExitedFile, s_holderShutdownWaitBound, cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        File.WriteAllText(ForceExitFile, string.Empty);

        if (await TryWaitForFileAsync(HolderExitedFile, s_holderShutdownWaitBound, cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        _outputHelper.WriteLine("[FakeNpmScript] Unix holder did not acknowledge force-exit within the cleanup bound.");
    }

    private async Task EnsureHolderStoppedOnWindowsAsync(Process? holderProcess, CancellationToken cancellationToken)
    {
        if (await TryWaitForFileAsync(HolderExitedFile, s_holderShutdownWaitBound, cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        File.WriteAllText(ForceExitFile, string.Empty);

        if (await TryWaitForFileAsync(HolderExitedFile, s_holderShutdownWaitBound, cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        if (holderProcess is null)
        {
            _outputHelper.WriteLine("[FakeNpmScript] Holder exit signal did not arrive within the cleanup bound.");
            return;
        }

        if (holderProcess.HasExited)
        {
            return;
        }

        // Windows PID reuse can hand a later lookup a different pwsh/powershell process with the same PID.
        // Only fall back to Kill after the holder ignored both cooperative signals and only against the
        // exact Process handle we observed before release while the real holder was still alive.
        _outputHelper.WriteLine(
            $"[FakeNpmScript] Holder process {holderProcess.Id} ignored release and force-exit signals. Killing the exact observed process before workspace cleanup.");
        TryKillExactProcess(holderProcess);

        using var waitForKillCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        waitForKillCts.CancelAfter(s_holderShutdownWaitBound);

        try
        {
            await holderProcess.WaitForExitAsync(waitForKillCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _outputHelper.WriteLine($"[FakeNpmScript] Holder process {holderProcess.Id} did not exit after an exact-process kill.");
        }
    }

    private async Task<Process?> TryGetVerifiedHolderProcessAsync(CancellationToken cancellationToken)
    {
        if (_holderProcess is not null)
        {
            return _holderProcess;
        }

        return await ObserveHolderProcessAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<Process?> ObserveHolderProcessAsync(CancellationToken cancellationToken)
    {
        if (_holderProcess is not null)
        {
            return _holderProcess;
        }

        var observationStopwatch = Stopwatch.StartNew();
        if (!await TryWaitForFileAsync(HolderIdentityFile, s_holderStartupWaitBound, cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        var remainingObservationTime = s_holderStartupWaitBound - observationStopwatch.Elapsed;
        if (remainingObservationTime <= TimeSpan.Zero)
        {
            return await TryCreateVerifiedHolderProcessAsync(CancellationToken.None, logFailure: true).ConfigureAwait(false);
        }

        using var observationTimeoutCts = new CancellationTokenSource(remainingObservationTime);
        using var combinedObservationCts = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            observationTimeoutCts.Token);

        // FileSystemWatcher can report the identity file as soon as it is created, before the shell
        // has finished writing the PID into it. Keep waiting for the actual condition we need — a
        // readable, validated holder process — instead of caching a permanent null from an empty read.
        while (!combinedObservationCts.IsCancellationRequested)
        {
            Process? holderProcess;
            try
            {
                holderProcess = await TryCreateVerifiedHolderProcessAsync(
                    combinedObservationCts.Token,
                    logFailure: false).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // The observation budget can expire during the file read. Perform the final
                // uncancelled validation below instead of surfacing that internal timeout.
                break;
            }

            if (holderProcess is not null)
            {
                return holderProcess;
            }

            try
            {
                await Task.Delay(s_holderObservationPollInterval, combinedObservationCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                break;
            }
        }

        return await TryCreateVerifiedHolderProcessAsync(CancellationToken.None, logFailure: true).ConfigureAwait(false);
    }

    private async Task<Process?> TryCreateVerifiedHolderProcessAsync(CancellationToken cancellationToken, bool logFailure = true)
    {
        string holderPidText;
        try
        {
            holderPidText = (await File.ReadAllTextAsync(HolderIdentityFile, cancellationToken).ConfigureAwait(false)).Trim();
        }
        catch (IOException ex)
        {
            if (logFailure)
            {
                _outputHelper.WriteLine($"[FakeNpmScript] Could not read holder identity file: {ex.Message}");
            }

            return null;
        }

        if (!int.TryParse(holderPidText, NumberStyles.None, CultureInfo.InvariantCulture, out var holderPid))
        {
            if (logFailure)
            {
                _outputHelper.WriteLine($"[FakeNpmScript] Holder identity '{holderPidText}' is not a valid PID. Cleanup will not kill by PID.");
            }
            return null;
        }

        Process holderProcess;
        try
        {
            holderProcess = Process.GetProcessById(holderPid);
        }
        catch (ArgumentException ex)
        {
            if (logFailure)
            {
                _outputHelper.WriteLine($"[FakeNpmScript] Holder process {holderPid} exited before it could be observed: {ex.Message}");
            }
            return null;
        }

        try
        {
            _ = holderProcess.Handle;
            var processName = holderProcess.ProcessName;
            if (!IsExpectedHolderProcessName(processName, HolderScriptPath))
            {
                if (logFailure)
                {
                    _outputHelper.WriteLine(
                        $"[FakeNpmScript] Holder PID {holderPid} resolved to unexpected process '{processName}'. Cleanup will not kill by PID.");
                }
                holderProcess.Dispose();
                return null;
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            if (logFailure)
            {
                _outputHelper.WriteLine(
                    $"[FakeNpmScript] Could not validate holder process {holderPid}: {ex.Message}. Cleanup will not kill by PID.");
            }
            holderProcess.Dispose();
            return null;
        }

        _holderProcess = holderProcess;
        return _holderProcess;
    }

    private IReadOnlyDictionary<string, string> BuildProcessEnvironment()
    {
        var environment = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["NPM_OUTPUT"] = _output,
            ["NPM_PARENT_READY_FILE"] = ParentReadyFile,
            ["NPM_PARENT_EXITED_FILE"] = ParentExitedFile,
            ["NPM_PARENT_RELEASE_FILE"] = ParentReleaseFile,
            ["NPM_RELEASE_FILE"] = ReleaseFile,
            ["NPM_FORCE_EXIT_FILE"] = ForceExitFile,
            ["NPM_HOLDER_IDENTITY_FILE"] = HolderIdentityFile,
            ["NPM_HOLDER_FORCE_EXIT_ACK_FILE"] = HolderForceExitAcknowledgedFile,
            ["NPM_HOLDER_EXITED_FILE"] = HolderExitedFile,
            ["NPM_HOLDER_PID_FILE"] = HolderPidFile,
            ["NPM_HOLDER_RELEASE_PID_FILE"] = HolderReleasePidFile,
            ["NPM_FAKE_MAX_POLLS"] = DefaultMaxPolls.ToString(CultureInfo.InvariantCulture)
        };

        if (_delayParentExitUntilReleased)
        {
            environment["NPM_DELAY_PARENT_EXIT_UNTIL_RELEASE"] = "1";
        }

        if (_ignoreReleaseFile)
        {
            environment["NPM_IGNORE_RELEASE_FILE"] = "1";
        }

        if (_ignoreForceExitFile)
        {
            environment["NPM_IGNORE_FORCE_EXIT_FILE"] = "1";
        }

        if (_publishMutablePidOnRelease)
        {
            environment["NPM_PUBLISH_PID_ON_RELEASE"] = "1";
        }

        return environment;
    }

    private void WriteScripts()
    {
        var mainScript = OperatingSystem.IsWindows() ? RenderBatchScript() : RenderShellScript();
        var holderScript = OperatingSystem.IsWindows() ? RenderPowerShellHolderScript() : RenderShellHolderScript();

        File.WriteAllText(ScriptPath, mainScript);
        File.WriteAllText(HolderScriptPath, holderScript);

        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(
                ScriptPath,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
            File.SetUnixFileMode(
                HolderScriptPath,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        }

        DumpScript(_outputHelper, ScriptPath, mainScript);
        DumpScript(_outputHelper, HolderScriptPath, holderScript);
    }

    private string RenderShellScript()
    {
        return $$"""
                 #!/bin/sh
                 "{{HolderScriptPath}}" "$$" &
                 : > "$NPM_PARENT_READY_FILE"
                 if [ -n "$NPM_OUTPUT" ]; then
                   printf '%s\n' "$NPM_OUTPUT"
                 fi
                 if [ "$NPM_DELAY_PARENT_EXIT_UNTIL_RELEASE" = "1" ]; then
                   poll_count=0
                   max_polls="${NPM_FAKE_MAX_POLLS:-50}"
                   while [ ! -f "$NPM_PARENT_RELEASE_FILE" ]; do
                     poll_count=$((poll_count + 1))
                     if [ "$poll_count" -ge "$max_polls" ]; then
                       exit 0
                     fi
                     sleep 0.1
                   done
                 fi
                 exit 0
                 """;
    }

    private static string RenderShellHolderScript()
    {
        return """
               #!/bin/sh
               parent_pid="$1"
               printf '%s\n' "$$" > "$NPM_HOLDER_IDENTITY_FILE"
               if [ "$NPM_PUBLISH_PID_ON_RELEASE" != "1" ]; then
                 printf '%s\n' "$$" > "$NPM_HOLDER_PID_FILE"
               fi
               max_polls="${NPM_FAKE_MAX_POLLS:-50}"
               poll_count=0
               parent_exited=0
               while kill -0 "$parent_pid" 2>/dev/null; do
                 poll_count=$((poll_count + 1))
                 if [ "$poll_count" -ge "$max_polls" ]; then
                   exit 0
                 fi
                 sleep 0.1
               done
               parent_exited=1
               if [ "$parent_exited" -ne 1 ]; then
                 exit 0
               fi
               : > "$NPM_PARENT_EXITED_FILE"
               poll_count=0
               while [ "$poll_count" -lt "$max_polls" ]; do
                 if [ "$NPM_IGNORE_RELEASE_FILE" != "1" ] && [ -f "$NPM_RELEASE_FILE" ]; then
                   if [ "$NPM_PUBLISH_PID_ON_RELEASE" = "1" ]; then
                     if [ -f "$NPM_HOLDER_RELEASE_PID_FILE" ]; then
                       cp "$NPM_HOLDER_RELEASE_PID_FILE" "$NPM_HOLDER_PID_FILE"
                     else
                       printf '%s\n' "$$" > "$NPM_HOLDER_PID_FILE"
                     fi
                   fi
                   : > "$NPM_HOLDER_EXITED_FILE"
                   exit 0
                 fi
                 if [ "$NPM_IGNORE_FORCE_EXIT_FILE" != "1" ] && [ -f "$NPM_FORCE_EXIT_FILE" ]; then
                   : > "$NPM_HOLDER_FORCE_EXIT_ACK_FILE"
                   : > "$NPM_HOLDER_EXITED_FILE"
                   exit 0
                 fi
                 poll_count=$((poll_count + 1))
                 sleep 0.1
               done
               exit 0
               """;
    }

    private string RenderBatchScript()
    {
        return $$"""
                 @echo off
                 setlocal
                 rem `for /f ... in ('command')` runs the command under a transient cmd.exe /c.
                 rem The holder must watch the outer npm shell, so walk from PowerShell -> transient cmd -> outer cmd.
                 for /f %%a in ('powershell -NoProfile -ExecutionPolicy Bypass -Command "$current = Get-CimInstance Win32_Process -Filter ('ProcessId=' + $PID); $transient = Get-CimInstance Win32_Process -Filter ('ProcessId=' + $current.ParentProcessId); [Console]::WriteLine($transient.ParentProcessId)"') do set NPM_PARENT_PID=%%a
                 start "" /b powershell -NoProfile -ExecutionPolicy Bypass -File "{{HolderScriptPath}}" -ParentPid %NPM_PARENT_PID%
                 type nul > "%NPM_PARENT_READY_FILE%"
                 if defined NPM_OUTPUT echo %NPM_OUTPUT%
                 if "%NPM_DELAY_PARENT_EXIT_UNTIL_RELEASE%"=="1" powershell -NoProfile -ExecutionPolicy Bypass -Command "$maxPolls = [int]$env:NPM_FAKE_MAX_POLLS; for ($i = 0; $i -lt $maxPolls; $i++) { if (Test-Path $env:NPM_PARENT_RELEASE_FILE) { exit 0 }; Start-Sleep -Milliseconds 100 }; exit 0"
                 exit /b 0
                 """;
    }

    private static string RenderPowerShellHolderScript()
    {
        return """
               param([int]$ParentPid)

               [System.IO.File]::WriteAllText(
                   $env:NPM_HOLDER_IDENTITY_FILE,
                   $PID.ToString([System.Globalization.CultureInfo]::InvariantCulture))

               if ($env:NPM_PUBLISH_PID_ON_RELEASE -ne '1')
               {
                   [System.IO.File]::WriteAllText(
                       $env:NPM_HOLDER_PID_FILE,
                       $PID.ToString([System.Globalization.CultureInfo]::InvariantCulture))
               }

               $maxPolls = [int]$env:NPM_FAKE_MAX_POLLS
               $parentExited = $false
               for ($i = 0; $i -lt $maxPolls; $i++)
               {
                   if (-not (Get-Process -Id $ParentPid -ErrorAction SilentlyContinue))
                   {
                       $parentExited = $true
                       break
                   }

                   Start-Sleep -Milliseconds 100
               }

               if (-not $parentExited)
               {
                   exit 0
               }

               [void](New-Item -ItemType File -Path $env:NPM_PARENT_EXITED_FILE -Force)

               for ($i = 0; $i -lt $maxPolls; $i++)
               {
                   if ($env:NPM_IGNORE_RELEASE_FILE -ne '1' -and (Test-Path $env:NPM_RELEASE_FILE))
                   {
                       if ($env:NPM_PUBLISH_PID_ON_RELEASE -eq '1')
                       {
                           if (Test-Path $env:NPM_HOLDER_RELEASE_PID_FILE)
                           {
                               Copy-Item -Path $env:NPM_HOLDER_RELEASE_PID_FILE -Destination $env:NPM_HOLDER_PID_FILE -Force
                           }
                           else
                           {
                               [System.IO.File]::WriteAllText(
                                   $env:NPM_HOLDER_PID_FILE,
                                   $PID.ToString([System.Globalization.CultureInfo]::InvariantCulture))
                           }
                       }

                       [void](New-Item -ItemType File -Path $env:NPM_HOLDER_EXITED_FILE -Force)
                       exit 0
                   }

                   if ($env:NPM_IGNORE_FORCE_EXIT_FILE -ne '1' -and (Test-Path $env:NPM_FORCE_EXIT_FILE))
                   {
                       [void](New-Item -ItemType File -Path $env:NPM_HOLDER_FORCE_EXIT_ACK_FILE -Force)
                       [void](New-Item -ItemType File -Path $env:NPM_HOLDER_EXITED_FILE -Force)
                       exit 0
                   }

                   Start-Sleep -Milliseconds 100
               }

               exit 0
               """;
    }

    private static void TryKillExactProcess(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or System.ComponentModel.Win32Exception)
        {
            // Best-effort cleanup. The bounded helper lifetime remains the final backstop if the kill fails.
        }
    }

    private static async Task WaitForFileAsync(string path, CancellationToken cancellationToken)
    {
        if (File.Exists(path))
        {
            return;
        }

        var fileName = Path.GetFileName(path);
        var directory = Path.GetDirectoryName(path);
        if (directory is null)
        {
            throw new InvalidOperationException($"Could not determine parent directory for '{path}'.");
        }

        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var watcher = new FileSystemWatcher(directory, fileName)
        {
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.CreationTime | NotifyFilters.LastWrite,
            EnableRaisingEvents = true
        };

        void CompleteIfPresent()
        {
            if (File.Exists(path))
            {
                tcs.TrySetResult();
            }
        }

        FileSystemEventHandler createdOrChanged = (_, _) => CompleteIfPresent();
        RenamedEventHandler renamed = (_, _) => CompleteIfPresent();
        watcher.Created += createdOrChanged;
        watcher.Changed += createdOrChanged;
        watcher.Renamed += renamed;

        CompleteIfPresent();
        using var registration = cancellationToken.Register(() => tcs.TrySetCanceled(cancellationToken));
        await tcs.Task.ConfigureAwait(false);
    }

    private static async Task<bool> TryWaitForFileAsync(string path, TimeSpan timeout, CancellationToken cancellationToken)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout);

        try
        {
            await WaitForFileAsync(path, cts.Token).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return File.Exists(path);
        }
    }

    private static void DumpScript(ITestOutputHelper outputHelper, string path, string content)
    {
        outputHelper.WriteLine($"[FakeNpmScript] --- begin script at {path} ---");
        outputHelper.WriteLine(content);
        outputHelper.WriteLine($"[FakeNpmScript] --- end script at {path} ---");
    }

    private sealed class EnvironmentOverrideScope(List<IDisposable> overrides) : IDisposable
    {
        private readonly List<IDisposable> _overrides = overrides;

        public void Dispose()
        {
            for (var i = _overrides.Count - 1; i >= 0; i--)
            {
                _overrides[i].Dispose();
            }
        }
    }
}
