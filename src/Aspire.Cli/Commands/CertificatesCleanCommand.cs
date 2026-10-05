// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.CommandLine;
using System.Globalization;
using Aspire.Cli.Certificates;
using Aspire.Cli.Interaction;
using Aspire.Cli.Resources;

namespace Aspire.Cli.Commands;

/// <summary>
/// Subcommand that removes all HTTPS development certificates.
/// </summary>
internal sealed class CertificatesCleanCommand : BaseCommand
{
    private readonly ICertificateToolRunner _certificateToolRunner;

    public CertificatesCleanCommand(ICertificateToolRunner certificateToolRunner, CommonCommandServices services)
        : base("clean", CertificatesCommandStrings.CleanDescription, services)
    {
        _certificateToolRunner = certificateToolRunner;
    }

    protected override Task<CommandResult> ExecuteAsync(ParseResult parseResult, CancellationToken cancellationToken)
    {
        InteractionService.DisplayMessage(KnownEmojis.Information, CertificatesCommandStrings.CleanProgress);

        var result = _certificateToolRunner.CleanHttpCertificate();

        if (result.Success)
        {
            InteractionService.DisplaySuccess(CertificatesCommandStrings.CleanSuccess);
            return Task.FromResult(CommandResult.FromExitCode(CliExitCodes.Success));
        }

        if (result.WasCancelled)
        {
            InteractionService.DisplayMessage(KnownEmojis.Warning, CertificatesCommandStrings.CleanCancelled);
            return Task.FromResult(CommandResult.FromExitCode(CliExitCodes.FailedToTrustCertificates));
        }

        var details = string.Format(CultureInfo.CurrentCulture, CertificatesCommandStrings.CleanFailureDetailsFormat, result.ErrorMessage);
        InteractionService.DisplayError(details);
        InteractionService.DisplayError(CertificatesCommandStrings.CleanFailure);
        return Task.FromResult(CommandResult.FromExitCode(CliExitCodes.FailedToTrustCertificates));
    }
}
