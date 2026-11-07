// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using System.Text.Json;
using Aspire.Cli.DotNet;
using Aspire.Cli.Resources;
using Aspire.Hosting.Utils;
using Aspire.Shared.DotnetWatch;
using Semver;

namespace Aspire.Cli.Watch;

internal sealed record DotnetWatchAppHostLaunch(
    string ToolPath,
    string WatchHostPath,
    string SdkDirectory,
    string SdkVersion,
    string DotnetPath,
    string? HostIncompatibility)
{
    internal const string ResolveTarget = "ResolveAspireDotnetWatch";
    internal const string ToolPathEnvironmentVariable = "ASPIRE_DOTNET_WATCH_TOOL_PATH";
    private const string ToolFileName = "Microsoft.DotNet.HotReload.Watch.Aspire.dll";

    internal static async Task<DotnetWatchAppHostLaunch> ResolveAsync(
        IDotNetCliRunner runner,
        FileInfo appHost,
        IReadOnlyDictionary<string, string> environment,
        Func<string, string?> getEnvironmentVariable,
        CancellationToken cancellationToken)
    {
        var (exitCode, output) = await runner.GetProjectItemsAndPropertiesAsync(
            appHost,
            items: [],
            properties: ["Configuration", "TargetFrameworks"],
            targets: [ResolveTarget],
            new ProcessInvocationOptions
            {
                NoRestore = true,
                SuppressLogging = true,
                QueryTargetResults = true,
                EnvironmentVariables = environment
            },
            cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        using var result = output;
        if (exitCode != 0 || result is null)
        {
            throw new InvalidOperationException(RunCommandStrings.WatchBootstrapUnavailable);
        }

        var toolOverride = environment.TryGetValue(ToolPathEnvironmentVariable, out var value)
            ? value
            : getEnvironmentVariable(ToolPathEnvironmentVariable);
        var rollForward = environment.TryGetValue("DOTNET_ROLL_FORWARD", out var rollForwardValue)
            ? rollForwardValue
            : getEnvironmentVariable("DOTNET_ROLL_FORWARD");

        return Read(appHost, result.RootElement, toolOverride, rollForward);
    }

    internal static DotnetWatchAppHostLaunch Read(
        FileInfo appHost,
        JsonElement result,
        string? toolOverride,
        string? rollForwardOverride)
    {
        // -getTargetResult returns the actual package target's result, not an unevaluated item:
        // {"Properties":{"Configuration":"Debug","TargetFrameworks":""},
        //  "TargetResults":{"ResolveAspireDotnetWatch":{"Result":"Success","Items":[{
        //   "Identity":".../watch.dll","ToolPath":".../watch.dll","WatchHostPath":".../WatchHost.dll",
        //   "DotnetPath":".../dotnet","SdkVersion":"10.0.403","MSBuildPath":".../sdk/10.0.403/"}]}}}
        var target = GetProperty(GetProperty(result, "TargetResults"), ResolveTarget);
        if (GetRequiredString(target, "Result") != "Success")
        {
            throw new InvalidDataException(RunCommandStrings.WatchBootstrapInvalid);
        }

        var tools = GetProperty(target, "Items");
        if (tools.ValueKind != JsonValueKind.Array || tools.GetArrayLength() != 1)
        {
            throw new InvalidDataException(RunCommandStrings.WatchBootstrapInvalid);
        }

        var tool = tools[0];
        var path = GetRequiredString(tool, "ToolPath");
        var identity = GetRequiredString(tool, "Identity");
        var watchHostPath = GetRequiredString(tool, "WatchHostPath");
        var dotnetPath = GetRequiredString(tool, "DotnetPath");
        var sdkVersion = GetRequiredString(tool, "SdkVersion");
        var msbuildLocation = GetRequiredString(tool, "MSBuildPath");
        // The package target returns the executing SDK's MSBuildToolsPath directory. Accept an
        // explicit MSBuild.dll descriptor as well, but never probe another SDK or installation.
        var sdkDirectory = Directory.Exists(msbuildLocation)
            ? Path.TrimEndingDirectorySeparator(msbuildLocation)
            : Path.GetDirectoryName(msbuildLocation);
        var msbuildPath = sdkDirectory is not null ? Path.Combine(sdkDirectory, "MSBuild.dll") : "";
        if (!IsExistingAbsoluteFile(path) || !IsExistingAbsoluteFile(watchHostPath) ||
            !IsExistingAbsoluteFile(dotnetPath) || !IsExistingAbsoluteFile(msbuildPath) ||
            !Path.IsPathFullyQualified(msbuildLocation) ||
            (!Directory.Exists(msbuildLocation) && !string.Equals(Path.GetFileName(msbuildLocation), "MSBuild.dll", StringComparison.OrdinalIgnoreCase)) ||
            !Path.IsPathFullyQualified(identity) ||
            !string.Equals(Path.GetFullPath(path), Path.GetFullPath(identity), StringComparisons.FileSystemPath) ||
            !string.Equals(Path.GetFileName(msbuildPath), "MSBuild.dll", StringComparison.OrdinalIgnoreCase) ||
            !SemVersion.TryParse(sdkVersion, SemVersionStyles.Strict, out _))
        {
            throw new InvalidDataException(RunCommandStrings.WatchBootstrapInvalid);
        }

        if (toolOverride is not null)
        {
            // An explicit override is authoritative, including a malformed or missing path.
            // Relative overrides belong to the AppHost, not the terminal's working directory.
            if (string.IsNullOrWhiteSpace(toolOverride))
            {
                throw new InvalidDataException(RunCommandStrings.WatchToolOverrideInvalid);
            }
            var overridePath = Path.GetFullPath(toolOverride, appHost.Directory!.FullName);
            if (Directory.Exists(overridePath))
            {
                overridePath = Path.Combine(overridePath, ToolFileName);
            }
            if (!IsExistingAbsoluteFile(overridePath) ||
                !string.Equals(PathNormalizer.ResolveSymlinks(overridePath), PathNormalizer.ResolveSymlinks(path), StringComparisons.FileSystemPath))
            {
                throw new InvalidDataException(RunCommandStrings.WatchToolOverrideInvalid);
            }
        }

        sdkDirectory = Path.GetDirectoryName(msbuildPath)!;
        // Validate the complete payload separately from AppHost SDK compatibility: an unsupported
        // host wrapper must not discard the package descriptor for independently compatible services.
        var requirements = DotnetWatchPayload.ReadRuntimeRequirements(path, rollForwardOverride);
        DotnetWatchPayload.ValidateDependencies(path);
        _ = DotnetWatchPayload.ValidateWatchHost(watchHostPath);

        string? incompatibility = null;
        try
        {
            _ = DotnetWatchPayload.Validate(path, sdkDirectory);
        }
        catch (Exception exception) when (exception is InvalidDataException or JsonException or IOException)
        {
            incompatibility = string.Format(CultureInfo.CurrentCulture, RunCommandStrings.WatchAppHostRequirementsInvalid, sdkVersion, exception.Message);
        }

        var properties = GetProperty(result, "Properties");
        if (!appHost.Extension.Equals(".csproj", StringComparison.OrdinalIgnoreCase))
        {
            incompatibility = RunCommandStrings.WatchAppHostProjectRequired;
        }
        else if (!string.Equals(GetRequiredString(properties, "Configuration"), "Debug", StringComparison.OrdinalIgnoreCase) ||
            GetProperty(properties, "TargetFrameworks").GetString() is { Length: > 0 })
        {
            incompatibility = RunCommandStrings.WatchAppHostDefaultBuildRequired;
        }

        if (incompatibility is null)
        {
            var dotnetDirectory = Path.GetDirectoryName(PathNormalizer.ResolveSymlinks(dotnetPath))!;
            var sharedDirectory = Path.Combine(dotnetDirectory, "shared");
            var installed = Directory.Exists(sharedDirectory)
                ? Directory.EnumerateDirectories(sharedDirectory)
                    .SelectMany(framework => Directory.EnumerateDirectories(framework)
                        .Select(version => new DotnetWatchPayload.Framework(Path.GetFileName(framework), Path.GetFileName(version))))
                    .ToArray()
                : [];
            if (DotnetWatchPayload.FindMissingFrameworks(requirements, installed) is { Count: > 0 } missing)
            {
                incompatibility = string.Format(CultureInfo.CurrentCulture, RunCommandStrings.WatchAppHostRuntimeMissing,
                    string.Join(", ", missing.Select(framework => $"{framework.Name} {framework.Version}")), dotnetDirectory);
            }
        }

        return new(path, watchHostPath, sdkDirectory, sdkVersion, dotnetPath, incompatibility);
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
            // host consumes the first separator and appends the remaining tokens to dotnet run.
            // The second separator protects SDK-shaped application arguments, including an empty
            // argument and options such as --environment, --project, and --launch-profile.
            // https://github.com/dotnet/dotnet/blob/5a2162bea6845eabc1fc721624a43568c65db3b1/src/sdk/src/Dotnet.Watch/Watch.Aspire/Host/AspireHostLauncher.cs
            arguments.Add("--");
            arguments.Add("--");
            arguments.AddRange(appHostArguments);
        }

        return [.. arguments];
    }

    private static bool IsExistingAbsoluteFile(string path) => Path.IsPathFullyQualified(path) && File.Exists(path);

    private static JsonElement GetProperty(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value)
            ? value
            : throw new InvalidDataException(RunCommandStrings.WatchBootstrapInvalid);

    private static string GetRequiredString(JsonElement element, string name)
        => GetProperty(element, name) is { ValueKind: JsonValueKind.String } value && value.GetString() is { Length: > 0 } text
            ? text
            : throw new InvalidDataException(RunCommandStrings.WatchBootstrapInvalid);
}
