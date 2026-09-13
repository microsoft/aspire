// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using Aspire.Cli.Bundles;
using Aspire.Cli.Layout;
using Aspire.Cli.Resources;
using Aspire.Cli.Utils;
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
        if (!environment.IsMacOS())
        {
            return CommandResult.Failure(CliExitCodes.InvalidCommand, TrayCommandStrings.MacOSOnly);
        }

        string? cliPath = null;
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

            string[] arguments = start ? ["start", "--cli", cliPath!, "--bundle-root", bundleRoot] : ["stop"];
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
}
