// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Layout;
using Microsoft.Extensions.Logging;

namespace Aspire.Cli.Bundles;

internal static class BundleLayoutRepairHelper
{
    public static async Task<string> EnsureManagedToolPathAsync(
        IBundleService bundleService,
        ILogger logger,
        string operationName,
        CancellationToken cancellationToken,
        bool allowRepair = true,
        string? bundleRoot = null)
    {
        var layout = await bundleService.EnsureExtractedAndGetLayoutAsync(cancellationToken).ConfigureAwait(false);
        var managedPath = layout?.GetManagedPath();
        bundleRoot ??= layout?.LayoutPath;

        if (managedPath is not null && File.Exists(managedPath))
        {
            return managedPath;
        }

        if (allowRepair && await TryRepairAsync(bundleService, logger, operationName, cancellationToken, bundleRoot).ConfigureAwait(false))
        {
            return await EnsureManagedToolPathAsync(bundleService, logger, operationName, cancellationToken, allowRepair: false, bundleRoot).ConfigureAwait(false);
        }

        throw new InvalidOperationException("aspire-managed not found in layout.");
    }

    public static async Task<(string ManagedPath, int ExitCode, string Output, string Error)> RunManagedToolWithRepairAsync(
        IBundleService bundleService,
        LayoutProcessRunner processRunner,
        ILogger logger,
        string operationName,
        IEnumerable<string> arguments,
        string? workingDirectory = null,
        IDictionary<string, string>? environmentVariables = null,
        string? managedPath = null,
        CancellationToken cancellationToken = default)
    {
        managedPath ??= await EnsureManagedToolPathAsync(bundleService, logger, operationName, cancellationToken).ConfigureAwait(false);
        var bundleRoot = GetBundleRootFromManagedPath(managedPath);

        var (exitCode, output, error) = await processRunner.RunAsync(
            managedPath,
            arguments,
            workingDirectory: workingDirectory,
            environmentVariables: environmentVariables,
            ct: cancellationToken).ConfigureAwait(false);

        if (exitCode == 0 || !BundleService.LooksLikeBundleCorruption(error: error, output: output, bundleRoot: bundleRoot))
        {
            return (managedPath, exitCode, output, error);
        }

        if (!await TryRepairAsync(bundleService, logger, operationName, cancellationToken, bundleRoot).ConfigureAwait(false))
        {
            return (managedPath, exitCode, output, error);
        }

        managedPath = await EnsureManagedToolPathAsync(bundleService, logger, operationName, cancellationToken, allowRepair: false, bundleRoot).ConfigureAwait(false);
        logger.LogWarning("Retrying {OperationName} after repairing the bundled layout.", operationName);

        (exitCode, output, error) = await processRunner.RunAsync(
            managedPath,
            arguments,
            workingDirectory: workingDirectory,
            environmentVariables: environmentVariables,
            ct: cancellationToken).ConfigureAwait(false);

        return (managedPath, exitCode, output, error);
    }

    public static async Task<bool> TryRepairAsync(
        IBundleService bundleService,
        ILogger logger,
        string operationName,
        CancellationToken cancellationToken,
        string? bundleRoot = null)
    {
        if (!bundleService.IsBundle)
        {
            return false;
        }

        var bundleState = bundleService.GetLayoutState(bundleRoot);
        if (!bundleState.HasKnownLayoutPath)
        {
            logger.LogWarning("Unable to repair the bundled layout while {OperationName} because the bundle root could not be determined. {BundleState}",
                operationName,
                bundleState.Describe());
            return false;
        }

        logger.LogWarning("Detected an incomplete or inconsistent bundled layout while {OperationName}. Attempting forced bundle repair. {BundleState}",
            operationName,
            bundleState.Describe());

        var result = await bundleService.RepairAsync(bundleState.LayoutPath, cancellationToken).ConfigureAwait(false);
        var repairedState = bundleService.GetLayoutState(bundleState.LayoutPath);

        logger.LogInformation("Bundle repair result while {OperationName}: {Result}. {BundleState}",
            operationName,
            result,
            repairedState.Describe());

        return result is BundleExtractResult.Extracted or BundleExtractResult.AlreadyUpToDate;
    }

    private static string? GetBundleRootFromManagedPath(string managedPath)
    {
        var managedDirectory = Path.GetDirectoryName(managedPath);
        return string.IsNullOrEmpty(managedDirectory) ? null : Path.GetDirectoryName(managedDirectory);
    }
}
