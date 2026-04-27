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
/// Subcommand that trusts the HTTPS development certificate, creating one if necessary.
/// </summary>
internal sealed class CertificatesTrustCommand : BaseCommand
{
    private readonly ICertificateService _certificateService;

    public CertificatesTrustCommand(ICertificateService certificateService, IInteractionService interactionService, IFeatures features, ICliUpdateNotifier updateNotifier, CliExecutionContext executionContext, AspireCliTelemetry telemetry)
        : base("trust", CertificatesCommandStrings.TrustDescription, features, updateNotifier, executionContext, interactionService, telemetry)
    {
        _certificateService = certificateService;
    }

    protected override bool UpdateNotificationsEnabled => false;

    protected override async Task<int> ExecuteAsync(ParseResult parseResult, CancellationToken cancellationToken)
    {
        InteractionService.DisplayMessage(KnownEmojis.Information, CertificatesCommandStrings.TrustProgress);

        var result = await _certificateService.EnsureCertificatesTrustedAsync(cancellationToken);

        if (result.Success)
        {
            InteractionService.DisplaySuccess(CertificatesCommandStrings.TrustSuccess);
            return ExitCodeConstants.Success;
        }

        if (result.WasCancelled)
        {
            return ExitCodeConstants.FailedToTrustCertificates;
        }

        var details = string.Format(CultureInfo.CurrentCulture, CertificatesCommandStrings.TrustFailureDetailsFormat, result.ResultCode);
        InteractionService.DisplayError(details);
        InteractionService.DisplayError(CertificatesCommandStrings.TrustFailure);
        return ExitCodeConstants.FailedToTrustCertificates;
    }
}
