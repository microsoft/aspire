// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using Aspire.Cli.Telemetry;
using Aspire.Cli.Utils;
using Microsoft.Extensions.Logging;
using Semver;

namespace Aspire.Cli.Npm;

/// <summary>
/// Runs npm CLI commands for package management operations.
/// </summary>
internal sealed class NpmRunner(IEnvironment environment, ILogger<NpmRunner> logger, ProfilingTelemetry profilingTelemetry) : INpmRunner
{
    /// <summary>
    /// The canonical public npm registry URL. Commands that resolve, pack, or install
    /// packages pass this explicitly via <c>--registry</c> so resolution and install use
    /// the public feed and cannot inherit a project-level <c>.npmrc</c> that redirects to a
    /// private feed (for example an Azure DevOps Artifacts feed). Such a private feed would
    /// otherwise return 401 for packages (including transitive dependencies) it has not
    /// mirrored, breaking <c>aspire agent init</c>.
    /// See https://github.com/microsoft/aspire/issues/19370.
    /// </summary>
    private const string PublicRegistry = "https://registry.npmjs.org/";
    private const string LatestDistTag = "latest";
    private static readonly TimeSpan s_metadataLookupTimeout = TimeSpan.FromSeconds(10);

    private readonly Lazy<string?> _npmPath = new(() => PathLookupHelper.FindFullPathFromPath("npm"));

    /// <inheritdoc />
    public bool IsAvailable => _npmPath.Value is not null;

    /// <inheritdoc />
    public async Task<SemVersion> GetLatestVersionAsync(string packageName, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packageName);
        cancellationToken.ThrowIfCancellationRequested();

        var npmPath = FindNpmPath();
        if (npmPath is null)
        {
            throw new InvalidOperationException("npm is not installed or not found in PATH.");
        }

        var packageSpecifier = NpmPackageInfo.FormatPackageSpecifier(packageName, LatestDistTag);
        logger.LogDebug("Resolving npm package {PackageSpecifier} using global npm configuration.", packageSpecifier);

        // `--global` applies the same registry, proxy, certificate, and authentication configuration
        // as the update command while excluding a project .npmrc. Running from an isolated directory
        // also prevents unrelated project discovery from affecting any other npm behavior.
        var tempDir = CreateIsolatedTempDirectory();

