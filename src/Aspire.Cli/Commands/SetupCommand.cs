// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.CommandLine;
using Aspire.Cli.Bundles;
using Aspire.Cli.Configuration;
using Aspire.Cli.Interaction;
using Aspire.Cli.Telemetry;
using Aspire.Cli.Utils;

namespace Aspire.Cli.Commands;

/// <summary>
/// Extracts the embedded bundle payload from a self-extracting Aspire CLI binary.
/// </summary>
internal sealed class SetupCommand : BaseCommand
{
    private readonly IBundleService _bundleService;

    private static readonly Option<string?> s_installPathOption = new("--install-path")
    {
        Description = "Directory to extract the bundle into. Defaults to the parent of the CLI binary's directory. Non-default paths require ASPIRE_LAYOUT_PATH to be set for auto-discovery."
    };

    private static readonly Option<bool> s_forceOption = new("--force")
    {
        Description = "Force extraction even if the layout already exists"
    };

    public SetupCommand(
        IBundleService bundleService,
        IFeatures features,
        ICliUpdateNotifier updateNotifier,
        CliExecutionContext executionContext,
        IInteractionService interactionService,
        AspireCliTelemetry telemetry)
        : base("setup", "Extract the embedded bundle to set up the Aspire CLI runtime", features, updateNotifier, executionContext, interactionService, telemetry)
    {
        // Hidden: the setup command is an implementation detail used by install scripts.
        Hidden = true;
        _bundleService = bundleService;

        Options.Add(s_installPathOption);
        Options.Add(s_forceOption);
    }

    protected override async Task<CommandResult> ExecuteAsync(ParseResult parseResult, CancellationToken cancellationToken)
    {
        var installPath = parseResult.GetValue(s_installPathOption);
        var force = parseResult.GetValue(s_forceOption);

        var processPath = Environment.ProcessPath;
        if (string.IsNullOrEmpty(processPath))
        {
            return CommandResult.Failure(CliExitCodes.FailedToBuildArtifacts, "Could not determine the CLI executable path.");
        }

        // `aspire setup` uses a route-independent default (parent of the binary's dir).
        // Do not switch to `_bundleService.GetDefaultExtractDir` — that path is route-aware
        // and reserved for auto-extract, where managed-route layouts must stay package-owned.
        if (string.IsNullOrEmpty(installPath))
        {
            installPath = GetDefaultInstallPath(processPath);
        }

        if (string.IsNullOrEmpty(installPath))
        {
            return CommandResult.Failure(CliExitCodes.FailedToBuildArtifacts, "Could not determine the installation path.");
        }

        // Extract with spinner
        BundleExtractResult result = BundleExtractResult.NoPayload;
        var exitCode = await InteractionService.ShowStatusAsync(
            "Extracting Aspire bundle...",
            async () =>
            {
                result = await _bundleService.ExtractAsync(installPath, force, cancellationToken);
                return CommandResult.Success();
            }, emoji: KnownEmojis.Package);

        switch (result)
        {
            case BundleExtractResult.NoPayload:
                InteractionService.DisplayMessage(KnownEmojis.Information, "This CLI binary does not contain an embedded bundle. No extraction needed.");
                break;

            case BundleExtractResult.AlreadyUpToDate:
                InteractionService.DisplayMessage(KnownEmojis.CheckMarkButton, "Bundle is already extracted and up to date. Use --force to re-extract.");
                break;

            case BundleExtractResult.Extracted:
                InteractionService.DisplayMessage(KnownEmojis.CheckMarkButton, $"Bundle extracted to {installPath}");
                break;

            case BundleExtractResult.ExtractionFailed:
                return CommandResult.Failure(CliExitCodes.FailedToBuildArtifacts, $"Bundle was extracted to {installPath} but layout validation failed.");
        }

        return exitCode;
    }

    /// <summary>
    /// Returns the parent of <paramref name="processPath"/>'s directory, or <c>null</c> if
    /// none. Route-independent counterpart to the route-aware <see cref="IBundleService.GetDefaultExtractDir"/>.
    /// </summary>
    internal static string? GetDefaultInstallPath(string? processPath)
    {
        if (string.IsNullOrEmpty(processPath))
        {
            return null;
        }

        var binaryDir = Path.GetDirectoryName(processPath);
        return string.IsNullOrEmpty(binaryDir) ? null : Path.GetDirectoryName(binaryDir);
    }
}
