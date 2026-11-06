// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using System.Text.Json;
using Aspire.Cli.DotNet;
using Aspire.Cli.Resources;
using Aspire.Hosting.Utils;
using Aspire.Shared;
using Semver;

namespace Aspire.Cli.Watch;

internal sealed record DotnetWatchAppHostLaunch(
    string ToolPath,
    string ToolVersion,
    int ToolRuntimeMajor,
    string SdkDirectory,
    string DotnetPath,
    string? HostIncompatibility)
{
    internal static async Task<DotnetWatchAppHostLaunch> ResolveAsync(
        IDotNetCliRunner runner,
        FileInfo appHost,
        IReadOnlyDictionary<string, string> environment,
        CancellationToken cancellationToken)
    {
        var (exitCode, output) = await runner.GetProjectItemsAndPropertiesAsync(
            appHost,
            items: ["AspireDotnetWatchTool"],
            properties: ["Configuration", "TargetFrameworks", "NETCoreSdkVersion"],
            targets: ["EnsureAspireDotnetWatchTool"],
            new ProcessInvocationOptions
            {
                NoRestore = true,
                SuppressLogging = true,
                EnvironmentVariables = environment
            },
            cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        using var result = output;
        if (exitCode != 0 || result is null)
        {
            throw new InvalidOperationException(RunCommandStrings.WatchBootstrapUnavailable);
        }

        return Read(appHost, result.RootElement);
    }

    internal static DotnetWatchAppHostLaunch Read(FileInfo appHost, JsonElement result)
    {
        // The explicit integration target returns:
        // {"Items":{"AspireDotnetWatchTool":[{"Identity":"...dll","Version":"10.0.403",
        //   "RuntimeMajor":"10","SdkDirectory":".../sdk/10.0.403","DotnetPath":".../dotnet"}]},
        //  "Properties":{"Configuration":"Debug","NETCoreSdkVersion":"10.0.403",...}}
        // No CLI-side installation or fallback tool selection belongs on this path.
        var tools = result.GetProperty("Items").GetProperty("AspireDotnetWatchTool");
        if (tools.GetArrayLength() != 1)
        {
            throw new InvalidDataException(RunCommandStrings.WatchBootstrapUnavailable);
        }

        var tool = tools[0];
        var path = GetRequiredString(tool, "Identity");
        var version = GetRequiredString(tool, "Version");
        var sdkDirectory = GetRequiredString(tool, "SdkDirectory");
        var dotnetPath = GetRequiredString(tool, "DotnetPath");
        if (!int.TryParse(GetRequiredString(tool, "RuntimeMajor"), NumberStyles.None, CultureInfo.InvariantCulture, out var runtimeMajor) ||
            runtimeMajor < 1 ||
            !Path.IsPathFullyQualified(path) || !File.Exists(path) ||
            !Path.IsPathFullyQualified(sdkDirectory) || !File.Exists(Path.Combine(sdkDirectory, "MSBuild.dll")) ||
            !Path.IsPathFullyQualified(dotnetPath) || !File.Exists(dotnetPath))
        {
            throw new InvalidDataException(RunCommandStrings.WatchBootstrapInvalid);
        }

        var properties = result.GetProperty("Properties");
        var sdkVersion = GetRequiredString(properties, "NETCoreSdkVersion");
        string? reason;
        if (!SemVersion.TryParse(sdkVersion, SemVersionStyles.Strict, out var parsedSdk))
        {
            reason = RunCommandStrings.WatchBootstrapInvalid;
        }
        else if (parsedSdk.Major != runtimeMajor)
        {
            // MSBuild assemblies from SDK 11 require System.Runtime 11. The pinned runtime-10
            // watcher cannot load them. Do not select another SDK or force major roll-forward.
            reason = string.Format(CultureInfo.CurrentCulture, RunCommandStrings.WatchAppHostSdkIncompatible, version, runtimeMajor, sdkVersion);
        }
        else if (!appHost.Extension.Equals(".csproj", StringComparison.OrdinalIgnoreCase))
        {
            reason = RunCommandStrings.WatchAppHostProjectRequired;
        }
        else if (properties.TryGetProperty("Configuration", out var configuration) &&
            !string.Equals(configuration.GetString(), "Debug", StringComparison.OrdinalIgnoreCase))
        {
            reason = RunCommandStrings.WatchAppHostDefaultBuildRequired;
        }
        else if (properties.TryGetProperty("TargetFrameworks", out var frameworks) && !string.IsNullOrEmpty(frameworks.GetString()))
        {
            reason = RunCommandStrings.WatchAppHostDefaultBuildRequired;
        }
        else
        {
            reason = GetMissingRuntimeReason(path, dotnetPath);
        }

        return new(path, version, runtimeMajor, sdkDirectory, dotnetPath, reason);
    }

    internal void ApplyEnvironment(IDictionary<string, string> environment)
    {
        environment[DotnetWatchConfiguration.ToolPath] = ToolPath;
        environment[DotnetWatchConfiguration.ToolVersion] = ToolVersion;
        environment[DotnetWatchConfiguration.ToolRuntimeMajor] = ToolRuntimeMajor.ToString(CultureInfo.InvariantCulture);
    }

    internal string[] CreateArguments(FileInfo appHost, ProcessInvocationOptions options, IReadOnlyList<string> appHostArguments)
    {
        var arguments = new List<string>
        {
            "exec", ToolPath, "host", "--sdk", SdkDirectory, "--entrypoint", appHost.FullName
        };
        if (options.NoLaunchProfile)
        {
            arguments.Add("--no-launch-profile");
        }
        else if (!string.IsNullOrEmpty(options.LaunchProfile))
        {
            arguments.Add($"--launch-profile={options.LaunchProfile}");
        }

        if (appHostArguments.Count > 0)
        {
            // The first separator is consumed by the watcher's parser. Its host launcher appends
            // the remaining arguments directly to dotnet run, so a second separator protects
            // application options such as --environment or --project from the SDK parser.
            // https://github.com/dotnet/dotnet/blob/5a2162bea6845eabc1fc721624a43568c65db3b1/src/sdk/src/Dotnet.Watch/Watch.Aspire/Host/AspireHostLauncher.cs
            arguments.Add("--");
            arguments.Add("--");
            arguments.AddRange(appHostArguments);
        }

        return [.. arguments];
    }

    private static string GetRequiredString(JsonElement element, string name)
        => element.GetProperty(name).GetString() is { Length: > 0 } value
            ? value
            : throw new InvalidDataException(RunCommandStrings.WatchBootstrapInvalid);

    private static string? GetMissingRuntimeReason(string toolPath, string dotnetPath)
    {
        using var runtimeConfig = JsonDocument.Parse(File.ReadAllText(Path.ChangeExtension(toolPath, ".runtimeconfig.json")));
        var options = runtimeConfig.RootElement.GetProperty("runtimeOptions");
        var frameworks = options.TryGetProperty("frameworks", out var frameworkArray)
            ? frameworkArray.EnumerateArray().ToArray()
            : [options.GetProperty("framework")];
        var dotnetDirectory = Path.GetDirectoryName(PathNormalizer.ResolveSymlinks(dotnetPath))!;
        foreach (var framework in frameworks)
        {
            var name = GetRequiredString(framework, "name");
            var version = GetRequiredString(framework, "version");
            var directory = Path.Combine(dotnetDirectory, "shared", name);
            if (!SemVersion.TryParse(version, SemVersionStyles.Strict, out var required) ||
                !Directory.Exists(directory) ||
                !Directory.EnumerateDirectories(directory).Any(path =>
                    SemVersion.TryParse(Path.GetFileName(path), SemVersionStyles.Strict, out var installed) &&
                    installed.Major == required.Major &&
                    installed.ComparePrecedenceTo(required) >= 0))
            {
                return string.Format(CultureInfo.CurrentCulture, RunCommandStrings.WatchAppHostRuntimeMissing, name, version, dotnetDirectory);
            }
        }

        return null;
    }
}
