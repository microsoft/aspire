// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.CommandLine;
using System.CommandLine.Invocation;
using System.Text.Json;
using Aspire.Cli.Acquisition;
using Aspire.Cli.Interaction;
using Microsoft.Extensions.Logging;

namespace Aspire.Cli.Commands;

/// <summary>
/// Handles the root <c>--info</c> option:
/// <list type="bullet">
///   <item><c>aspire --info</c> — text: version + channel + install table.</item>
///   <item><c>aspire --info --format json</c> — JSON object: <c>{ version, channel, installs[] }</c>.</item>
///   <item><c>aspire --info --self</c> — text: just the running CLI's row.</item>
///   <item><c>aspire --info --self --format json</c> — single-element <see cref="InstallationInfo"/>
///     array. This shape is the cross-version peer-probe contract consumed by
///     <see cref="PeerInstallProbe"/>; it intentionally omits the version/channel
///     summary that the human form shows.</item>
/// </list>
/// </summary>
/// <remarks>
/// <para>
/// Wired as the <see cref="Option.Action"/> on <see cref="RootCommand.InfoOption"/>
/// so it short-circuits normal subcommand routing the same way <c>--help</c> and
/// <c>--version</c> do.
/// </para>
/// <para>
/// The action injects <see cref="IIdentityChannelReader"/> directly instead of
/// reading <see cref="CliExecutionContext.IdentityChannel"/> so a CLI binary with
/// missing/invalid <c>AspireCliChannel</c> assembly metadata can still render
/// <c>--info</c> output. The factory in <c>Program.cs</c> wraps the eager
/// <see cref="IIdentityChannelReader.ReadChannel"/> call in a <see cref="Lazy{T}"/>
/// so <see cref="CliExecutionContext"/> resolves even when channel metadata is broken;
/// this action then catches the <see cref="InvalidOperationException"/> from
/// <see cref="IIdentityChannelReader.ReadChannel"/> and emits the row with the
/// <c>channel</c> field omitted (JSON convention — null fields are dropped) or the
/// <c>Channel</c> text row skipped, while still exiting 0.
/// </para>
/// </remarks>
internal sealed class InfoOptionAction : AsynchronousCommandLineAction
{
    private readonly IInteractionService _interactionService;
    private readonly IInstallationDiscovery _installationDiscovery;
    private readonly HiveEnumerator _hiveEnumerator;
    private readonly IIdentityChannelReader _identityChannelReader;
    private readonly ILogger<InfoOptionAction> _logger;
    private Option<bool>? _selfOption;
    private Option<InfoOutputFormat>? _formatOption;

    public InfoOptionAction(
        IInteractionService interactionService,
        IInstallationDiscovery installationDiscovery,
        HiveEnumerator hiveEnumerator,
        IIdentityChannelReader identityChannelReader,
        ILogger<InfoOptionAction> logger)
    {
        ArgumentNullException.ThrowIfNull(interactionService);
        ArgumentNullException.ThrowIfNull(installationDiscovery);
        ArgumentNullException.ThrowIfNull(hiveEnumerator);
        ArgumentNullException.ThrowIfNull(identityChannelReader);
        ArgumentNullException.ThrowIfNull(logger);
        _interactionService = interactionService;
        _installationDiscovery = installationDiscovery;
        _hiveEnumerator = hiveEnumerator;
        _identityChannelReader = identityChannelReader;
        _logger = logger;
    }

    /// <summary>
    /// Binds the <c>--self</c> and <c>--format</c> option instances that the
    /// action will read from <see cref="ParseResult"/>. Called by
    /// <see cref="RootCommand"/> at construction time after the options are
    /// created. The options are per-RootCommand instance so that concurrent
    /// tests building their own <see cref="RootCommand"/> instances don't
    /// race on the action wiring.
    /// </summary>
    public void BindOptions(Option<bool> selfOption, Option<InfoOutputFormat> formatOption)
    {
        ArgumentNullException.ThrowIfNull(selfOption);
        ArgumentNullException.ThrowIfNull(formatOption);
        _selfOption = selfOption;
        _formatOption = formatOption;
    }

