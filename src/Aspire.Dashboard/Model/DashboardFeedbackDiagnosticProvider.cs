// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Aspire.Dashboard.Utils;
using Aspire.Hosting;
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
        builder.AppendLine(CultureInfo.InvariantCulture, $"- Dashboard route: /{navigationManager.ToBaseRelativePath(navigationManager.Uri)}");
        return builder.ToString();
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
        var result = await processRunner.RunAsync(
            "aspire",
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
        using var json = await RunMSBuildPropertyProbeAsync(appHostFilePath, cancellationToken).ConfigureAwait(false);
        if (json is null)
        {
            return FormatCSharpAppHostContext(appHostFilePath, sdkVersion: null, appHostPackageVersion: null, targetFramework: null);
        }

        var (sdkVersion, appHostPackageVersion, targetFramework) = ParseCSharpAppHostProperties(json.RootElement);
        return FormatCSharpAppHostContext(appHostFilePath, sdkVersion, appHostPackageVersion, targetFramework);
    }

    private async Task<JsonDocument?> RunMSBuildPropertyProbeAsync(string appHostFilePath, CancellationToken cancellationToken)
    {
        // Single-file AppHosts (apphost.cs) must go through the `dotnet build` driver so the
        // file-based app is materialized into a project; project AppHosts use `dotnet msbuild`.
        // Matches the CLI's DotNetCliRunner.GetProjectItemsAndPropertiesAsync.
        var isSingleFile = string.Equals(Path.GetExtension(appHostFilePath), ".cs", StringComparison.OrdinalIgnoreCase);

        var arguments = new List<string>
        {
            isSingleFile ? "build" : "msbuild",
            // MSBuildVersion is requested first as a workaround: `dotnet msbuild -getProperty` with a
            // single property name returns a bare value instead of a JSON document, which breaks
            // parsing. Asking for more than one property forces the JSON shape.
            // https://github.com/dotnet/msbuild/issues/12490
            "-getProperty:MSBuildVersion,AspireHostingSDKVersion,TargetFramework",
            "-getItem:PackageReference,AspireProjectOrPackageReference,PackageVersion",
            appHostFilePath
        };

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
            return JsonDocument.Parse(result.StandardOutput);
        }
        catch (JsonException ex)
        {
            logger.LogDebug(ex, "Could not parse AppHost MSBuild property JSON for '{AppHostFilePath}'.", appHostFilePath);
            return null;
        }
    }

    private static (string? SdkVersion, string? AppHostPackageVersion, string? TargetFramework) ParseCSharpAppHostProperties(JsonElement root)
    {
        // The probe returns the MSBuild -getProperty/-getItem JSON shape:
        //   {
        //     "Properties": { "AspireHostingSDKVersion": "13.5.0", "TargetFramework": "net10.0", ... },
        //     "Items": {
        //       "PackageReference": [ { "Identity": "Aspire.Hosting.AppHost", "Version": "9.0.0" } ],
        //       "PackageVersion": [ ... ]
        //     }
        //   }
        string? sdkVersion = null;
        string? targetFramework = null;
        if (root.TryGetProperty("Properties", out var properties))
        {
            sdkVersion = GetNonEmptyString(properties, "AspireHostingSDKVersion");
            targetFramework = GetNonEmptyString(properties, "TargetFramework");
        }

        // AppHost package version precedence mirrors AppHostInfoResolver.ParseAppHostInfo:
        // a direct PackageReference wins, then the SDK-provided AspireProjectOrPackageReference,
        // then the Central Package Management PackageVersion entry.
        string? appHostPackageVersion = null;
        if (root.TryGetProperty("Items", out var items))
        {
            appHostPackageVersion =
                GetPackageVersionFromItems(items, "PackageReference", AppHostPackageName)
                ?? GetPackageVersionFromItems(items, "AspireProjectOrPackageReference", AppHostPackageName)
                ?? GetPackageVersionFromItems(items, "PackageVersion", AppHostPackageName);
        }

        return (sdkVersion, appHostPackageVersion, targetFramework);
    }

    private static string FormatCSharpAppHostContext(string appHostFilePath, string? sdkVersion, string? appHostPackageVersion, string? targetFramework)
    {
        var builder = new StringBuilder();
        builder.Append(CultureInfo.InvariantCulture, $"C# at `{appHostFilePath}`");

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
        builder.Append(CultureInfo.InvariantCulture, $"TypeScript at `{appHostFilePath}`");
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

    private static string? GetNonEmptyString(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var value)
            && value.ValueKind == JsonValueKind.String
            && value.GetString() is { Length: > 0 } stringValue
                ? stringValue
                : null;
    }

    private static string? GetPackageVersionFromItems(JsonElement items, string itemType, string packageId)
    {
        if (!items.TryGetProperty(itemType, out var array) || array.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var item in array.EnumerateArray())
        {
            if (item.TryGetProperty("Identity", out var identity)
                && identity.ValueKind == JsonValueKind.String
                && string.Equals(identity.GetString(), packageId, StringComparison.Ordinal)
                && GetNonEmptyString(item, "Version") is { } version)
            {
                return version;
            }
        }

        return null;
    }
}
