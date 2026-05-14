// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Aspire.Cli.Acquisition;

/// <summary>
/// Default <see cref="IPeerInstallProbe"/>. Spawns the peer with
/// <c>info --format json</c>, enforces a hard timeout, captures stdout
/// up to a byte cap, and kills the entire process tree on timeout so a
/// hung peer cannot survive past the parent's lifetime.
/// </summary>
/// <remarks>
/// Uses <see cref="Process"/> directly rather than the project's
/// <c>IProcessExecutionFactory</c> because the latter's cancellation
/// semantics await <see cref="Process.WaitForExitAsync(CancellationToken)"/>
/// directly: on cancellation, the await throws before any kill branch can
/// run, leaving the peer alive. The peer-probe contract requires the kill
/// to actually fire.
/// </remarks>
internal sealed class PeerInstallProbe : IPeerInstallProbe
{
    /// <summary>Maximum wall-clock time we wait for a peer to respond.</summary>
    /// <remarks>
    /// 5 seconds is a generous budget for a native-AOT CLI to start, read
    /// its assembly metadata, write 1 KB of JSON, and exit. A peer slower
    /// than that is almost certainly broken; faster than that is the norm.
    /// </remarks>
    internal static readonly TimeSpan s_defaultTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Stdout byte cap. A misbehaving peer that spams its stdout cannot
    /// allocate unbounded memory in the parent. 1 MiB is far more than the
    /// well-behaved JSON shape (~200 bytes per install) needs.
    /// </summary>
    internal const int StdoutByteCap = 1 * 1024 * 1024;

    private readonly TimeSpan _timeout;
    private readonly ILogger<PeerInstallProbe> _logger;

    public PeerInstallProbe(ILogger<PeerInstallProbe> logger)
        : this(s_defaultTimeout, logger)
    {
    }

    internal PeerInstallProbe(TimeSpan timeout, ILogger<PeerInstallProbe> logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _timeout = timeout;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<PeerProbeResult> ProbeAsync(string binaryPath, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(binaryPath) || !File.Exists(binaryPath))
        {
            return new PeerProbeResult.Failed("Binary not found.");
        }

        // Primary path: ask the peer to self-describe via `info --format json`.
        // Older peers (predating the info command) will exit non-zero here;
        // we fall back to `--version` so they at least surface the version
        // — the rest of the row is filled in from the local sidecar by the
        // caller.
        var primary = await SpawnAndCaptureAsync(binaryPath, ["info", "--format", "json"], cancellationToken).ConfigureAwait(false);
        if (primary.Cancelled)
        {
            cancellationToken.ThrowIfCancellationRequested();
        }

        if (primary.Failure is { } primaryFailure)
        {
            return new PeerProbeResult.Failed(primaryFailure);
        }

        if (primary.ExitCode == 0 && !string.IsNullOrWhiteSpace(primary.Stdout))
        {
            try
            {
                using var doc = JsonDocument.Parse(primary.Stdout);
                if (doc.RootElement.ValueKind == JsonValueKind.Array && doc.RootElement.GetArrayLength() > 0)
                {
                    return new PeerProbeResult.Ok(ParseInstallationInfo(doc.RootElement[0]));
                }
                // Wrong shape — fall through to the --version fallback so an
                // older peer that happens to emit non-array stdout for some
                // other reason still gets best-effort treatment.
                _logger.LogDebug("Peer probe at {BinaryPath} returned non-array JSON; trying --version fallback.", binaryPath);
            }
            catch (JsonException ex)
            {
                _logger.LogDebug(ex, "Peer probe at {BinaryPath} returned invalid JSON; trying --version fallback.", binaryPath);
            }
        }

        // Fallback path. We reach here for:
        //   - peer exited non-zero (common: predates the info command),
        //   - peer emitted blank/whitespace-only stdout,
        //   - peer emitted JSON we couldn't parse as the expected array shape.
        var fallback = await SpawnAndCaptureAsync(binaryPath, ["--version"], cancellationToken).ConfigureAwait(false);
        if (fallback.Cancelled)
        {
            cancellationToken.ThrowIfCancellationRequested();
        }

        if (fallback.Failure is not null)
        {
            // Surface the *primary* failure reason because it tells the user
            // why the rich probe didn't work; the version fallback failing
            // on top is a secondary symptom.
            return new PeerProbeResult.Failed(DescribePrimaryFailure(primary, alsoTriedVersion: true));
        }

        if (fallback.ExitCode != 0)
        {
            return new PeerProbeResult.Failed(DescribePrimaryFailure(primary, alsoTriedVersion: true));
        }

        var versionLine = ExtractVersionLine(fallback.Stdout);
        if (string.IsNullOrEmpty(versionLine))
        {
            return new PeerProbeResult.Failed(DescribePrimaryFailure(primary, alsoTriedVersion: true));
        }

        // Partial info: version only. Route is overlaid by InstallationDiscovery
        // from the locally-readable sidecar (the trust gate already required
        // it). Channel intentionally null — we can't read assembly metadata
        // from outside an AOT binary, and the older peer has no surface that
        // exposes its channel.
        return new PeerProbeResult.Ok(new InstallationInfo
        {
            Path = binaryPath,
            Version = versionLine,
            Status = InstallationInfoStatus.Ok,
        });
    }

