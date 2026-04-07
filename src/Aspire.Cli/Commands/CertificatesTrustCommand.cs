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
using Microsoft.AspNetCore.Certificates.Generation;

namespace Aspire.Cli.Commands;

/// <summary>
/// Subcommand that trusts the HTTPS development certificate, creating one if necessary.
/// </summary>
internal sealed class CertificatesTrustCommand : BaseCommand
{
    private readonly ICertificateToolRunner _certificateToolRunner;

    public CertificatesTrustCommand(ICertificateToolRunner certificateToolRunner, IInteractionService interactionService, IFeatures features, ICliUpdateNotifier updateNotifier, CliExecutionContext executionContext, AspireCliTelemetry telemetry)
        : base("trust", CertificatesCommandStrings.TrustDescription, features, updateNotifier, executionContext, interactionService, telemetry)
    {
        _certificateToolRunner = certificateToolRunner;
    }

    protected override bool UpdateNotificationsEnabled => false;

    protected override Task<int> ExecuteAsync(ParseResult parseResult, CancellationToken cancellationToken)
    {
        InteractionService.DisplayMessage(KnownEmojis.Information, CertificatesCommandStrings.TrustProgress);

        var result = _certificateToolRunner.TrustHttpCertificate();

        if (CertificateHelpers.IsSuccessfulTrustResult(result))
        {
            InteractionService.DisplaySuccess(CertificatesCommandStrings.TrustSuccess);
            return Task.FromResult(ExitCodeConstants.Success);
        }

        if (result == EnsureCertificateResult.UserCancelledTrustStep)
        {
            InteractionService.DisplayMessage(KnownEmojis.Warning, CertificatesCommandStrings.TrustCancelled);
            return Task.FromResult(ExitCodeConstants.FailedToTrustCertificates);
        }

        var details = string.Format(CultureInfo.CurrentCulture, CertificatesCommandStrings.TrustFailureDetailsFormat, result);
        InteractionService.DisplayError(details);
        InteractionService.DisplayError(CertificatesCommandStrings.TrustFailure);
        return Task.FromResult(ExitCodeConstants.FailedToTrustCertificates);
    }
}
