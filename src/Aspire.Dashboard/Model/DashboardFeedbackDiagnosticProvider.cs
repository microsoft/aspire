// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using Aspire.Dashboard.Utils;
using Aspire.Hosting;
using Microsoft.AspNetCore.Components;

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
    /// Captures the output of <c>aspire doctor --format json</c> for inclusion in a bug report.
    /// </summary>
    Task<string> CaptureAspireDoctorOutputAsync(CancellationToken cancellationToken);
}

internal sealed class DashboardFeedbackDiagnosticProvider(
    NavigationManager navigationManager,
    IConfiguration configuration,
    IFeedbackDiagnosticProcessRunner processRunner) : IDashboardFeedbackDiagnosticProvider
{
    private static readonly TimeSpan s_doctorTimeout = TimeSpan.FromSeconds(30);

    public string BuildAdditionalContext(bool includeAppHostInfo)
    {
        var builder = new StringBuilder();
        builder.AppendLine("- Posted from: Dashboard");
        builder.AppendLine(CultureInfo.InvariantCulture, $"- Aspire version: {VersionHelpers.DashboardDisplayVersion}");
        builder.AppendLine(CultureInfo.InvariantCulture, $"- Operating system: {RuntimeInformation.OSDescription} ({RuntimeInformation.OSArchitecture})");
        builder.AppendLine(CultureInfo.InvariantCulture, $"- Dashboard route: /{GetSanitizedDashboardRoute()}");

        // The AppHost description is forwarded by the AppHost itself (DASHBOARD__APPHOST__INFO) because
        // it is the running app and therefore knows its exact Aspire SDK/package versions and target
        // framework. The dashboard never re-runs MSBuild to discover them. See AppHostDiagnosticInfo in
        // Aspire.Hosting. The line is only relevant to bug reports, hence the includeAppHostInfo gate.
        if (includeAppHostInfo &&
            configuration[DashboardConfigNames.AppHostInfoName.ConfigKey] is { Length: > 0 } appHostInfo)
        {
            builder.AppendLine(CultureInfo.InvariantCulture, $"- AppHost: {appHostInfo}");
        }

        return builder.ToString();
    }

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
        // Prefer the exact CLI that launched the AppHost (forwarded by the AppHost as
        // DASHBOARD__CLI__PATH from the CLI's Environment.ProcessPath) so the captured diagnostics
        // come from the same `aspire` build the user is running. Fall back to `aspire` on PATH when
        // the dashboard wasn't started via the CLI (for example a manual `dotnet run`).
        var cliExecutable = configuration[DashboardConfigNames.CliPathName.ConfigKey] is { Length: > 0 } configuredCliPath
            ? configuredCliPath
            : "aspire";

        var result = await processRunner.RunAsync(
            cliExecutable,
            ["doctor", "--format", "json"],
            workingDirectory: null,
            environment: null,
            s_doctorTimeout,
            cancellationToken).ConfigureAwait(false);

        if (!result.Started)
        {
            return $"Could not capture `aspire doctor` output ({result.FailureMessage}).";
        }

        if (result.TimedOut)
        {
            return $"Could not capture `aspire doctor` output because it did not complete within {s_doctorTimeout.TotalSeconds:N0} seconds.";
        }

        // `aspire doctor --format json` writes clean JSON to stdout; progress text goes to stderr
        // (which the runner drains and discards), so the captured stdout can be used directly.
        var output = result.StandardOutput.Trim();
        if (result.ExitCode != 0 && output.Length == 0)
        {
            return $"Could not capture `aspire doctor` output (exit code {result.ExitCode}).";
        }

        return output;
    }
}
