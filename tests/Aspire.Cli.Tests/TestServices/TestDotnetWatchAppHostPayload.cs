// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;
using System.Text.Json.Nodes;

namespace Aspire.Cli.Tests.TestServices;

internal sealed record TestDotnetWatchAppHostPayload(
    string ToolPath,
    string SdkDirectory,
    string DotnetPath,
    string SdkVersion,
    string RuntimeVersion)
{
    internal static TestDotnetWatchAppHostPayload Create(DirectoryInfo directory, string sdkVersion, string runtimeVersion)
    {
        var toolPath = Path.Combine(directory.CreateSubdirectory("watch-tool").FullName, "watch.dll");
        File.WriteAllText(toolPath, "");
        var runtimeConfig = new JsonObject
        {
            ["runtimeOptions"] = new JsonObject
            {
                ["frameworks"] = new JsonArray(
                    new JsonObject { ["name"] = "Microsoft.NETCore.App", ["version"] = runtimeVersion },
                    new JsonObject { ["name"] = "Microsoft.AspNetCore.App", ["version"] = runtimeVersion })
            }
        };
        File.WriteAllText(Path.ChangeExtension(toolPath, ".runtimeconfig.json"), runtimeConfig.ToJsonString());

        var dotnetDirectory = directory.CreateSubdirectory("dotnet");
        var dotnetPath = Path.Combine(dotnetDirectory.FullName, OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet");
        File.WriteAllText(dotnetPath, "");
        var sdkDirectory = dotnetDirectory.CreateSubdirectory(Path.Combine("sdk", sdkVersion));
        File.WriteAllText(Path.Combine(sdkDirectory.FullName, "MSBuild.dll"), "");
        foreach (var framework in new[] { "Microsoft.NETCore.App", "Microsoft.AspNetCore.App" })
        {
            dotnetDirectory.CreateSubdirectory(Path.Combine("shared", framework, runtimeVersion));
        }

        return new(toolPath, sdkDirectory.FullName, dotnetPath, sdkVersion, runtimeVersion);
    }

    internal JsonObject CreateQueryResult() => new()
    {
        ["Items"] = new JsonObject
        {
            ["AspireDotnetWatchTool"] = new JsonArray(new JsonObject
            {
                ["Identity"] = ToolPath,
                ["Version"] = "custom",
                ["RuntimeMajor"] = RuntimeVersion.Split('.')[0],
                [nameof(SdkDirectory)] = SdkDirectory,
                [nameof(DotnetPath)] = DotnetPath
            })
        },
        ["Properties"] = new JsonObject
        {
            ["Configuration"] = "Debug",
            ["TargetFrameworks"] = "",
            ["NETCoreSdkVersion"] = SdkVersion
        }
    };

    internal JsonDocument CreateQueryDocument() => JsonDocument.Parse(CreateQueryResult().ToJsonString());
}
