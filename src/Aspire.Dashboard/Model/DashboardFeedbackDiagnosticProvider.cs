// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Aspire.Dashboard.Utils;
using Aspire.Hosting;
using Microsoft.AspNetCore.Components;

namespace Aspire.Dashboard.Model;

internal interface IDashboardFeedbackDiagnosticProvider
{
    string BuildAdditionalContext();

    Task<DashboardFeedbackDiagnosticContext> CaptureBugContextAsync(CancellationToken cancellationToken);
}

internal sealed record DashboardFeedbackDiagnosticContext(string AspireDoctorOutput, string AdditionalContext);

internal sealed class DashboardFeedbackDiagnosticProvider(
    NavigationManager navigationManager,
    IConfiguration configuration,
    ILogger<DashboardFeedbackDiagnosticProvider> logger) : IDashboardFeedbackDiagnosticProvider
{
    private static readonly TimeSpan s_doctorTimeout = TimeSpan.FromSeconds(30);
    private static readonly Regex s_singleFileAppHostSdkRegex = new(@"^\s*#:\s*sdk\s+Aspire\.AppHost\.Sdk@(?<version>\S+)\s*$", RegexOptions.Multiline | RegexOptions.CultureInvariant);
    private const string AppHostSdkName = "Aspire.AppHost.Sdk";

    public string BuildAdditionalContext()
    {
        var builder = new StringBuilder();
        builder.AppendLine("- Posted from: Dashboard");
        builder.AppendLine(CultureInfo.InvariantCulture, $"- Aspire version: {VersionHelpers.DashboardDisplayVersion}");
        builder.AppendLine(CultureInfo.InvariantCulture, $"- Operating system: {RuntimeInformation.OSDescription} ({RuntimeInformation.OSArchitecture})");
        builder.AppendLine(CultureInfo.InvariantCulture, $"- Dashboard route: /{navigationManager.ToBaseRelativePath(navigationManager.Uri)}");
        if (TryGetAppHostContext() is { } appHostContext)
        {
            builder.AppendLine(CultureInfo.InvariantCulture, $"- AppHost: {appHostContext}");
        }

        return builder.ToString();
    }

    public async Task<DashboardFeedbackDiagnosticContext> CaptureBugContextAsync(CancellationToken cancellationToken)
    {
        var doctorOutput = await CaptureAspireDoctorOutputAsync(cancellationToken).ConfigureAwait(false);

        return new DashboardFeedbackDiagnosticContext(doctorOutput, BuildAdditionalContext());
    }

    private async Task<string> CaptureAspireDoctorOutputAsync(CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo("aspire")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        startInfo.ArgumentList.Add("doctor");
        startInfo.ArgumentList.Add("--format");
        startInfo.ArgumentList.Add("json");

        using var process = new Process
        {
            StartInfo = startInfo
        };

        try
        {
            if (!process.Start())
            {
                return "Could not capture `aspire doctor` output because the Aspire CLI process could not be started.";
            }
        }
        catch (Win32Exception ex)
        {
            logger.LogDebug(ex, "Could not start 'aspire doctor' while gathering dashboard feedback diagnostics.");
            return string.Create(CultureInfo.InvariantCulture, $"Could not capture `aspire doctor` output ({ex.Message}).");
        }
        catch (InvalidOperationException ex)
        {
            logger.LogDebug(ex, "Could not start 'aspire doctor' while gathering dashboard feedback diagnostics.");
            return string.Create(CultureInfo.InvariantCulture, $"Could not capture `aspire doctor` output ({ex.Message}).");
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(s_doctorTimeout);

        try
        {
            var outputTask = process.StandardOutput.ReadToEndAsync(timeoutCts.Token);
            var errorTask = process.StandardError.ReadToEndAsync(timeoutCts.Token);

            await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
            await Task.WhenAll(outputTask, errorTask).ConfigureAwait(false);

            var output = outputTask.Result;
            var error = errorTask.Result;
            if (!string.IsNullOrWhiteSpace(error))
            {
                output = string.IsNullOrWhiteSpace(output)
                    ? error
                    : string.Concat(output, Environment.NewLine, error);
            }

            if (process.ExitCode != 0 && string.IsNullOrWhiteSpace(output))
            {
                return string.Create(CultureInfo.InvariantCulture, $"Could not capture `aspire doctor` output (exit code {process.ExitCode}).");
            }

            return NormalizeAspireDoctorOutput(output);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            KillTimedOutProcess(process);
            return string.Create(CultureInfo.InvariantCulture, $"Could not capture `aspire doctor` output because it did not complete within {s_doctorTimeout.TotalSeconds:N0} seconds.");
        }
    }

    internal static string NormalizeAspireDoctorOutput(string output)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return string.Empty;
        }

        return TryExtractJsonObject(output) ?? output.Trim();
    }

    private static string? TryExtractJsonObject(string output)
    {
        var startIndex = output.IndexOf('{', StringComparison.Ordinal);
        if (startIndex < 0)
        {
            return null;
        }

        var depth = 0;
        var inString = false;
        var escaped = false;

        // `aspire doctor --format json` can still be accompanied by progress text from
        // lower-level checks, for example:
        //   { "checks": [...] }
        //
        //   Checking Aspire environment...
        // Keep only the first complete JSON object so the issue prefill remains clean.
        for (var i = startIndex; i < output.Length; i++)
        {
            var c = output[i];
            if (inString)
            {
                if (escaped)
                {
                    escaped = false;
                }
                else if (c == '\\')
                {
                    escaped = true;
                }
                else if (c == '"')
                {
                    inString = false;
                }

                continue;
            }

            if (c == '"')
            {
                inString = true;
            }
            else if (c == '{')
            {
                depth++;
            }
            else if (c == '}')
            {
                depth--;
                if (depth == 0)
                {
                    return output[startIndex..(i + 1)].Trim();
                }
            }
        }

        return null;
    }

    private string? TryGetAppHostContext()
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

        var extension = Path.GetExtension(appHostFilePath).ToLowerInvariant();
        return extension switch
        {
            ".csproj" => GetCSharpProjectAppHostContext(appHostFilePath),
            ".cs" => GetCSharpSingleFileAppHostContext(appHostFilePath),
            ".ts" or ".mts" => GetTypeScriptAppHostContext(appHostFilePath),
            _ => null
        };
    }

    private static string? GetCSharpProjectAppHostContext(string appHostFilePath)
    {
        if (!File.Exists(appHostFilePath))
        {
            return null;
        }

        try
        {
            var project = XDocument.Load(appHostFilePath).Root;
            if (project is null)
            {
                return null;
            }

            var sdkVersion = TryGetSdkVersionFromProject(project)
                ?? TryGetSdkVersionFromGlobalJson(Path.GetDirectoryName(appHostFilePath));
            var targetFramework = TryGetProjectProperty(project, "TargetFramework")
                ?? TryGetProjectProperty(project, "TargetFrameworks");

            return FormatAppHostContext("C#", appHostFilePath, sdkVersion, targetFramework);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or System.Xml.XmlException)
        {
            return FormatAppHostContext("C#", appHostFilePath);
        }
    }

    private static string? GetCSharpSingleFileAppHostContext(string appHostFilePath)
    {
        if (!File.Exists(appHostFilePath))
        {
            return null;
        }

        try
        {
            var appHostSource = File.ReadAllText(appHostFilePath);
            var sdkVersion = s_singleFileAppHostSdkRegex.Match(appHostSource) is { Success: true } match
                ? match.Groups["version"].Value
                : TryGetSdkVersionFromGlobalJson(Path.GetDirectoryName(appHostFilePath));

            return FormatAppHostContext("C#", appHostFilePath, sdkVersion);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return FormatAppHostContext("C#", appHostFilePath);
        }
    }

    private static string? GetTypeScriptAppHostContext(string appHostFilePath)
    {
        if (!File.Exists(appHostFilePath))
        {
            return null;
        }

        var nodeVersion = TryGetNodeVersionFromPackageJson(Path.GetDirectoryName(appHostFilePath));
        return FormatAppHostContext("TypeScript", appHostFilePath, stackVersion: nodeVersion is null ? null : $"Node.js {nodeVersion}");
    }

    private static string FormatAppHostContext(string language, string appHostFilePath, string? appHostSdkVersion = null, string? targetFramework = null, string? stackVersion = null)
    {
        var builder = new StringBuilder();
        builder.Append(CultureInfo.InvariantCulture, $"{language} at `{appHostFilePath}`");

        if (!string.IsNullOrWhiteSpace(stackVersion))
        {
            builder.Append(CultureInfo.InvariantCulture, $" on {stackVersion}");
        }

        if (!string.IsNullOrWhiteSpace(appHostSdkVersion))
        {
            builder.Append(CultureInfo.InvariantCulture, $" using {AppHostSdkName} {appHostSdkVersion}");
        }

        if (!string.IsNullOrWhiteSpace(targetFramework))
        {
            builder.Append(CultureInfo.InvariantCulture, $" targeting `{targetFramework}`");
        }

        return builder.ToString();
    }

    private static string? TryGetSdkVersionFromProject(XElement project)
    {
        if (project.Attribute("Sdk")?.Value is { Length: > 0 } projectSdk)
        {
            foreach (var sdk in projectSdk.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            {
                const string appHostSdkPrefix = $"{AppHostSdkName}/";
                if (sdk.StartsWith(appHostSdkPrefix, StringComparison.Ordinal))
                {
                    return sdk[appHostSdkPrefix.Length..];
                }
            }
        }

        return project.Elements()
            .Where(e => e.Name.LocalName == "Sdk" && string.Equals(e.Attribute("Name")?.Value, AppHostSdkName, StringComparison.Ordinal))
            .Select(e => e.Attribute("Version")?.Value)
            .FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));
    }

    private static string? TryGetProjectProperty(XElement project, string propertyName)
    {
        return project.Elements()
            .Where(e => e.Name.LocalName == "PropertyGroup")
            .Elements()
            .Where(e => e.Name.LocalName == propertyName)
            .Select(e => e.Value)
            .FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));
    }

    private static string? TryGetSdkVersionFromGlobalJson(string? startDirectory)
    {
        for (var directory = startDirectory; directory is not null; directory = Directory.GetParent(directory)?.FullName)
        {
            var globalJsonPath = Path.Combine(directory, "global.json");
            if (!File.Exists(globalJsonPath))
            {
                continue;
            }

            try
            {
                var globalJson = JsonNode.Parse(File.ReadAllText(globalJsonPath)) as JsonObject;
                if (globalJson?["msbuild-sdks"]?[AppHostSdkName]?.GetValue<string>() is { Length: > 0 } version)
                {
                    return version;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                return null;
            }
        }

        return null;
    }

    private static string? TryGetNodeVersionFromPackageJson(string? startDirectory)
    {
        for (var directory = startDirectory; directory is not null; directory = Directory.GetParent(directory)?.FullName)
        {
            var packageJsonPath = Path.Combine(directory, "package.json");
            if (!File.Exists(packageJsonPath))
            {
                continue;
            }

            try
            {
                var packageJson = JsonNode.Parse(File.ReadAllText(packageJsonPath)) as JsonObject;
                if (packageJson?["engines"]?["node"]?.GetValue<string>() is { Length: > 0 } version)
                {
                    return version;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                return null;
            }
        }

        return null;
    }

    private void KillTimedOutProcess(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException ex)
        {
            logger.LogDebug(ex, "The 'aspire doctor' process exited before it needed to be killed.");
        }
        catch (Win32Exception ex)
        {
            logger.LogDebug(ex, "Could not kill timed-out 'aspire doctor' process while gathering dashboard feedback diagnostics.");
        }
    }
}