    /// <summary>
    /// Spawns the peer with the given arguments and captures stdout under
    /// the timeout / kill-on-timeout / stdout-cap contract. Returns a
    /// structured result describing exit code, captured output, and any
    /// transport-level failure (process couldn't start, etc.).
    /// </summary>
    private async Task<SpawnResult> SpawnAndCaptureAsync(string binaryPath, string[] arguments, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = binaryPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in arguments)
        {
            startInfo.ArgumentList.Add(arg);
        }

        Process process;
        try
        {
            var started = Process.Start(startInfo);
            if (started is null)
            {
                return new SpawnResult(ExitCode: -1, Stdout: string.Empty, Failure: "Process.Start returned null.", Cancelled: false);
            }
            process = started;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            _logger.LogDebug(ex, "Could not start peer probe for {BinaryPath}.", binaryPath);
            return new SpawnResult(ExitCode: -1, Stdout: string.Empty, Failure: $"Could not start peer process: {ex.Message}", Cancelled: false);
        }

        // Combined timeout + cancellation so the user pressing Ctrl-C
        // tears the peer down immediately rather than waiting for the full
        // 5s budget.
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(_timeout);

        var stdoutBuffer = new StringBuilder(capacity: 4096);
        var readStdoutTask = ReadCappedAsync(process.StandardOutput, stdoutBuffer, StdoutByteCap, timeoutCts.Token);

        bool timedOut = false;
        try
        {
            await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            // Linked CTS fired due to the inner timeout, not the caller's
            // cancellation: peer overstayed its budget.
            timedOut = true;
        }

        if (timedOut)
        {
            TryKillProcessTree(process);
            await SwallowAsync(readStdoutTask).ConfigureAwait(false);
            using (process) { /* dispose */ }
            return new SpawnResult(ExitCode: -1, Stdout: stdoutBuffer.ToString(), Failure: $"Peer probe timed out after {_timeout.TotalSeconds:F1}s.", Cancelled: false);
        }

        if (cancellationToken.IsCancellationRequested)
        {
            TryKillProcessTree(process);
            await SwallowAsync(readStdoutTask).ConfigureAwait(false);
            using (process) { /* dispose */ }
            return new SpawnResult(ExitCode: -1, Stdout: stdoutBuffer.ToString(), Failure: null, Cancelled: true);
        }

        try
        {
            await readStdoutTask.ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException)
        {
            _logger.LogDebug(ex, "Could not read peer probe stdout for {BinaryPath}.", binaryPath);
        }

        int exitCode;
        try
        {
            exitCode = process.ExitCode;
        }
        finally
        {
            process.Dispose();
        }

