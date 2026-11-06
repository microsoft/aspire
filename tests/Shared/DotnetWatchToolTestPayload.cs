// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Tests.Utils;

/// <summary>
/// Creates a non-executable payload for filesystem-only acquisition and MSBuild adapter tests.
/// </summary>
internal static class DotnetWatchToolTestPayload
{
    public static string Create(string directory)
    {
        Directory.CreateDirectory(directory);
        var dll = Path.Combine(directory, "Microsoft.DotNet.HotReload.Watch.Aspire.dll");
        File.WriteAllText(dll, "test payload");
        File.WriteAllText(Path.ChangeExtension(dll, ".deps.json"), """
            {
              "runtimeTarget": { "name": ".NETCoreApp,Version=v10.0" },
              "targets": {
                ".NETCoreApp,Version=v10.0": {
                  "Watch/1.0": {
                    "runtime": { "Microsoft.DotNet.HotReload.Watch.Aspire.dll": {}, "lib/net10.0/Dependency.dll": {} }
                  }
                }
              }
            }
            """);
        File.WriteAllText(Path.Combine(directory, "Dependency.dll"), "test dependency");
        foreach (var agent in new[]
        {
            "hotreload/net6.0/Microsoft.Extensions.DotNetDeltaApplier.dll",
            "hotreload/net6.0/Microsoft.AspNetCore.Watch.BrowserRefresh.dll",
            "hotreload/net10.0/Microsoft.Extensions.DotNetDeltaApplier.dll"
        })
        {
            var path = Path.Combine(directory, agent);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "test agent");
        }
        File.WriteAllText(Path.ChangeExtension(dll, ".runtimeconfig.json"), """
            {
              "runtimeOptions": {
                "frameworks": [
                  { "name": "Microsoft.NETCore.App", "version": "10.0.14" },
                  { "name": "Microsoft.AspNetCore.App", "version": "10.0.14" }
                ]
              }
            }
            """);
        return dll;
    }
}
