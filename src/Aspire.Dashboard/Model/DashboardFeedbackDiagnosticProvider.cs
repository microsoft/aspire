// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using Aspire.Dashboard.Utils;
using Aspire.Hosting;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Localization;
using LayoutResources = Aspire.Dashboard.Resources.Layout;

namespace Aspire.Dashboard.Model;

internal interface IDashboardFeedbackDiagnosticProvider
{
    /// <summary>
    /// Builds the additional-context lines (environment and dashboard route) for a feedback issue.
    /// When <paramref name="includeAppHostInfo"/> is <see langword="true"/> and the AppHost forwarded a
    /// description (via <c>DASHBOARD__APPHOST__INFO</c>), a <c>- AppHost: ...</c> line is included.
    /// </summary>
    string BuildAdditionalContext(bool includeAppHostInfo);

    /// <summary>
    /// Gets a value indicating whether <c>aspire doctor</c> output can be captured. This is only true
    /// when the AppHost forwarded a CLI path (via <c>DASHBOARD__CLI__PATH</c>) — either the CLI that
    /// launched the AppHost or an <c>aspire</c> the AppHost resolved on PATH; the dashboard never probes
    /// for a CLI itself. Callers should check this before showing a doctor-output field or calling
    /// <see cref="CaptureAspireDoctorOutputAsync"/>.
    /// </summary>
    bool IsAspireDoctorOutputAvailable { get; }

    /// <summary>
    /// Captures the output of <c>aspire doctor --format json</c> for inclusion in a bug report. Only
    /// call this when <see cref="IsAspireDoctorOutputAvailable"/> is <see langword="true"/>.
    /// </summary>
    Task<string> CaptureAspireDoctorOutputAsync(CancellationToken cancellationToken);
}

internal sealed class DashboardFeedbackDiagnosticProvider(
    NavigationManager navigationManager,
    IConfiguration configuration,
    IFeedbackDiagnosticProcessRunner processRunner,
    IStringLocalizer<LayoutResources> localizer) : IDashboardFeedbackDiagnosticProvider
{
    private static readonly TimeSpan s_doctorTimeout = TimeSpan.FromSeconds(30);

    public string BuildAdditionalContext(bool includeAppHostInfo)
    {
        // These lines are inserted into the (editable) issue body the user reviews before submitting,
        // so the labels are localized. The leading "- " markdown bullet and the interpolated values
        // (versions, OS, route) are kept out of the resources so translators can't break the format.
        var builder = new StringBuilder();
        builder.AppendLine(CultureInfo.CurrentCulture, $"- {localizer[nameof(LayoutResources.MainLayoutProvideFeedbackContextPostedFrom)]}");
        builder.AppendLine(CultureInfo.CurrentCulture, $"- {localizer[nameof(LayoutResources.MainLayoutProvideFeedbackContextAspireVersion), VersionHelpers.DashboardDisplayVersion ?? string.Empty]}");
        builder.AppendLine(CultureInfo.CurrentCulture, $"- {localizer[nameof(LayoutResources.MainLayoutProvideFeedbackContextOperatingSystem), $"{RuntimeInformation.OSDescription} ({RuntimeInformation.OSArchitecture})"]}");
        builder.AppendLine(CultureInfo.CurrentCulture, $"- {localizer[nameof(LayoutResources.MainLayoutProvideFeedbackContextDashboardRoute), GetSanitizedDashboardRoute()]}");

        // The AppHost description is forwarded by the AppHost itself (DASHBOARD__APPHOST__INFO) because
        // it is the running app and therefore knows its exact Aspire SDK/package versions and target
        // framework. The dashboard never re-runs MSBuild to discover them. See AppHostDiagnosticInfo in
        // Aspire.Hosting. The line is only relevant to bug reports, hence the includeAppHostInfo gate.
        if (includeAppHostInfo &&
            configuration[DashboardConfigNames.AppHostInfoName.ConfigKey] is { Length: > 0 } appHostInfo)
        {
            builder.AppendLine(CultureInfo.CurrentCulture, $"- {localizer[nameof(LayoutResources.MainLayoutProvideFeedbackContextAppHost), appHostInfo]}");
        }

        return builder.ToString();
    }

    public bool IsAspireDoctorOutputAvailable =>
        configuration[DashboardConfigNames.CliPathName.ConfigKey] is { Length: > 0 };

    private string GetSanitizedDashboardRoute()
    {
        // The relative path can carry a query string and/or fragment that encode user-specific state,
        // for example a resource name or structured-log filters (structuredlogs?resource=customer-api).
        // Strip everything from the first '?' or '#' so those values don't leak into a public GitHub
        // issue; only the page identity (e.g. "structuredlogs") is useful context anyway.
        var relativePath = navigationManager.ToBaseRelativePath(navigationManager.Uri);
        var separatorIndex = relativePath.IndexOfAny(['?', '#']);
        return separatorIndex >= 0 ? relativePath[..separatorIndex] : relativePath;
    }

    public async Task<string> CaptureAspireDoctorOutputAsync(CancellationToken cancellationToken)
    {
        // Run `aspire doctor` against the CLI path the AppHost forwarded (DASHBOARD__CLI__PATH): the
        // exact CLI that launched the AppHost, or the `aspire` the AppHost resolved on PATH for non-CLI
        // launches (see DashboardEventHandlers in Aspire.Hosting). When that path is absent (e.g. a
        // standalone dashboard with no AppHost, or no CLI installed) doctor output is unavailable;
        // callers gate on IsAspireDoctorOutputAvailable, so the dashboard never probes for a CLI itself.
        if (configuration[DashboardConfigNames.CliPathName.ConfigKey] is not { Length: > 0 } cliExecutable)
        {
            throw new InvalidOperationException(
                $"{nameof(CaptureAspireDoctorOutputAsync)} requires '{DashboardConfigNames.CliPathName.ConfigKey}' to be configured; check {nameof(IsAspireDoctorOutputAvailable)} first.");
        }

        var result = await processRunner.RunAsync(
            cliExecutable,
            ["doctor", "--format", "json"],
            workingDirectory: null,
            environment: null,
            s_doctorTimeout,
            cancellationToken).ConfigureAwait(false);

        if (!result.Started)
        {
            return localizer[nameof(LayoutResources.MainLayoutProvideFeedbackDoctorCaptureFailed), result.FailureMessage ?? string.Empty];
        }

        if (result.TimedOut)
        {
            return localizer[nameof(LayoutResources.MainLayoutProvideFeedbackDoctorCaptureTimedOut), (int)s_doctorTimeout.TotalSeconds];
        }

        // `aspire doctor --format json` writes clean JSON to stdout; progress text goes to stderr
        // (which the runner drains and discards), so the captured stdout can be used directly.
        var output = result.StandardOutput.Trim();
        if (result.ExitCode != 0 && output.Length == 0)
        {
            return localizer[nameof(LayoutResources.MainLayoutProvideFeedbackDoctorCaptureExitCode), result.ExitCode];
        }

        return output;
    }
}
