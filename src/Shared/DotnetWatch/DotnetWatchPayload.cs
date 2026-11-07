// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;

namespace Aspire.Shared.DotnetWatch;

/// <summary>
/// Validates the bundled watcher and the SDK it loads, without acquiring or executing a tool.
/// </summary>
internal static class DotnetWatchPayload
{
    internal sealed record Framework(string Name, string Version);
    internal sealed record RuntimeRequirements(IReadOnlyList<Framework> Frameworks, string RollForward);

    internal static RuntimeRequirements Validate(string toolPath, string sdkDirectory)
    {
        if (!File.Exists(toolPath))
        {
            throw new InvalidDataException($"The watch tool '{toolPath}' does not exist.");
        }

        var requirements = ReadRuntimeRequirements(toolPath, Environment.GetEnvironmentVariable("DOTNET_ROLL_FORWARD"));
        ValidateDependencies(toolPath);
        var directory = Path.GetDirectoryName(Path.GetFullPath(toolPath))!;
        foreach (var relative in new[]
        {
            "hotreload/net6.0/Microsoft.AspNetCore.Watch.BrowserRefresh.dll",
            "hotreload/net6.0/Microsoft.Extensions.DotNetDeltaApplier.dll",
            "hotreload/net10.0/Microsoft.Extensions.DotNetDeltaApplier.dll"
        })
        {
            var agent = Path.Combine(directory, relative.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(agent))
            {
                throw new InvalidDataException($"The watch payload is missing hot-reload agent '{agent}'.");
            }
        }
        var msbuildPath = Path.Combine(sdkDirectory, "MSBuild.dll");
        if (!File.Exists(msbuildPath))
        {
            throw new InvalidDataException($"The selected SDK has no MSBuild assembly at '{msbuildPath}'.");
        }

        var sdkRequirements = ReadRuntimeRequirements(msbuildPath, rollForwardOverride: null);
        var toolRuntime = requirements.Frameworks.FirstOrDefault(framework => framework.Name == "Microsoft.NETCore.App");
        var sdkRuntime = sdkRequirements.Frameworks.FirstOrDefault(framework => framework.Name == "Microsoft.NETCore.App");
        if (toolRuntime is null || sdkRuntime is null ||
            !TryParseVersion(toolRuntime.Version, out var toolVersion, out _) ||
            !TryParseVersion(sdkRuntime.Version, out var sdkVersion, out _))
        {
            throw new InvalidDataException("The watch tool and SDK must declare valid Microsoft.NETCore.App runtime requirements.");
        }

        // Loading a newer SDK's MSBuild into an older watcher runtime is not supported,
        // even when the muxer also has the newer runtime installed.
        if (toolVersion < sdkVersion)
        {
            throw new InvalidDataException(
                $"The SDK's MSBuild requires .NET {sdkRuntime.Version}, but the watch tool targets .NET {toolRuntime.Version}. " +
                "Select a compatible SDK/tool pair or run without --watch.");
        }

        return requirements;
    }