        try
        {
            var args = new[]
            {
                "view",
                packageSpecifier,
                "version",
                "--global",
                "--json=false",
                "--color=false",
                "--loglevel=error"
            };
            var startInfo = CreateNpmProcessStartInfo(npmPath, args, tempDir, environment);
            using var activity = profilingTelemetry.StartNpmCommand(npmPath, args, tempDir);

            var result = await ProcessCaptureRunner.RunAsync(
                startInfo,
                s_metadataLookupTimeout,
                async (process, captureCancellationToken) =>
                {
                    activity.SetProcessId(process.Id);

                    // Drain both streams concurrently so npm cannot block on a full pipe. Stderr can
                    // include configured registry details, so discard it rather than retaining it.
                    var outputTask = process.StandardOutput.ReadToEndAsync(captureCancellationToken);
                    var errorTask = process.StandardError.BaseStream.CopyToAsync(
                        Stream.Null,
                        captureCancellationToken);
                    await Task.WhenAll(outputTask, errorTask).ConfigureAwait(false);

                    return await outputTask.ConfigureAwait(false);
                },
                static () => string.Empty,
                logger,
                cancellationToken);

            if (result.FailureKind is ProcessCaptureFailureKind.TimedOut)
            {
                activity.SetError($"npm timed out after {s_metadataLookupTimeout.TotalSeconds:g} seconds.");
            }
            else if (result.FailureKind is not null)
            {
                activity.SetError(result.FailureMessage ?? "npm could not be started.");
            }

            if (!result.Cancelled && result.FailureKind is null)
            {
                activity.SetProcessExitCode(result.ExitCode);

                if (result.ExitCode != 0)
                {
                    activity.SetError($"npm exited with code {result.ExitCode}.");
                }
            }

            return ResolveLatestVersionLookupResult(packageSpecifier, result, cancellationToken);
        }
        finally
        {
            CleanupTempDirectory(tempDir);
        }
    }

    internal static SemVersion ResolveLatestVersionLookupResult(
        string packageSpecifier,
        ProcessCaptureResult<string> result,
        CancellationToken cancellationToken)
    {
        if (result.FailureKind is ProcessCaptureFailureKind.TimedOut)
        {
            throw new TimeoutException(
                $"Timed out after {s_metadataLookupTimeout.TotalSeconds:g} seconds while resolving {packageSpecifier} through npm.");
        }

        if (result.FailureKind is not null)
        {
            throw new InvalidOperationException(
                $"Could not run npm while resolving {packageSpecifier}: {result.FailureMessage}");
        }

        if (result.Cancelled)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw new OperationCanceledException(cancellationToken);
        }

        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"npm exited with code {result.ExitCode} while resolving {packageSpecifier}.");
        }

        // ProcessCaptureRunner bounds post-exit stream draining and can therefore turn a
        // caller cancellation that lands during that drain into an empty capture result.
        // Re-check the original token only after timeout/start/capture failures and non-zero
        // exits have already been classified so Ctrl+C does not mask the true outcome.
        cancellationToken.ThrowIfCancellationRequested();

        if (!TryExtractLastVersion(result.Capture, out var versionString) ||
            !SemVersion.TryParse(versionString, SemVersionStyles.Strict, out var version))
        {
            throw new InvalidDataException(
                $"npm returned an invalid version while resolving {packageSpecifier}.");
        }

        return version;
    }

    /// <inheritdoc />
    public async Task<NpmPackageInfo?> ResolvePackageAsync(string packageName, string versionRange, CancellationToken cancellationToken)
    {
        var npmPath = FindNpmPath();
        if (npmPath is null)
        {
            return null;
        }

        logger.LogDebug("Resolving npm package {PackageSpecifier}", NpmPackageInfo.FormatPackageSpecifier(packageName, versionRange));

        // Use an isolated temp subdirectory so npm doesn't pick up .npmrc or
        // other config files from the shared temp root or the user's CWD.
        var tempDir = CreateIsolatedTempDirectory();

        try
        {
            // Resolve version: npm view <package>@<range> version
            var versionOutput = await RunNpmCommandInDirectoryAsync(
                npmPath,
                ["view", NpmPackageInfo.FormatPackageSpecifier(packageName, versionRange), "version", "--registry", PublicRegistry],
                tempDir,
                cancellationToken);

            if (versionOutput is null)
            {
                logger.LogDebug("Failed to resolve version for {PackageSpecifier}", NpmPackageInfo.FormatPackageSpecifier(packageName, versionRange));
                return null;
            }

            if (!TryExtractLastVersion(versionOutput, out var versionString))
            {
                logger.LogDebug("Could not extract version from npm output: {Output}", versionOutput.Trim());
                return null;
            }

            if (!SemVersion.TryParse(versionString, SemVersionStyles.Any, out var version))
            {
                logger.LogDebug("Could not parse npm version from output: {Output}", versionString);
                return null;
            }

            logger.LogDebug("Resolved {PackageSpecifier}", NpmPackageInfo.FormatPackageSpecifier(packageName, version));

            return new NpmPackageInfo
            {
                Version = version
            };
        }
        finally
        {
            CleanupTempDirectory(tempDir);
        }
    }

    /// <inheritdoc />
    public async Task<string?> PackAsync(string packageName, string version, string outputDirectory, CancellationToken cancellationToken)
    {
        var npmPath = FindNpmPath();
        if (npmPath is null)
        {
            return null;
        }

        logger.LogDebug("Packing npm package {PackageSpecifier} to {OutputDirectory}", NpmPackageInfo.FormatPackageSpecifier(packageName, version), outputDirectory);

        var output = await RunNpmCommandInDirectoryAsync(
            npmPath,
            ["pack", NpmPackageInfo.FormatPackageSpecifier(packageName, version), "--pack-destination", outputDirectory, "--registry", PublicRegistry],
            outputDirectory,
            cancellationToken);

        if (output is null)
        {
            logger.LogDebug("Failed to pack {PackageSpecifier}", NpmPackageInfo.FormatPackageSpecifier(packageName, version));
            return null;
        }

        // npm pack outputs the filename of the created tarball
        var filename = output.Trim().Split(['\n', '\r'], StringSplitOptions.RemoveEmptyEntries).LastOrDefault();
        if (string.IsNullOrWhiteSpace(filename))
        {
            logger.LogDebug("npm pack returned empty filename");
            return null;
        }

        var tarballPath = Path.Combine(outputDirectory, filename);
        if (!File.Exists(tarballPath))
        {
            logger.LogDebug("npm pack output file not found: {Path}", tarballPath);
            return null;
        }

        logger.LogDebug("Packed {PackageSpecifier} to {TarballPath}", NpmPackageInfo.FormatPackageSpecifier(packageName, version), tarballPath);

        return tarballPath;
    }

    /// <inheritdoc />
    public async Task<bool> InstallGlobalAsync(string tarballPath, CancellationToken cancellationToken)
    {
        var npmPath = FindNpmPath();
        if (npmPath is null)
        {
            return false;
        }

        logger.LogDebug("Installing npm package globally from {TarballPath}", tarballPath);

        // Use an isolated temp subdirectory so npm doesn't pick up .npmrc or
        // other config files from the shared temp root or the user's CWD.
        var tempDir = CreateIsolatedTempDirectory();

        try
        {
            // The root tarball is provenance-verified, but its transitive dependencies are not.
            // Prevent dependency lifecycle scripts from executing during installation.
            var output = await RunNpmCommandInDirectoryAsync(
                npmPath,
                ["install", "-g", tarballPath, "--ignore-scripts", "--registry", PublicRegistry],
                tempDir,
                cancellationToken);

            if (output is null)
            {
                logger.LogDebug("Failed to install npm package globally from {TarballPath}", tarballPath);
                return false;
            }

            logger.LogDebug("Successfully installed npm package globally from {TarballPath}", tarballPath);
            return true;
        }
        finally
        {
            CleanupTempDirectory(tempDir);
        }
    }

    private string? FindNpmPath()
    {
        var npmPath = _npmPath.Value;
        if (npmPath is null)
        {
            logger.LogDebug("npm is not installed or not found in PATH");
        }

        return npmPath;
    }

    private static string CreateIsolatedTempDirectory()
    {
        return Directory.CreateTempSubdirectory("aspire-npm-").FullName;
    }

    private void CleanupTempDirectory(string tempDir)
    {
        try
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
        catch (IOException ex)
        {
            logger.LogDebug(ex, "Failed to clean up temporary directory: {TempDir}", tempDir);
        }
    }

    /// <summary>
    /// Creates a <see cref="ProcessStartInfo"/> configured to run an npm command.
    /// On Windows, .cmd files are invoked via cmd.exe /c for reliable stdout redirection.
    /// </summary>
    internal static ProcessStartInfo CreateNpmProcessStartInfo(string npmPath, string[] args, string workingDirectory, IEnvironment environment)
    {
        var startInfo = new ProcessStartInfo
        {
            // Redirect stdin so the child npm process (and any lifecycle scripts it invokes)
            // does not inherit the CLI's TTY. The caller closes stdin immediately after Start()
            // so any read surfaces as EOF instead of hanging waiting on the terminal. NpmRunner
            // is intended to be fully non-interactive. See https://github.com/microsoft/aspire/issues/16791.
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = workingDirectory
        };

        // On Windows, npm resolves to npm.cmd (a batch wrapper). Launching
        // .cmd files via Process.Start with redirected stdout can produce empty
        // output. Use cmd.exe /c to invoke the batch file reliably.
        // Note: cmd.exe /c has special quote-stripping rules that are incompatible
        // with ArgumentList (which individually quotes each argument). We must use
        // the Arguments string property and wrap the entire command in an outer set
        // of quotes so cmd.exe preserves interior quoting correctly.
        if (environment.IsWindows() && npmPath.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase))
        {
            startInfo.FileName = "cmd.exe";
            startInfo.Arguments = @$"/c """"{npmPath}"" {string.Join(" ", args.Select(a => @$"""{a}"""))}""";
        }
        else
        {
            startInfo.FileName = npmPath;
            foreach (var arg in args)
            {
                startInfo.ArgumentList.Add(arg);
            }
        }

        return startInfo;
    }

    /// <summary>
    /// Tries to extract the version string from npm view output. When a version range
    /// matches multiple versions, npm returns multi-line output in the format
    /// <c>@scope/pkg@version 'version'</c> per line, sorted ascending. This method
    /// returns the last (highest) version from such output, or the trimmed output
    /// when it contains a single version.
    /// </summary>
    internal static bool TryExtractLastVersion(string npmOutput, [NotNullWhen(true)] out string? version)
    {
        version = null;

        var lastLine = npmOutput
            .Split(['\n', '\r'], StringSplitOptions.RemoveEmptyEntries)
            .LastOrDefault()?
            .Trim();

        if (string.IsNullOrEmpty(lastLine))
        {
            return false;
        }

        // Multi-version format: "@scope/pkg@version 'version'" — extract the quoted version.
        // Single-version format: just "version" — return as-is.
        var quoteStart = lastLine.IndexOf('\'');
        if (quoteStart >= 0)
        {
            var quoteEnd = lastLine.IndexOf('\'', quoteStart + 1);
            if (quoteEnd > quoteStart)
            {
                version = lastLine[(quoteStart + 1)..quoteEnd];
                return !string.IsNullOrEmpty(version);
            }
        }

        version = lastLine;
        return true;
    }

    private async Task<string?> RunNpmCommandInDirectoryAsync(string npmPath, string[] args, string workingDirectory, CancellationToken cancellationToken)
    {
        var argsString = string.Join(" ", args);
        logger.LogDebug("Running npm {Args} in {WorkingDirectory}", argsString, workingDirectory);

        try
        {
            var startInfo = CreateNpmProcessStartInfo(npmPath, args, workingDirectory, environment);

            using var process = new Process { StartInfo = startInfo };
            using var activity = profilingTelemetry.StartNpmCommand(npmPath, args, workingDirectory);
            process.Start();
            // Close stdin so any npm lifecycle script that tries to read terminal input
            // sees EOF instead of blocking on the inherited TTY. See ProcessGuestLauncher
            // and https://github.com/microsoft/aspire/issues/16791.
            try
            {
                process.StandardInput.Close();
            }
            catch (IOException)
            {
                // The child may have already closed its stdin; ignore.
            }
            activity.SetProcessId(process.Id);

            // Read both streams concurrently to avoid deadlock when a pipe buffer fills.
            var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);

            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            activity.SetProcessExitCode(process.ExitCode);

            if (process.ExitCode != 0)
            {
                activity.SetError($"npm exited with code {process.ExitCode}.");
                var errorOutput = await errorTask.ConfigureAwait(false);
                logger.LogDebug("npm {Args} returned non-zero exit code {ExitCode}: {Error}", argsString, process.ExitCode, errorOutput.Trim());
                return null;
            }

            return await outputTask.ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            logger.LogDebug(ex, "Failed to run npm {Args}", argsString);
            return null;
        }
    }

}
