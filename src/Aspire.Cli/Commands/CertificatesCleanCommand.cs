// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.CommandLine;
using System.Globalization;
using Aspire.Cli.Certificates;
using Aspire.Cli.Configuration;
using Aspire.Cli.Interaction;
using Aspire.Cli.Resources;
using Aspire.Cli.Telemetry;
using Aspire.Cli.Utils;

namespace Aspire.Cli.Commands;

/// <summary>
/// Subcommand that removes all HTTPS development certificates.
/// </summary>
internal sealed class CertificatesCleanCommand : BaseCommand
{
    private readonly ICertificateToolRunner _certificateToolRunner;

    public CertificatesCleanCommand(ICertificateToolRunner certificateToolRunner, IInteractionService interactionService, IFeatures features, ICliUpdateNotifier updateNotifier, CliExecutionContext executionContext, AspireCliTelemetry telemetry)
        : base("clean", CertificatesCommandStrings.CleanDescription, features, updateNotifier, executionContext, interactionService, telemetry)
    {
        _certificateToolRunner = certificateToolRunner;
    }

    protected override bool UpdateNotificationsEnabled => false;

    protected override Task<int> ExecuteAsync(ParseResult parseResult, CancellationToken cancellationToken)
    {
        InteractionService.DisplayMessage(KnownEmojis.Information, CertificatesCommandStrings.CleanProgress);

        var result = _certificateToolRunner.CleanHttpCertificate();

        if (result.Success)
        {
            InteractionService.DisplaySuccess(CertificatesCommandStrings.CleanSuccess);
            return Task.FromResult(ExitCodeConstants.Success);
        }

        if (result.WasCancelled)
        {
            InteractionService.DisplayMessage(KnownEmojis.Warning, CertificatesCommandStrings.CleanCancelled);
            return Task.FromResult(ExitCodeConstants.FailedToTrustCertificates);
        }

        var details = string.Format(CultureInfo.CurrentCulture, CertificatesCommandStrings.CleanFailureDetailsFormat, result.ErrorMessage);
        InteractionService.DisplayError(details);
        InteractionService.DisplayError(CertificatesCommandStrings.CleanFailure);
        return Task.FromResult(ExitCodeConstants.FailedToTrustCertificates);
    }
}
