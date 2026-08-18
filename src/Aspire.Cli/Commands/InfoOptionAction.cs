// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;
using Aspire.Cli.Acquisition;
using Aspire.Cli.Interaction;
using Microsoft.Extensions.Logging;

namespace Aspire.Cli.Commands;

/// <summary>
/// Output format for <c>aspire --info</c>. Distinct from <see cref="OutputFormat"/>
/// (the <c>table</c>/<c>json</c> format used by <c>doctor --format</c>): <c>--info</c>'s
/// human rendering is a list of labeled sections, not a bordered table.
/// </summary>
internal enum InfoOutputFormat
{
    /// <summary>Human-readable list rendering. The default.</summary>
    List,

    /// <summary>Machine-readable JSON.</summary>
    Json,
}

/// <summary>
/// Implements <c>aspire --info [--self] [--format list|json]</c>: reports the running
/// CLI's version and channel, plus either a bounded discovery of all known Aspire CLI
/// installations and hive directories (default), or just the running CLI's own row
/// (<c>--self</c>).
/// </summary>
internal sealed class InfoOptionAction
{
    private readonly IInstallationDiscovery _installationDiscovery;
    private readonly IHiveEnumerator _hiveEnumerator;
    private readonly WingetFirstRunProbe _wingetFirstRunProbe;
    private readonly CliExecutionContext _executionContext;
    private readonly IInteractionService _interactionService;
    private readonly ILogger<InfoOptionAction> _logger;

    public InfoOptionAction(
        IInstallationDiscovery installationDiscovery,
        IHiveEnumerator hiveEnumerator,
        WingetFirstRunProbe wingetFirstRunProbe,
        CliExecutionContext executionContext,
        IInteractionService interactionService,
        ILogger<InfoOptionAction> logger)
    {
        _installationDiscovery = installationDiscovery;
        _hiveEnumerator = hiveEnumerator;
        _wingetFirstRunProbe = wingetFirstRunProbe;
        _executionContext = executionContext;
        _interactionService = interactionService;
        _logger = logger;
    }

    public async Task<int> ExecuteAsync(bool selfOnly, InfoOutputFormat format, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        IReadOnlyList<InfoInstallation> rows;

        if (selfOnly)
        {
            // Self mode never runs full discovery: it probes only the running CLI,
            // so it stays fast and cannot be affected by peer installations that
            // are slow, broken, or absent.
            var self = InstallationInfoOutput.DescribeSelfAsInfoInstallation(
                _installationDiscovery,
                _logger);
            rows = [self];
        }
        else
        {
            var discoveryResult = await InstallationInfoOutput.DiscoverAllToResultSafelyAsync(
                _installationDiscovery,
                _hiveEnumerator,
                _wingetFirstRunProbe,
                _logger,
                cancellationToken).ConfigureAwait(false);
            rows = InstallationInfoOutput.BuildInfoRows(discoveryResult, discoveryResult.Hives);
        }

        if (format == InfoOutputFormat.Json)
        {
            WriteJson(selfOnly, rows);
        }
        else
        {
            InstallationInfoOutput.DisplayHumanReadable(_interactionService, _executionContext, rows);
        }

        return CliExitCodes.Success;
    }

    private void WriteJson(bool selfOnly, IReadOnlyList<InfoInstallation> rows)
    {
        string json;
        if (selfOnly)
        {
            // Self mode's JSON contract is a bare array, not the {version, channel,
            // installs} envelope: there is exactly one row and no aggregate metadata
            // to report alongside it.
            json = JsonSerializer.Serialize(rows.ToArray(), JsonSourceGenerationContext.RelaxedEscaping.InfoInstallationArray);
        }
        else
        {
            var output = new InfoOutput
            {
                Version = _executionContext.IdentityVersion,
                Channel = _executionContext.IdentityChannel,
                Installs = rows.ToArray(),
            };
            json = JsonSerializer.Serialize(output, JsonSourceGenerationContext.RelaxedEscaping.InfoOutput);
        }

        _interactionService.DisplayPlainText(json);
    }
}
