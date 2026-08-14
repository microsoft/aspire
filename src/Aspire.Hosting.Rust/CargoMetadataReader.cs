// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;

namespace Aspire.Hosting.Rust;

/// <summary>
/// Queries cargo for a crate's package/target layout without compiling anything.
/// </summary>
/// <remarks>
/// Registered in the app host's service container by <c>AddRustApp</c> so tests can substitute a
/// deterministic implementation and exercise publishing and debugging on machines with no Rust toolchain.
/// </remarks>
internal interface ICargoMetadataReader
{
    Task<CargoMetadata> ReadAsync(string workingDirectory, string? manifestPath, string resourceName, IReadOnlyDictionary<string, string> environment, CancellationToken cancellationToken);
}

/// <summary>
/// Queries cargo for a crate's package/target layout without compiling anything.
/// </summary>
/// <remarks>
/// Publishing a Rust app builds it inside the container, so the host must never compile. It still needs the
/// name of the produced binary in order to emit a correct <c>COPY --from=build</c> and <c>ENTRYPOINT</c>, and
/// <c>cargo metadata</c> is the only cargo subcommand that answers that from the manifest alone.
/// <c>--no-deps</c> additionally stops cargo from resolving or downloading the dependency graph.
/// See https://doc.rust-lang.org/cargo/commands/cargo-metadata.html
/// </remarks>
internal sealed class CargoMetadataReader : ICargoMetadataReader
{
    internal const int MaximumStandardErrorLength = 4096;
    private const string TruncatedDiagnosticSuffix = "... (truncated)";

    // A cold `cargo metadata --format-version 1 --no-deps` has been measured at close to 15 seconds on a
    // machine whose cargo caches are empty, so a short timeout would fail valid apps rather than protect
    // them. This is only a backstop against a cargo process that never exits; ordinary shutdown flows
    // through the caller's cancellation token instead.
    private static readonly TimeSpan s_timeout = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Builds the argument vector passed to cargo.
    /// </summary>
    /// <remarks>
    /// Exposed separately so tests can assert that publishing never invokes a compiling subcommand.
    /// </remarks>
    internal static string[] BuildArguments(string? manifestPath)
    {
        string[] arguments = ["metadata", "--format-version", "1", "--no-deps"];

        return manifestPath is null ? arguments : [.. arguments, "--manifest-path", manifestPath];
    }

    /// <summary>
    /// Runs <c>cargo metadata</c> for the crate in <paramref name="workingDirectory"/>.
    /// </summary>
    /// <remarks>
    /// <paramref name="environment"/> carries the resource's own environment so the query sees the same
    /// cargo configuration the build will. <c>CARGO_TARGET_DIR</c> is the one that matters most: it moves the
    /// <c>target_directory</c> cargo reports here, and therefore the path the debugger is pointed at.
    /// </remarks>
    public async Task<CargoMetadata> ReadAsync(string workingDirectory, string? manifestPath, string resourceName, IReadOnlyDictionary<string, string> environment, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo("cargo")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        foreach (var argument in BuildArguments(manifestPath))
        {
            startInfo.ArgumentList.Add(argument);
        }

        foreach (var (name, value) in environment)
        {
            startInfo.Environment[name] = value;
        }

        using var process = new Process { StartInfo = startInfo };

        try
        {
            process.Start();
        }
        catch (Exception ex)
        {
            throw new DistributedApplicationException(
                $"Unable to start 'cargo' to inspect the Rust app '{resourceName}'. Install Rust from https://www.rust-lang.org/tools/install " +
                $"or supply your own Dockerfile in '{workingDirectory}'. {ex.Message}", ex);
        }

        // Drain both redirected streams concurrently so a full pipe cannot block cargo before it exits.
        var stdoutTask = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
        var stderrTask = process.StandardError.ReadToEndAsync(CancellationToken.None);

        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(s_timeout);

        try
        {
            await process.WaitForExitAsync(timeoutSource.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            TryKillProcess(process);
            throw new DistributedApplicationException(
                $"'cargo metadata' for the Rust app '{resourceName}' did not complete within {s_timeout.TotalSeconds:0} seconds.");
        }
        catch (OperationCanceledException)
        {
            TryKillProcess(process);
            throw;
        }

        var stdout = await stdoutTask.ConfigureAwait(false);
        var stderr = await stderrTask.ConfigureAwait(false);

        if (process.ExitCode != 0)
        {
            var diagnostic = FormatStandardError(stderr, environment);
            var diagnosticSuffix = diagnostic.Length > 0 ? $" {diagnostic}" : string.Empty;
            throw new DistributedApplicationException(
                $"'cargo metadata' failed for the Rust app '{resourceName}' with exit code {process.ExitCode}.{diagnosticSuffix}");
        }

        try
        {
            return CargoMetadata.Parse(stdout);
        }
        catch (Exception ex) when (ex is not DistributedApplicationException)
        {
            throw new DistributedApplicationException(
                $"Unable to read the output of 'cargo metadata' for the Rust app '{resourceName}'. {ex.Message}", ex);
        }
    }

    internal static string FormatStandardError(string standardError, IReadOnlyDictionary<string, string> environment)
    {
        // Cargo wrappers and configuration errors can echo values from the resolved resource environment.
        // Redact before truncating so a value that crosses the retained-output boundary cannot leak partially.
        var redacted = standardError;
        foreach (var value in environment.Values
            .Where(static value => !string.IsNullOrEmpty(value))
            .Distinct(StringComparer.Ordinal)
            .OrderByDescending(static value => value.Length))
        {
            redacted = redacted.Replace(value, "***", StringComparison.Ordinal);
        }

        redacted = redacted.Trim();
        if (redacted.Length <= MaximumStandardErrorLength)
        {
            return redacted;
        }

        return string.Concat(
            redacted.AsSpan(0, MaximumStandardErrorLength - TruncatedDiagnosticSuffix.Length),
            TruncatedDiagnosticSuffix);
    }

    private static void TryKillProcess(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
            // The process exited while cancellation was being observed.
        }
    }
}
