// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.CommandLine;
using System.Globalization;
using Aspire.Cli.Certificates;
using Aspire.Cli.Interaction;
using Aspire.Cli.Resources;
using Microsoft.AspNetCore.Certificates.Generation;

namespace Aspire.Cli.Commands;

/// <summary>
/// Subcommand that trusts the HTTPS development certificate, creating one if necessary.
/// </summary>
internal sealed class CertificatesTrustCommand : BaseCommand
{
    private readonly ICertificateService _certificateService;

    public CertificatesTrustCommand(ICertificateService certificateService,
        CommonCommandServices services)
        : base("trust", CertificatesCommandStrings.TrustDescription, services)
    {
        _certificateService = certificateService;
    }

    protected override async Task<CommandResult> ExecuteAsync(ParseResult parseResult, CancellationToken cancellationToken)
    {
        InteractionService.DisplayMessage(KnownEmojis.Information, CertificatesCommandStrings.TrustProgress);

        var result = await _certificateService.EnsureCertificatesTrustedAsync(cancellationToken);

        if (result.Success)
        {
            if (result.ResultCode == EnsureCertificateResult.PartiallyFailedToTrustTheCertificate)
            {
                InteractionService.DisplayMessage(KnownEmojis.Warning, CertificatesCommandStrings.TrustPartialSuccess);
            }
            else
            {
                InteractionService.DisplaySuccess(CertificatesCommandStrings.TrustSuccess);
            }

            return CommandResult.Success();
        }

        if (result.WasCancelled)
        {
            return CommandResult.Failure(CliExitCodes.FailedToTrustCertificates);
        }

        var details = string.Format(CultureInfo.CurrentCulture, CertificatesCommandStrings.TrustFailureDetailsFormat, result.ResultCode);
        return CommandResult.Failure(CliExitCodes.FailedToTrustCertificates, $"{CertificatesCommandStrings.TrustFailure} {details}");
    }
}
