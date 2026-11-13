// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using Aspire.Dashboard.Configuration;
using Aspire.Dashboard.Model;
using Aspire.Dashboard.Model.Markdown;
using Aspire.Dashboard.Resources;
using Aspire.Hosting;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Options;

namespace Aspire.Dashboard.Components.Dialogs;

public partial class TelemetryHelpDialog
{
    // Intended redirect: https://opentelemetry.io/docs/specs/otlp/
    private const string OtlpSpecificationUrl = "https://aka.ms/aspire/otlp-spec";
    // Intended redirect: https://aspire.dev/dashboard/standalone/#send-telemetry-to-the-dashboard
    private const string SendTelemetryUrl = "https://aka.ms/aspire/dashboard-send-telemetry";
    // Intended redirect: https://aspire.dev/dashboard/explore/#manage-data
    private const string ManageDataUrl = "https://aka.ms/aspire/dashboard-manage-data";

    private MarkdownProcessor? _markdownProcessor;

    [Inject]
    public required IStringLocalizer<Resources.Dialogs> Loc { get; init; }

    [Inject]
    public required IStringLocalizer<ControlsStrings> ControlsLoc { get; init; }

    [Inject]
    public required IConfiguration Configuration { get; init; }

    [Inject]
    public required IOptionsMonitor<DashboardOptions> Options { get; init; }

    [Inject]
    public required OtlpEndpointInfo Endpoints { get; init; }

    [Inject]
    public required TelemetryImportService TelemetryImportService { get; init; }

    [Inject]
    public required DashboardDataSource DataSource { get; init; }

    [Inject]
    public required DashboardDialogService DialogService { get; init; }

    private bool CanImport => TelemetryImportService.IsImportEnabled && !DataSource.TelemetryRepository.IsReadOnly;

    private string GetHelpMarkdown()
    {
        var sections = new List<string>
        {
            string.Format(CultureInfo.CurrentCulture, Loc[nameof(Resources.Dialogs.TelemetryHelpIntroduction)],
                OtlpSpecificationUrl)
        };
        var grpcAddress = Endpoints.GrpcAddress;
        var httpAddress = Endpoints.HttpAddress;
        if (grpcAddress is not null || httpAddress is not null)
        {
            var rows = new List<string>
            {
                $"| {Loc[nameof(Resources.Dialogs.TelemetryHelpProtocolHeader)]} | {Loc[nameof(Resources.Dialogs.TelemetryHelpEndpointHeader)]} |",
                "| --- | --- |"
            };
            if (grpcAddress is not null)
            {
                rows.Add($"| OTLP/gRPC | `{grpcAddress}` |");
            }
            if (httpAddress is not null)
            {
                rows.Add($"| OTLP/HTTP | `{httpAddress}` |");
            }
            sections.Add(string.Join(Environment.NewLine, rows));

            if (Options.CurrentValue.Otlp.AuthMode is OtlpAuthMode.ApiKey)
            {
                sections.Add(Loc[nameof(Resources.Dialogs.TelemetryHelpApiKeyDescription)]);
            }
            else if (Options.CurrentValue.Otlp.AuthMode is OtlpAuthMode.ClientCertificate)
            {
                sections.Add(Loc[nameof(Resources.Dialogs.TelemetryHelpCertificateDescription)]);
            }
        }
        else
        {
            sections.Add(Loc[nameof(Resources.Dialogs.TelemetryHelpNoEndpointsDescription)]);
        }

        if (Configuration.GetBool(KnownConfigNames.DotnetRunningInContainer) is true)
        {
            sections.Add(Loc[nameof(Resources.Dialogs.TelemetryHelpContainerDescription)]);
        }
        sections.Add(string.Format(CultureInfo.CurrentCulture, "[{0}]({1})",
            Loc[nameof(Resources.Dialogs.TelemetryHelpSendTelemetryLink)], SendTelemetryUrl));
        if (CanImport)
        {
            sections.Add(string.Format(CultureInfo.CurrentCulture, Loc[nameof(Resources.Dialogs.TelemetryHelpImportDescription)],
                Loc[nameof(Resources.Dialogs.SettingsImportButtonText)], Loc[nameof(Resources.Dialogs.ManageDataDialogTitle)]));
            sections.Add(string.Format(CultureInfo.CurrentCulture, "[{0}]({1})",
                Loc[nameof(Resources.Dialogs.TelemetryHelpManageDataLink)], ManageDataUrl));
        }

        return string.Join(Environment.NewLine + Environment.NewLine, sections);
    }

    private MarkdownProcessor GetMarkdownProcessor()
    {
        return _markdownProcessor ??= new MarkdownProcessor(ControlsLoc, safeUrlSchemes: MarkdownHelpers.SafeUrlSchemes, extensions: []);
    }

    private Task OpenImportAsync() => DialogService.ShowManageDataAsync();
}
