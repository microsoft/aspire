// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Cli.Templating;

/// <summary>
/// Generates the four additional launchSettings.json ports the embedded
/// <c>csharp-starter</c> template needs on top of the six AppHost dashboard /
/// OTLP / resource-service ports. The ranges mirror the
/// <c>port</c> generators in the upstream
/// <c>Aspire.ProjectTemplates/templates/aspire-starter/.template.config/template.json</c>
/// so a project scaffolded through the embedded path falls in the same port
/// space a <c>dotnet new aspire-starter</c> consumer would land in.
/// </summary>
internal static class StarterProfilePortGenerator
{
    // Ranges copied from template.json. The upstream `port` generator picks
    // a free port inside [low, high]; we mirror the same low/high bounds and
    // use a uniform pick so collisions with the AppHost ranges (15000-22300)
    // remain impossible by construction.
    internal const int WebHttpPortMin = 5000;
    internal const int WebHttpPortMaxExclusive = 5301;
    internal const int WebHttpsPortMin = 7000;
    internal const int WebHttpsPortMaxExclusive = 7301;
    internal const int ApiServiceHttpPortMin = 5301;
    internal const int ApiServiceHttpPortMaxExclusive = 5601;
    internal const int ApiServiceHttpsPortMin = 7301;
    internal const int ApiServiceHttpsPortMaxExclusive = 7601;

    internal static StarterProfilePorts Generate(Random random)
    {
        ArgumentNullException.ThrowIfNull(random);

        return new StarterProfilePorts(
            WebHttpPort: random.Next(WebHttpPortMin, WebHttpPortMaxExclusive),
            WebHttpsPort: random.Next(WebHttpsPortMin, WebHttpsPortMaxExclusive),
            ApiServiceHttpPort: random.Next(ApiServiceHttpPortMin, ApiServiceHttpPortMaxExclusive),
            ApiServiceHttpsPort: random.Next(ApiServiceHttpsPortMin, ApiServiceHttpsPortMaxExclusive));
    }
}

internal readonly record struct StarterProfilePorts(
    int WebHttpPort,
    int WebHttpsPort,
    int ApiServiceHttpPort,
    int ApiServiceHttpsPort);
