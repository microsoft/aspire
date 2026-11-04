// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using System.Runtime.InteropServices;
using Aspire.Cli.Acquisition;
using Aspire.Cli.Bundles;
using Aspire.Cli.Layout;
using Aspire.Cli.Resources;
using Aspire.Cli.Utils;
using Aspire.Shared;
using Microsoft.Extensions.Logging;

namespace Aspire.Cli.Commands;

/// <summary>
/// Invokes the bundled tray's acknowledged lifecycle helpers while protecting their payload.
/// </summary>
internal sealed class TrayLifecycleService(
    IBundleService bundleService,
    LayoutProcessRunner processRunner,
    IEnvironment environment,
    IProcessPathProvider processPathProvider,
    TimeProvider timeProvider,
    ILogger<TrayLifecycleService> logger)
{
    internal static TimeSpan HelperTimeout { get; } = TimeSpan.FromSeconds(30);

    public async Task<CommandResult> ExecuteAsync(bool start, CancellationToken cancellationToken)
    {
        if ((!environment.IsMacOS() && !environment.IsWindows()) ||
            RuntimeInformation.ProcessArchitecture is not (Architecture.X64 or Architecture.Arm64))
        {
            return CommandResult.Failure(CliExitCodes.InvalidCommand, TrayCommandStrings.UnsupportedPlatform);
        }

        string? cliPath = null;
        string? startupCliPath = null;
        if (start)
        {
            var processPath = processPathProvider.ProcessPath;
            // A managed development entrypoint may be dotnet itself, or a runtime-dependent
            // apphost. Neither is the self-contained invoking CLI promised to the companion.
            if (!processPathProvider.IsNativeAot || string.IsNullOrEmpty(processPath) ||
                !Path.IsPathFullyQualified(processPath) || !File.Exists(processPath))
            {
                return CommandResult.Failure(CliExitCodes.InvalidCommand, TrayCommandStrings.NativeCliRequired);
            }

            // Capture the actual executable once; never re-resolve PATH or install a private CLI.
            cliPath = Path.GetFullPath(CliPathHelper.ResolveSymlinkOrOriginalPath(processPath, logger));
            startupCliPath = GetStartupCliPath(processPath, cliPath);
        }

        var action = start ? "start" : "stop";
        try
        {
            // Extraction and lease acquisition are atomic with respect to version cleanup.
            // Keep this lease until the short-lived helper has acknowledged UI readiness and
            // the GUI's own lease (start), or graceful GUI shutdown (stop), and has exited.
            using var lease = await bundleService.EnsureExtractedAndAcquireLayoutAsync(
                "cli", $"tray {action}", cancellationToken).ConfigureAwait(false);
            var layout = lease?.Layout;
            if (lease is null || !lease.HasLease || layout?.LayoutPath is not { } bundleRoot ||
                !Path.IsPathFullyQualified(bundleRoot))
            {
                return CommandResult.Failure(CliExitCodes.InvalidCommand, TrayCommandStrings.BundleRequired);
            }

            var trayPath = layout.GetTrayPath();
            if (trayPath is null || !Path.IsPathFullyQualified(trayPath) || !File.Exists(trayPath))
            {
                return CommandResult.Failure(CliExitCodes.InvalidCommand, TrayCommandStrings.PayloadMissing);
            }

            if (environment.IsWindows())
            {
                if (!string.Equals(trayPath, Path.Combine(bundleRoot, WindowsTrayPayload.ExecutablePath), StringComparison.OrdinalIgnoreCase))
                {
                    return CommandResult.Failure(CliExitCodes.InvalidCommand, TrayCommandStrings.PayloadMissing);
                }
                WindowsTrayPayload.Validate(Path.GetDirectoryName(trayPath)!,
                    RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "win-arm64" : "win-x64");
            }

            string[] arguments = start
                ? startupCliPath is null
                    ? ["start", "--cli", cliPath!, "--bundle-root", bundleRoot]
                    : ["start", "--cli", cliPath!, "--bundle-root", bundleRoot, "--startup-cli", startupCliPath]
                : ["stop"];
            var variables = new Dictionary<string, string>();
            lease.AddEnvironment(variables);
            cancellationToken.ThrowIfCancellationRequested();
            using var timeout = new CancellationTokenSource(HelperTimeout, timeProvider);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
            try
            {
                // Once start launches the detached GUI, cancellation must not kill its helper
                // before the GUI acquires its own lease. The helper has a bounded readiness
                // handshake; after this point wait for it using only our independent deadline.
                var helperToken = start ? timeout.Token : linked.Token;
                var (exitCode, output, error) = await processRunner.RunAsync(
                    trayPath, arguments, workingDirectory: bundleRoot,
                    environmentVariables: variables, ct: helperToken).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(output))
                {
                    logger.LogDebug("Tray helper stdout: {Output}", output.Trim());
                }
                if (!string.IsNullOrWhiteSpace(error))
                {
                    logger.LogDebug("Tray helper stderr: {Error}", error.Trim());
                }

                return exitCode == CliExitCodes.Success
                    ? CommandResult.Success()
                    : CommandResult.Failure(exitCode, string.IsNullOrWhiteSpace(error)
                        ? string.Format(CultureInfo.CurrentCulture, TrayCommandStrings.HelperFailed, action, exitCode)
                        : string.Format(CultureInfo.CurrentCulture, TrayCommandStrings.OperationFailed, action, error.Trim()));
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested && (start || !cancellationToken.IsCancellationRequested))
            {
                return CommandResult.Failure(CliExitCodes.WaitTimeout, string.Format(CultureInfo.CurrentCulture, TrayCommandStrings.HelperTimedOut, action));
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogDebug(ex, "Failed to invoke the tray {Action} helper.", action);
            return CommandResult.Failure(CliExitCodes.InvalidCommand, string.Format(CultureInfo.CurrentCulture, TrayCommandStrings.OperationFailed, action, ex.Message));
        }
    }

    private string? GetStartupCliPath(string invokingPath, string resolvedPath)
    {
        var directory = Path.GetDirectoryName(resolvedPath)!;
        var source = InstallSidecarReader.ReadSourceField(Path.Combine(directory, InstallSidecarReader.SidecarFileName));
        // Script/PR installers replace <prefix>/bin/aspire in place. Package-manager
        // binaries (npm node_modules, .NET tools/.store, Homebrew/Nix stores, etc.)
        // may be versioned even when the process is NativeAOT. Do not register those
        // paths or guess a shim from PATH. Preserve a supplied installation symlink
        // only when its actual binary has the stable portable-install contract.
        // https://github.com/microsoft/aspire/blob/main/docs/specs/install-routes.md
        if (source is not (InstallSourceExtensions.ScriptWire or InstallSourceExtensions.PrWire or InstallSourceExtensions.LocalHiveWire)
            || !string.Equals(Path.GetFileName(directory), "bin", StringComparison.OrdinalIgnoreCase)
            || IsPackageStorePath(invokingPath) || IsPackageStorePath(resolvedPath))
        {
            logger.LogDebug("Tray launch-at-sign-in is unavailable: the invoking CLI has no verified stable portable installation entry point.");
            return null;
        }

        return Path.GetFullPath(invokingPath);
    }

    private static bool IsPackageStorePath(string path)
        => Path.GetFullPath(path).Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries).Any(component =>
                component.Equals("node_modules", StringComparison.OrdinalIgnoreCase)
                || component.Equals(".store", StringComparison.OrdinalIgnoreCase));
}
