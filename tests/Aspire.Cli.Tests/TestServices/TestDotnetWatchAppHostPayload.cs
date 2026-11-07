// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;
using System.Text.Json.Nodes;

namespace Aspire.Cli.Tests.TestServices;

internal sealed record TestDotnetWatchAppHostPayload(
    string ToolPath,
    string WatchHostPath,
    string DotnetPath,
    string SdkDirectory,
    string SdkVersion,
    string DependencyPath)
{
    internal static TestDotnetWatchAppHostPayload Create(DirectoryInfo root, string sdkVersion, string toolRuntimeVersion)
    {
        var toolDirectory = Directory.CreateDirectory(Path.Combine(root.FullName, "tools", "watch"));
        var hostDirectory = Directory.CreateDirectory(Path.Combine(root.FullName, "tools", "watch-host"));
        var dotnetDirectory = Directory.CreateDirectory(Path.Combine(root.FullName, "dotnet"));
        var sdkDirectory = Directory.CreateDirectory(Path.Combine(dotnetDirectory.FullName, "sdk", sdkVersion));
        var toolPath = Path.Combine(toolDirectory.FullName, "Microsoft.DotNet.HotReload.Watch.Aspire.dll");
        var hostPath = Path.Combine(hostDirectory.FullName, "Aspire.Hosting.Dotnet.WatchHost.dll");
        var dotnetPath = Path.Combine(dotnetDirectory.FullName, OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet");
        var dependencyPath = Path.Combine(toolDirectory.FullName, "watch-dependency.dll");
        File.WriteAllText(dotnetPath, "");
        File.WriteAllText(dependencyPath, "");
        var net6Agents = Directory.CreateDirectory(Path.Combine(toolDirectory.FullName, "hotreload", "net6.0"));
        var net10Agents = Directory.CreateDirectory(Path.Combine(toolDirectory.FullName, "hotreload", "net10.0"));
        File.WriteAllText(Path.Combine(net6Agents.FullName, "Microsoft.AspNetCore.Watch.BrowserRefresh.dll"), "");
        File.WriteAllText(Path.Combine(net6Agents.FullName, "Microsoft.Extensions.DotNetDeltaApplier.dll"), "");
        File.WriteAllText(Path.Combine(net10Agents.FullName, "Microsoft.Extensions.DotNetDeltaApplier.dll"), "");
        File.WriteAllText(Path.Combine(hostDirectory.FullName, "Aspire.DotnetWatch.Evaluation.dll"), "");
        File.WriteAllText(Path.Combine(hostDirectory.FullName, "Aspire.DotnetWatch.Evaluation.proj"), "<Project />");
        WriteEntryPoint(toolPath, toolRuntimeVersion, includeAspNetCore: true, dependency: Path.GetFileName(dependencyPath));
        WriteEntryPoint(hostPath, toolRuntimeVersion, includeAspNetCore: false, dependency: null);
        WriteEntryPoint(Path.Combine(sdkDirectory.FullName, "MSBuild.dll"),
            sdkVersion.StartsWith("11.", StringComparison.Ordinal) ? "11.0.0-rc.2.26504.105" : "10.0.14",
            includeAspNetCore: false, dependency: null);
        Directory.CreateDirectory(Path.Combine(dotnetDirectory.FullName, "shared", "Microsoft.NETCore.App", toolRuntimeVersion));
        Directory.CreateDirectory(Path.Combine(dotnetDirectory.FullName, "shared", "Microsoft.AspNetCore.App", toolRuntimeVersion));

        return new(toolPath, hostPath, dotnetPath, sdkDirectory.FullName, sdkVersion, dependencyPath);
    }

    internal JsonObject CreateQueryResult() => new()
    {
        ["Properties"] = new JsonObject
        {
            ["Configuration"] = "Debug",
            ["TargetFrameworks"] = ""
        },
        ["TargetResults"] = new JsonObject
        {
            ["ResolveAspireDotnetWatch"] = new JsonObject
            {
                ["Result"] = "Success",
                ["Items"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["Identity"] = ToolPath,
                        [nameof(ToolPath)] = ToolPath,
                        [nameof(WatchHostPath)] = WatchHostPath,
                        [nameof(DotnetPath)] = DotnetPath,
                        [nameof(SdkVersion)] = SdkVersion,
                        ["MSBuildPath"] = SdkDirectory
                    }
                }
            }
        }
    };

    internal JsonDocument CreateQueryDocument() => JsonDocument.Parse(CreateQueryResult().ToJsonString());

    private static void WriteEntryPoint(string path, string runtimeVersion, bool includeAspNetCore, string? dependency)
    {
        File.WriteAllText(path, "");
        var frameworks = new JsonArray(new JsonObject { ["name"] = "Microsoft.NETCore.App", ["version"] = runtimeVersion });
        if (includeAspNetCore)
        {
            frameworks.Add(new JsonObject { ["name"] = "Microsoft.AspNetCore.App", ["version"] = runtimeVersion });
        }
        var runtimeConfig = new JsonObject
        {
            ["runtimeOptions"] = new JsonObject { ["frameworks"] = frameworks, ["rollForward"] = "LatestPatch" }
        };
        File.WriteAllText(Path.ChangeExtension(path, ".runtimeconfig.json"), runtimeConfig.ToJsonString());
        var runtimeAssets = new JsonObject { [Path.GetFileName(path)] = new JsonObject() };
        if (dependency is not null)
        {
            runtimeAssets[dependency] = new JsonObject();
        }
        var manifest = new JsonObject
        {
            ["runtimeTarget"] = new JsonObject { ["name"] = "test" },
            ["targets"] = new JsonObject
            {
                ["test"] = new JsonObject { ["payload/1.0.0"] = new JsonObject { ["runtime"] = runtimeAssets } }
            }
        };
        File.WriteAllText(Path.ChangeExtension(path, ".deps.json"), manifest.ToJsonString());
    }
}
