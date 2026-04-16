// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;
using Aspire.Cli.Configuration;

namespace Aspire.Cli.Projects;

/// <summary>
/// Generates the AppHost server's <c>appsettings.json</c>.
///
/// This file is the AppHost server's static startup config. It carries:
///
/// <list type="bullet">
///   <item><c>AtsAssemblies</c> — the .NET assembly names the server scans for
///   <c>[AspireExport]</c> attributes via CLR reflection.</item>
///   <item><c>IntegrationHosts</c> — non-.NET integration hosts (npm packages today,
///   pip / cargo / go modules later). The server's <c>IntegrationHostLauncher</c>
///   reads this section at <c>StartAsync</c> time and spawns each host process
///   itself, the same way it loads .NET integrations into an
///   <c>AssemblyLoadContext</c>. No runtime RPC is involved.</item>
/// </list>
///
/// Shared between <see cref="DotNetBasedAppHostServerProject"/> (dev mode, builds the
/// AppHost server from source via <c>dotnet build</c>) and <see cref="PrebuiltAppHostServer"/>
/// (bundle mode, uses a prebuilt server) so both code paths produce an identical
/// startup config shape.
/// </summary>
internal static class AppHostServerAppSettingsWriter
{
    public static string Generate(
        IEnumerable<string> atsAssemblies,
        IEnumerable<IntegrationReference> npmIntegrations)
    {
        var assembliesJson = string.Join(",\n      ", atsAssemblies.Select(a => $"\"{a}\""));

        var integrationHostEntries = npmIntegrations
            .Where(i => i.Source == IntegrationSource.Npm)
            .Select(i => $$"""
                {
                  "Language": "typescript/nodejs",
                  "PackageName": {{JsonString(i.Name)}},
                  "HostEntryPoint": {{JsonString(i.Path!)}}
                }
                """)
            .ToList();
        var integrationHostsJson = integrationHostEntries.Count > 0
            ? "[\n    " + string.Join(",\n    ", integrationHostEntries) + "\n  ]"
            : "[]";

        return $$"""
            {
              "Logging": {
                "LogLevel": {
                  "Default": "Information",
                  "Microsoft.AspNetCore": "Warning",
                  "Aspire.Hosting.Dcp": "Warning"
                }
              },
              "AtsAssemblies": [
                {{assembliesJson}}
              ],
              "IntegrationHosts": {{integrationHostsJson}}
            }
            """;
    }

    private static string JsonString(string value) => $"\"{JsonEncodedText.Encode(value)}\"";
}