    internal static RuntimeRequirements ValidateWatchHost(string hostPath)
    {
        ValidateDependencies(hostPath);
        var adapter = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(hostPath))!, "Aspire.DotnetWatch.Evaluation.dll");
        var query = Path.ChangeExtension(adapter, ".proj");
        if (!File.Exists(adapter) || !File.Exists(query))
        {
            throw new InvalidDataException($"The bundled watch payload requires its SDK-bound evaluation adapter '{adapter}' and query '{query}'.");
        }
        return ReadRuntimeRequirements(hostPath, Environment.GetEnvironmentVariable("DOTNET_ROLL_FORWARD"));
    }

    internal static void ValidateDependencies(string entryPoint)
    {
        var depsPath = Path.ChangeExtension(entryPoint, ".deps.json");
        if (!File.Exists(depsPath))
        {
            throw new InvalidDataException($"The watch payload is missing '{depsPath}'.");
        }

        using var document = JsonDocument.Parse(File.ReadAllText(depsPath));
        if (!document.RootElement.TryGetProperty("runtimeTarget", out var runtimeTarget) ||
            !runtimeTarget.TryGetProperty("name", out var targetName) ||
            targetName.GetString() is not { Length: > 0 } name ||
            !document.RootElement.TryGetProperty("targets", out var targets) ||
            !targets.TryGetProperty(name, out var target))
        {
            throw new InvalidDataException($"The watch payload dependency manifest '{depsPath}' has no runtime target.");
        }

        var directory = Path.GetDirectoryName(Path.GetFullPath(entryPoint))!;
        foreach (var library in target.EnumerateObject())
        {
            foreach (var assetKind in new[] { "runtime", "resources" })
            {
                if (!library.Value.TryGetProperty(assetKind, out var assets))
                {
                    continue;
                }

                foreach (var asset in assets.EnumerateObject())
                {
                    if (Path.GetFileName(asset.Name) == "_._")
                    {
                        continue;
                    }

                    var relativePath = Path.GetFileName(asset.Name);
                    if (assetKind == "resources" && asset.Value.TryGetProperty("locale", out var locale) &&
                        locale.GetString() is { Length: > 0 } culture)
                    {
                        relativePath = Path.Combine(culture, relativePath);
                    }

                    var path = Path.GetFullPath(relativePath, directory);
                    if (!File.Exists(path))
                    {
                        throw new InvalidDataException($"The watch payload is missing dependency '{path}' declared by '{depsPath}'.");
                    }
                }
            }
        }
    }

    internal static RuntimeRequirements ReadRuntimeRequirements(string entryPoint, string? rollForwardOverride)
    {
        var path = Path.ChangeExtension(entryPoint, ".runtimeconfig.json");
        if (!File.Exists(path))
        {
            throw new InvalidDataException($"The watch payload '{entryPoint}' has no '{Path.GetFileName(path)}'.");
        }

        using var document = JsonDocument.Parse(File.ReadAllText(path),
            new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        if (!document.RootElement.TryGetProperty("runtimeOptions", out var options) || options.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException($"The watch payload runtime configuration '{path}' has no runtimeOptions object.");
        }

        var frameworks = new List<Framework>();
        if (options.TryGetProperty("framework", out var framework))
        {
            AddFramework(framework);
        }

        if (options.TryGetProperty("frameworks", out var list))
        {
            foreach (var item in list.EnumerateArray())
            {
                AddFramework(item);
            }
        }

        var rollForward = options.TryGetProperty("rollForward", out var value)
            ? value.GetString() ?? throw new InvalidDataException($"Invalid rollForward in '{path}'.")
            : options.TryGetProperty("rollForwardOnNoCandidateFx", out var legacy)
                ? legacy.GetInt32() switch { 0 => "LatestPatch", 1 => "Minor", 2 => "Major", _ => throw new InvalidDataException($"Invalid rollForwardOnNoCandidateFx in '{path}'.") }
                : "Minor";
        if (!string.IsNullOrEmpty(rollForwardOverride) && GetRollForwardRank(rollForwardOverride) > GetRollForwardRank(rollForward))
        {
            rollForward = rollForwardOverride;
        }

        _ = GetRollForwardRank(rollForward);
        return new(frameworks, rollForward);

        void AddFramework(JsonElement element)
        {
            var name = element.GetProperty("name").GetString();
            var version = element.GetProperty("version").GetString();
            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(version) ||
                !TryParseVersion(version, out _, out _))
            {
                throw new InvalidDataException($"Invalid framework requirement in '{path}'.");
            }

            frameworks.Add(new(name, version));
        }
    }

    /// <summary>Parses lines such as <c>Microsoft.NETCore.App 10.0.14 [/usr/share/dotnet/shared/Microsoft.NETCore.App]</c>.</summary>
    internal static IReadOnlyList<Framework> ParseInstalledFrameworks(string output) =>
        [.. output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line => line.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries))
            .Where(parts => parts.Length >= 2)
            .Select(parts => new Framework(parts[0], parts[1]))];

    internal static IReadOnlyList<Framework> FindMissingFrameworks(RuntimeRequirements requirements, IReadOnlyList<Framework> installed)
    {
        var rank = GetRollForwardRank(requirements.RollForward);
        return [.. requirements.Frameworks.Where(required =>
            !TryParseVersion(required.Version, out var requiredVersion, out var requiredIsPrerelease) ||
            !installed.Any(candidate =>
                string.Equals(candidate.Name, required.Name, StringComparison.OrdinalIgnoreCase) &&
                TryParseVersion(candidate.Version, out var version, out var prerelease) &&
                version >= requiredVersion &&
                (version != requiredVersion || requiredIsPrerelease || !prerelease) &&
                (rank switch
                {
                    0 => version == requiredVersion,
                    1 => version.Major == requiredVersion.Major && version.Minor == requiredVersion.Minor,
                    2 => version.Major == requiredVersion.Major,
                    _ => true
                })))];
    }

    private static int GetRollForwardRank(string rollForward) => rollForward.ToUpperInvariant() switch
    {
        "DISABLE" => 0,
        "LATESTPATCH" => 1,
        "MINOR" or "LATESTMINOR" => 2,
        "MAJOR" or "LATESTMAJOR" => 3,
        _ => throw new InvalidDataException($"Unrecognized .NET roll-forward policy '{rollForward}'.")
    };

    private static bool TryParseVersion(string value, out Version version, out bool prerelease)
    {
        var dash = value.IndexOfAny(['-', '+']);
        prerelease = dash >= 0 && value[dash] == '-';
        return Version.TryParse(dash >= 0 ? value[..dash] : value, out version!);
    }
}