        return new SpawnResult(ExitCode: exitCode, Stdout: stdoutBuffer.ToString(), Failure: null, Cancelled: false);
    }

    /// <summary>
    /// Composes a user-facing reason for a probe failure. When the
    /// <c>--version</c> fallback was also attempted, prefix the message so
    /// users see both attempts in one row.
    /// </summary>
    private static string DescribePrimaryFailure(SpawnResult primary, bool alsoTriedVersion)
    {
        var suffix = alsoTriedVersion ? " (and --version fallback)" : string.Empty;
        if (primary.Failure is { } reason)
        {
            return reason + suffix;
        }
        if (primary.ExitCode != 0)
        {
            return $"Peer exited with code {primary.ExitCode}{suffix}.";
        }
        return $"Peer produced no usable output{suffix}.";
    }

    /// <summary>
    /// Pulls the first non-blank line out of <c>aspire --version</c>
    /// output. Older Aspire CLI versions emit just the bare version
    /// string; newer versions may add a banner, in which case the first
    /// non-blank line still holds the version.
    /// </summary>
    private static string? ExtractVersionLine(string stdout)
    {
        foreach (var raw in stdout.Split('\n'))
        {
            var trimmed = raw.Trim();
            if (trimmed.Length == 0)
            {
                continue;
            }
            return trimmed;
        }
        return null;
    }

    private readonly record struct SpawnResult(int ExitCode, string Stdout, string? Failure, bool Cancelled);

    /// <summary>
    /// Reads stdout into <paramref name="buffer"/> until EOF or
    /// <paramref name="cap"/> bytes have been collected, whichever comes
    /// first. The cap is enforced in chars (close enough to bytes for an
    /// ASCII JSON shape) so we never allocate unbounded memory for a
    /// peer spamming stdout.
    /// </summary>
    private static async Task ReadCappedAsync(StreamReader reader, StringBuilder buffer, int cap, CancellationToken cancellationToken)
    {
        var chunk = new char[4096];
        while (!cancellationToken.IsCancellationRequested)
        {
            int read;
            try
            {
                read = await reader.ReadAsync(chunk.AsMemory(), cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException)
            {
                return;
            }
            if (read == 0)
            {
                return;
            }
            var remaining = cap - buffer.Length;
            if (remaining <= 0)
            {
                // Cap hit: keep draining the pipe so the peer doesn't block
                // on a full pipe, but discard the trailing bytes.
                continue;
            }
            buffer.Append(chunk, 0, Math.Min(read, remaining));
        }
    }

    /// <summary>
    /// Kills the peer including any children. Best-effort: a peer that
    /// has already exited or that we lack permission to signal simply
    /// drops out of the kill loop without raising.
    /// </summary>
    private void TryKillProcessTree(Process process)
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
            _logger.LogDebug(ex, "Could not kill peer probe process {Pid}.", process.Id);
        }
    }

    private static async Task SwallowAsync(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch
        {
            // Reader is being torn down alongside the killed process —
            // any exception here is uninteresting noise.
        }
    }

    /// <summary>
    /// Parses one row of the peer's <c>aspire info --format json</c>
    /// output. Resilient to optional fields so peers across versions
    /// don't fail to decode.
    /// </summary>
    private static InstallationInfo ParseInstallationInfo(JsonElement row)
    {
        string GetStringOr(string property, string fallback)
        {
            return row.TryGetProperty(property, out var el) && el.ValueKind == JsonValueKind.String
                ? el.GetString() ?? fallback
                : fallback;
        }
        string? GetOptionalString(string property)
        {
            return row.TryGetProperty(property, out var el) && el.ValueKind == JsonValueKind.String
                ? el.GetString()
                : null;
        }
        bool GetBool(string property)
        {
            return row.TryGetProperty(property, out var el) && el.ValueKind is JsonValueKind.True or JsonValueKind.False
                && el.GetBoolean();
        }

        return new InstallationInfo
        {
            Path = GetStringOr("path", string.Empty),
            CanonicalPath = GetOptionalString("canonicalPath"),
            Version = GetOptionalString("version"),
            Channel = GetOptionalString("channel"),
            Route = GetOptionalString("route"),
            IsOnPath = GetBool("isOnPath"),
            Status = GetStringOr("status", InstallationInfoStatus.Ok),
            StatusReason = GetOptionalString("statusReason"),
        };
    }
}
