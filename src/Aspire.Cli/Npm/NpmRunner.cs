// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Logging;

namespace Aspire.Cli.Npm;

/// <summary>
/// Runs npm CLI commands for package management operations.
/// </summary>
internal sealed class NpmRunner(ILogger<NpmRunner> logger) : INpmRunner
{
    /// <summary>
    /// The public npm registry URL. Commands that resolve packages from the registry
    /// pass this explicitly via <c>--registry</c> to avoid inheriting a project-level
    /// <c>.npmrc</c> that may redirect to a private feed (e.g. Azure DevOps).
    /// </summary>
    private const string PublicRegistry = "https://registry.npmjs.org/";

    private readonly Lazy<string?> _npmPath = new(() => PathLookupHelper.FindFullPathFromPath("npm"));

    /// <inheritdoc />
    public bool IsAvailable => _npmPath.Value is not null;

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

            // Resolve integrity hash: npm view <package>@<version> dist.integrity
            var integrityOutput = await RunNpmCommandInDirectoryAsync(
                npmPath,
                ["view", NpmPackageInfo.FormatPackageSpecifier(packageName, version), "dist.integrity", "--registry", PublicRegistry],
                tempDir,
                cancellationToken);

            if (string.IsNullOrWhiteSpace(integrityOutput))
            {
                logger.LogDebug("Could not resolve integrity hash for {PackageSpecifier}", NpmPackageInfo.FormatPackageSpecifier(packageName, version));
                return null;
            }

            logger.LogDebug("Resolved {PackageSpecifier} with integrity {Integrity}", NpmPackageInfo.FormatPackageSpecifier(packageName, version), integrityOutput.Trim());

            return new NpmPackageInfo
            {
                Version = version,
                Integrity = integrityOutput.Trim()
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
    public async Task<bool> AuditSignaturesAsync(string packageName, string version, CancellationToken cancellationToken)
    {
        var npmPath = FindNpmPath();
        if (npmPath is null)
        {
            return false;
        }

        logger.LogDebug("Auditing npm signatures for {PackageSpecifier}", NpmPackageInfo.FormatPackageSpecifier(packageName, version));

        // npm audit signatures requires a project context (node_modules + package-lock.json).
        // For global tool installs there is no project, so we create a temporary one.
        // The package must be installed from the registry (not a local tarball) because
        // npm audit signatures skips packages with "resolved: file:..." in the lockfile.
        var tempDir = CreateIsolatedTempDirectory();

        try
        {
            // Create minimal package.json
            var packageJson = Path.Combine(tempDir, "package.json");
            await File.WriteAllTextAsync(
                packageJson,
                """{"name":"aspire-verify","version":"1.0.0","private":true}""",
                cancellationToken).ConfigureAwait(false);

            // Install the package from the registry to get proper attestation metadata
            var installOutput = await RunNpmCommandInDirectoryAsync(
                npmPath,
                ["install", NpmPackageInfo.FormatPackageSpecifier(packageName, version), "--ignore-scripts", "--registry", PublicRegistry],
                tempDir,
                cancellationToken);

            if (installOutput is null)
            {
                logger.LogDebug("Failed to install {PackageSpecifier} into temporary project for audit", NpmPackageInfo.FormatPackageSpecifier(packageName, version));
                return false;
            }

            // Run npm audit signatures in the temporary project directory
            var auditOutput = await RunNpmCommandInDirectoryAsync(
                npmPath,
                ["audit", "signatures"],
                tempDir,
                cancellationToken);

            if (auditOutput is null)
            {
                logger.LogDebug("Signature audit failed for {PackageSpecifier}", NpmPackageInfo.FormatPackageSpecifier(packageName, version));
                return false;
            }

            logger.LogDebug("Signature audit passed for {PackageSpecifier}", NpmPackageInfo.FormatPackageSpecifier(packageName, version));
            return true;
        }
        finally
        {
            CleanupTempDirectory(tempDir);
        }
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
            var output = await RunNpmCommandInDirectoryAsync(
                npmPath,
                ["install", "-g", tarballPath],
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
        var tempDir = Path.Combine(Path.GetTempPath(), $"aspire-npm-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        return tempDir;
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
    internal static ProcessStartInfo CreateNpmProcessStartInfo(string npmPath, string[] args, string workingDirectory)
    {
        var startInfo = new ProcessStartInfo
        {
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
        if (OperatingSystem.IsWindows() && npmPath.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase))
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
            var startInfo = CreateNpmProcessStartInfo(npmPath, args, workingDirectory);

            using var process = new Process { StartInfo = startInfo };
            process.Start();

            var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);

            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

            if (process.ExitCode != 0)
            {
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
