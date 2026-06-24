// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Aspire.Dashboard.Utils;
using Aspire.Hosting;
using Aspire.Shared;
using Microsoft.AspNetCore.Components;

namespace Aspire.Dashboard.Model;

internal interface IDashboardFeedbackDiagnosticProvider
{
    /// <summary>
    /// Builds the basic additional-context lines (environment and dashboard route) that can be
    /// produced synchronously. The AppHost line is gathered separately via
    /// <see cref="CaptureAppHostContextAsync"/> because it can require launching MSBuild or Node.js.
    /// </summary>
    string BuildAdditionalContext();

    /// <summary>
    /// Resolves the <c>- AppHost: ...</c> context line (newline-terminated so it can be appended to
    /// the basic context), or <see langword="null"/> when no AppHost is configured or it cannot be
    /// inspected.
    /// </summary>
    Task<string?> CaptureAppHostContextAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Captures the output of <c>aspire doctor --format json</c> for inclusion in a bug report.
    /// </summary>
    Task<string> CaptureAspireDoctorOutputAsync(CancellationToken cancellationToken);
}

internal sealed class DashboardFeedbackDiagnosticProvider(
    NavigationManager navigationManager,
    IConfiguration configuration,
    IFeedbackDiagnosticProcessRunner processRunner,
    ILogger<DashboardFeedbackDiagnosticProvider> logger) : IDashboardFeedbackDiagnosticProvider
{
    private const string AppHostSdkName = "Aspire.AppHost.Sdk";
    private const string AppHostPackageName = "Aspire.Hosting.AppHost";

    private static readonly TimeSpan s_doctorTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan s_appHostProbeTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan s_commandVersionTimeout = TimeSpan.FromSeconds(10);

    public string BuildAdditionalContext()
    {
        var builder = new StringBuilder();
        builder.AppendLine("- Posted from: Dashboard");
        builder.AppendLine(CultureInfo.InvariantCulture, $"- Aspire version: {VersionHelpers.DashboardDisplayVersion}");
        builder.AppendLine(CultureInfo.InvariantCulture, $"- Operating system: {RuntimeInformation.OSDescription} ({RuntimeInformation.OSArchitecture})");
        builder.AppendLine(CultureInfo.InvariantCulture, $"- Dashboard route: /{GetSanitizedDashboardRoute()}");
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

    public async Task<string?> CaptureAppHostContextAsync(CancellationToken cancellationToken)
    {
        if (TryGetAppHostFilePath() is not { } appHostFilePath)
        {
            return null;
        }

        var extension = Path.GetExtension(appHostFilePath).ToLowerInvariant();
        var context = extension switch
        {
            ".csproj" or ".cs" => await GetCSharpAppHostContextAsync(appHostFilePath, cancellationToken).ConfigureAwait(false),
            ".ts" or ".mts" => await GetTypeScriptAppHostContextAsync(appHostFilePath, cancellationToken).ConfigureAwait(false),
            _ => null
        };

        // Returned newline-terminated so the dialog can append it directly to the synchronously
        // built additional-context block.
        return context is null ? null : $"- AppHost: {context}{Environment.NewLine}";
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

    private string? TryGetAppHostFilePath()
    {
        if (configuration[DashboardConfigNames.AppHostFilePathName.ConfigKey] is not { Length: > 0 } configuredAppHostFilePath)
        {
            return null;
        }

        string appHostFilePath;
        try
        {
            appHostFilePath = Path.GetFullPath(configuredAppHostFilePath);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }

        return File.Exists(appHostFilePath) ? appHostFilePath : null;
    }

    private async Task<string?> GetCSharpAppHostContextAsync(string appHostFilePath, CancellationToken cancellationToken)
    {
        // Evaluate the AppHost with MSBuild rather than parsing the project/source file directly.
        // MSBuild honors values contributed by imported files (Directory.Build.props, the
        // Aspire.AppHost.Sdk, and Central Package Management via Directory.Packages.props) that a
        // textual scan would miss. This mirrors the Aspire CLI's AppHostInfoResolver, which uses
        // DotNetCliRunner.GetProjectItemsAndPropertiesAsync.
        var inspection = await RunMSBuildPropertyProbeAsync(appHostFilePath, cancellationToken).ConfigureAwait(false);
        if (inspection is null)
        {
            return FormatCSharpAppHostContext(appHostFilePath, sdkVersion: null, appHostPackageVersion: null, targetFramework: null);
        }

        var (sdkVersion, appHostPackageVersion, targetFramework) = ParseCSharpAppHostProperties(inspection);
        return FormatCSharpAppHostContext(appHostFilePath, sdkVersion, appHostPackageVersion, targetFramework);
    }

    private async Task<AppHostProjectInspectionOutput?> RunMSBuildPropertyProbeAsync(string appHostFilePath, CancellationToken cancellationToken)
    {
        // Single-file AppHosts (apphost.cs) must go through the `dotnet build` driver so the
        // file-based app is materialized into a project; project AppHosts use `dotnet msbuild`.
        // The argument construction is shared with the CLI's
        // DotNetCliRunner.GetProjectItemsAndPropertiesAsync via DotNetProjectProbe so the two
        // probes stay identical.
        var arguments = DotNetProjectProbe.BuildItemsAndPropertiesArguments(
            appHostFilePath,
            items: ["PackageReference", "AspireProjectOrPackageReference", "PackageVersion"],
            properties: ["AspireHostingSDKVersion", "TargetFramework"],
            targets: []);

        // Evaluation-only environment so first-run/telemetry/workload-update banners can't corrupt
        // the JSON written to stdout. Mirrors the env the CLI sets for the same probe.
        var environment = new Dictionary<string, string>
        {
            ["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1",
            ["DOTNET_NOLOGO"] = "1",
            ["DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE"] = "1"
        };

        var result = await processRunner.RunAsync(
            "dotnet",
            arguments,
            Path.GetDirectoryName(appHostFilePath),
            environment,
            s_appHostProbeTimeout,
            cancellationToken).ConfigureAwait(false);

        if (!result.Started || result.TimedOut || result.ExitCode != 0 || string.IsNullOrWhiteSpace(result.StandardOutput))
        {
            logger.LogDebug(
                "Could not evaluate AppHost MSBuild properties for '{AppHostFilePath}' (started: {Started}, timed out: {TimedOut}, exit code: {ExitCode}).",
                appHostFilePath, result.Started, result.TimedOut, result.ExitCode);
            return null;
        }

        try
        {
            // Deserialize into the shared inspection model via source generation (trim/AOT safe).
            // The records and the package-version precedence are shared with the CLI's
            // AppHostInfoResolver through Aspire.Shared.AppHostProjectInspection.
            return JsonSerializer.Deserialize(result.StandardOutput, AppHostInspectionSerializerContext.Default.AppHostProjectInspectionOutput);
        }
        catch (JsonException ex)
        {
            logger.LogDebug(ex, "Could not parse AppHost MSBuild property JSON for '{AppHostFilePath}'.", appHostFilePath);
            return null;
        }
    }

    private static (string? SdkVersion, string? AppHostPackageVersion, string? TargetFramework) ParseCSharpAppHostProperties(AppHostProjectInspectionOutput inspection)
    {
        // The probe returns the MSBuild -getProperty/-getItem JSON shape:
        //   {
        //     "Properties": { "AspireHostingSDKVersion": "13.5.0", "TargetFramework": "net10.0", ... },
        //     "Items": {
        //       "PackageReference": [ { "Identity": "Aspire.Hosting.AppHost", "Version": "9.0.0" } ],
        //       "PackageVersion": [ ... ]
        //     }
        //   }
        var properties = inspection.Properties;
        var sdkVersion = string.IsNullOrWhiteSpace(properties?.AspireHostingSDKVersion) ? null : properties.AspireHostingSDKVersion;
        var targetFramework = string.IsNullOrWhiteSpace(properties?.TargetFramework) ? null : properties.TargetFramework;

        // AppHost package version precedence is applied by the shared helper, which is the same code
        // the CLI's AppHostInfoResolver uses, so the rule is no longer mirrored here.
        var appHostPackageVersion = AppHostProjectInspection.FindPackageVersion(inspection.Items, AppHostPackageName);

        return (sdkVersion, appHostPackageVersion, targetFramework);
    }

    private static string FormatCSharpAppHostContext(string appHostFilePath, string? sdkVersion, string? appHostPackageVersion, string? targetFramework)
    {
        var builder = new StringBuilder();

        // Only include the AppHost file name, never the full path: the absolute path leaks the local
        // user/home directory (and customer folder names) into a public GitHub issue. The file name
        // (for example MyApp.AppHost.csproj or apphost.cs) is enough to identify the AppHost shape.
        builder.Append(CultureInfo.InvariantCulture, $"C# (`{Path.GetFileName(appHostFilePath)}`)");

        var startedUsingClause = false;
        if (!string.IsNullOrWhiteSpace(sdkVersion))
        {
            builder.Append(CultureInfo.InvariantCulture, $" using {AppHostSdkName} {sdkVersion}");
            startedUsingClause = true;
        }

        if (!string.IsNullOrWhiteSpace(appHostPackageVersion))
        {
            var connector = startedUsingClause ? "and" : "using";
            builder.Append(CultureInfo.InvariantCulture, $" {connector} {AppHostPackageName} {appHostPackageVersion}");
        }

        if (!string.IsNullOrWhiteSpace(targetFramework))
        {
            builder.Append(CultureInfo.InvariantCulture, $" targeting `{targetFramework}`");
        }

        return builder.ToString();
    }

    private async Task<string?> GetTypeScriptAppHostContextAsync(string appHostFilePath, CancellationToken cancellationToken)
    {
        // Report the Node.js version actually on PATH (the best available cross-platform signal)
        // rather than the engines.node range declared in package.json, which is rarely set and is
        // not necessarily what is running.
        var nodeVersion = await CaptureCommandVersionAsync("node", appHostFilePath, cancellationToken).ConfigureAwait(false);

        var builder = new StringBuilder();

        // Use only the file name, not the absolute path, to avoid leaking the local user/home
        // directory (and customer folder names) into a public GitHub issue.
        builder.Append(CultureInfo.InvariantCulture, $"TypeScript (`{Path.GetFileName(appHostFilePath)}`)");
        if (!string.IsNullOrWhiteSpace(nodeVersion))
        {
            builder.Append(CultureInfo.InvariantCulture, $" on Node.js {nodeVersion}");
        }

        return builder.ToString();
    }

    private async Task<string?> CaptureCommandVersionAsync(string fileName, string appHostFilePath, CancellationToken cancellationToken)
    {
        var result = await processRunner.RunAsync(
            fileName,
            ["--version"],
            Path.GetDirectoryName(appHostFilePath),
            environment: null,
            s_commandVersionTimeout,
            cancellationToken).ConfigureAwait(false);

        if (!result.Started || result.TimedOut || result.ExitCode != 0 || string.IsNullOrWhiteSpace(result.StandardOutput))
        {
            return null;
        }

        // `node --version` emits a single line such as "v24.15.0".
        return result.StandardOutput
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault()?
            .Trim();
    }
}