    public override async Task<int> InvokeAsync(ParseResult parseResult, CancellationToken cancellationToken)
    {
        if (_selfOption is null || _formatOption is null)
        {
            throw new InvalidOperationException($"{nameof(InfoOptionAction)} was invoked before {nameof(BindOptions)} was called.");
        }

        var format = parseResult.GetValue(_formatOption);
        var self = parseResult.GetValue(_selfOption);

        if (self)
        {
            return WriteSelf(format);
        }

        return await WriteFullInfoAsync(format, cancellationToken).ConfigureAwait(false);
    }

    private int WriteSelf(InfoOutputFormat format)
    {
        var rows = InstallationInfoOutput.DescribeSelfSafely(_installationDiscovery, _logger);
        if (format == InfoOutputFormat.Json)
        {
            // Single-element array. Bit-for-bit identical to the previous
            // `aspire --info --self --format json` contract so the
            // PeerInstallProbe.TryParseRichProbeResult parser keeps working
            // across CLI versions without a fallback.
            var json = JsonSerializer.Serialize(rows.ToArray(), JsonSourceGenerationContext.RelaxedEscaping.InstallationInfoArray);
            _interactionService.DisplayRawText(json, ConsoleOutput.Standard);
            return CliExitCodes.Success;
        }

        foreach (var install in rows)
        {
            _interactionService.DisplayMarkdown("**self**");
            DisplayField("Status", install.Status);
            DisplayField("Channel", install.Channel);
            DisplayField("Source", install.Source);
            DisplayField("Version", install.Version);
            DisplayField("Path", install.Path);
            DisplayField("On PATH", install.PathStatus);
        }

        return CliExitCodes.Success;
    }

    private async Task<int> WriteFullInfoAsync(InfoOutputFormat format, CancellationToken cancellationToken)
    {
        var version = TryDescribeSelfVersion();
        var channel = TryReadChannel();
        var rows = await InstallationInfoOutput.BuildRowsSafelyAsync(_hiveEnumerator, _installationDiscovery, _logger, cancellationToken).ConfigureAwait(false);

        if (format == InfoOutputFormat.Json)
        {
            var payload = new InfoOutput(version, channel, rows);
            var json = JsonSerializer.Serialize(payload, JsonSourceGenerationContext.RelaxedEscaping.InfoOutput);
            _interactionService.DisplayRawText(json, ConsoleOutput.Standard);
            return CliExitCodes.Success;
        }

        DisplayField("Version", version);
        DisplayField("Channel", channel);
        _interactionService.DisplayEmptyLine();

        foreach (var row in rows)
        {
            _interactionService.DisplayMarkdown($"**{row.Id}**  {row.Kind}");
            DisplayField("Status", row.Status);
            DisplayField("Channel", row.Channel);
            DisplayField("Version", row.Version);
            DisplayField("Path", row.Path);
            DisplayField("Hive", row.Hive);
            DisplayField("On PATH", row.PathStatus);
            if (row.StatusReason is { Length: > 0 })
            {
                DisplayField("Reason", row.StatusReason);
            }
            _interactionService.DisplayEmptyLine();
        }

        return CliExitCodes.Success;
    }

    private string? TryDescribeSelfVersion()
    {
        try
        {
            return _installationDiscovery.DescribeSelf().Version;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Match the tolerant posture of the channel-read path: a broken
            // discovery shouldn't take the diagnostic command down with it.
            _logger.LogWarning(ex, "Could not read the running Aspire CLI version for `aspire --info`.");
            return null;
        }
    }

    private string? TryReadChannel()
    {
        try
        {
            return _identityChannelReader.ReadChannel();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Identity channel comes from AssemblyMetadata that the build system
            // bakes; a missing/invalid value is a broken-binary signal but
            // `aspire --info` is the surface a user reaches for to *find out*
            // their binary is broken, so we must not crash here. The channel is
            // dropped from JSON via DefaultIgnoreCondition.WhenWritingNull and
            // omitted from the text rendering by `DisplayField`.
            _logger.LogWarning(ex, "Could not read the running Aspire CLI identity channel for `aspire --info`.");
            return null;
        }
    }

    private void DisplayField(string name, string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return;
        }

        _interactionService.DisplayPlainText($"  {name,-8} {value}");
    }
}
