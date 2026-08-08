// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text;
using Aspire.Hosting.Dcp.Process;

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
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();

        Task<ProcessResult> resultTask;
        IAsyncDisposable disposable;

        try
        {
            (resultTask, disposable) = ProcessUtil.Run(new ProcessSpec("cargo")
            {
                ArgumentList = BuildArguments(manifestPath),
                WorkingDirectory = workingDirectory,
                EnvironmentVariables = environment.ToDictionary(),
                // Cargo reports a missing or malformed manifest on stderr with a non-zero exit code, which is
                // more useful than a generic launch failure, so handle the exit code here instead.
                ThrowOnNonZeroReturnCode = false,
                OnOutputData = line => stdout.AppendLine(line),
                OnErrorData = line => stderr.AppendLine(line)
            });
        }
        catch (Exception ex)
        {
            throw new DistributedApplicationException(
                $"Unable to start 'cargo' to inspect the Rust app '{resourceName}'. Install Rust from https://www.rust-lang.org/tools/install " +
                $"or supply your own Dockerfile in '{workingDirectory}'. {ex.Message}", ex);
        }

        ProcessResult result;

        await using (disposable.ConfigureAwait(false))
        {
            using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutSource.CancelAfter(s_timeout);

            try
            {
                result = await resultTask.WaitAsync(timeoutSource.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new DistributedApplicationException(
                    $"'cargo metadata' for the Rust app '{resourceName}' did not complete within {s_timeout.TotalSeconds:0} seconds.");
            }
        }

        if (result.ExitCode != 0)
        {
            throw new DistributedApplicationException(
                $"'cargo metadata' failed for the Rust app '{resourceName}' with exit code {result.ExitCode}. {stderr.ToString().Trim()}");
        }

        try
        {
            return CargoMetadata.Parse(stdout.ToString());
        }
        catch (Exception ex) when (ex is not DistributedApplicationException)
        {
            throw new DistributedApplicationException(
                $"Unable to read the output of 'cargo metadata' for the Rust app '{resourceName}'. {ex.Message}", ex);
        }
    }
}
