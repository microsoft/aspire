// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Cli.Templating;

/// <summary>
/// Generates the two Server launchSettings.json ports the embedded
/// <c>ts-cs-starter</c> template needs on top of the six AppHost dashboard /
/// OTLP / resource-service ports. The ranges mirror the <c>port</c> generators
/// in the upstream
/// <c>Aspire.ProjectTemplates/templates/aspire-ts-cs-starter/.template.config/template.json</c>
/// (<c>serverHttpPort</c> 5301-5600, <c>serverHttpsPort</c> 7301-7600) so a
/// project scaffolded through the embedded path falls in the same port space a
/// <c>dotnet new aspire-ts-cs-starter</c> consumer would land in.
/// </summary>
internal static class ServerProfilePortGenerator
{
    // Ranges copied from template.json. The upstream `port` generator picks a
    // free port inside [low, high]; we mirror the same low/high bounds and use a
    // uniform pick so collisions with the AppHost ranges (15000-22300) remain
    // impossible by construction.
    internal const int ServerHttpPortMin = 5301;
    internal const int ServerHttpPortMaxExclusive = 5601;
    internal const int ServerHttpsPortMin = 7301;
    internal const int ServerHttpsPortMaxExclusive = 7601;

    internal static ServerProfilePorts Generate(Random random)
    {
        ArgumentNullException.ThrowIfNull(random);

        return new ServerProfilePorts(
            ServerHttpPort: random.Next(ServerHttpPortMin, ServerHttpPortMaxExclusive),
            ServerHttpsPort: random.Next(ServerHttpsPortMin, ServerHttpsPortMaxExclusive));
    }
}

internal readonly record struct ServerProfilePorts(
    int ServerHttpPort,
    int ServerHttpsPort);
