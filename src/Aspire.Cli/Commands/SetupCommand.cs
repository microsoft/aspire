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

    protected override async Task<int> ExecuteAsync(ParseResult parseResult, CancellationToken cancellationToken)
    {
        var installPath = parseResult.GetValue(s_installPathOption);
        var force = parseResult.GetValue(s_forceOption);

        var processPath = Environment.ProcessPath;
        if (string.IsNullOrEmpty(processPath))
        {
            InteractionService.DisplayError("Could not determine the CLI executable path.");
            return ExitCodeConstants.FailedToBuildArtifacts;
        }

        // Determine extraction directory
        if (string.IsNullOrEmpty(installPath))
        {
            installPath = BundleService.GetDefaultExtractDir(processPath);
        }

        if (string.IsNullOrEmpty(installPath))
        {
            InteractionService.DisplayError("Could not determine the installation path.");
            return ExitCodeConstants.FailedToBuildArtifacts;
        }

        // Extract with spinner
        BundleExtractResult result = BundleExtractResult.NoPayload;
        var exitCode = await InteractionService.ShowStatusAsync(
            "Extracting Aspire bundle...",
            async () =>
            {
                result = await _bundleService.ExtractAsync(installPath, force, cancellationToken);
                return ExitCodeConstants.Success;
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
                InteractionService.DisplayError($"Bundle was extracted to {installPath} but layout validation failed.");
                return ExitCodeConstants.FailedToBuildArtifacts;
        }

        return exitCode;
    }
}
