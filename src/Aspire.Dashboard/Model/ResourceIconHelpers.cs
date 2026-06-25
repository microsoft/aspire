// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Dashboard.Model;

using Aspire.Dashboard.Components.Deck;
using Microsoft.Extensions.Diagnostics.HealthChecks;

internal static class ResourceIconHelpers
{
    /// <summary>
    /// Maps a resource to a Deck icon, checking for a custom icon first, then falling back to a
    /// default icon based on the resource type.
    /// </summary>
    public static DeckIconName GetDeckIconForResource(ResourceViewModel resource)
    {
        // A custom icon set by the AppHost via WithIconName. These are Fluent system-icon names
        // (https://aka.ms/fluentui-system-icons); map the ones Aspire integrations commonly use to the
        // nearest Deck glyph. Unknown names fall through to the resource-type icon below: Deck ships a
        // fixed, single-stroke glyph set rather than the full Fluent icon library, so arbitrary names
        // can't be resolved — and we intentionally don't reach back to Fluent for them.
        if (!string.IsNullOrWhiteSpace(resource.IconName) && TryGetDeckIcon(resource.IconName, out var custom))
        {
            return custom;
        }

        return resource.ResourceType switch
        {
            KnownResourceTypes.Executable => DeckIconName.Executable,
            KnownResourceTypes.Project => DeckIconName.Project,
            KnownResourceTypes.Container => DeckIconName.Container,
            KnownResourceTypes.Parameter => DeckIconName.Parameters,
            KnownResourceTypes.ConnectionString => DeckIconName.Link,
            KnownResourceTypes.ExternalService => DeckIconName.External,
            string t when t.Contains("database", StringComparison.OrdinalIgnoreCase) => DeckIconName.Database,
            _ => DeckIconName.Resources,
        };
    }

    /// <summary>
    /// Maps a health status to a Deck icon plus the Deck <c>icon-*</c> tone class that colors it.
    /// Mirrors the legacy Fluent heart/heart-broken treatment.
    /// </summary>
    public static (DeckIconName icon, string toneClass) GetHealthStatusDeckIcon(HealthStatus? healthStatus)
    {
        return healthStatus switch
        {
            HealthStatus.Healthy => (DeckIconName.Heart, "icon-success"),
            HealthStatus.Degraded => (DeckIconName.HeartBroken, "icon-warning"),
            HealthStatus.Unhealthy => (DeckIconName.HeartBroken, "icon-error"),
            _ => (DeckIconName.CircleHint, "icon-muted")
        };
    }

    // Maps a Fluent system-icon name (passed via WithIconName, or a command's IconName) to the nearest
    // Deck glyph. Comparison is case-insensitive. Returns false for names with no Deck equivalent so the
    // caller can fall back (to a resource-type icon, or to no icon for a command).
    public static bool TryGetDeckIcon(string iconName, out DeckIconName icon)
    {
        switch (iconName.ToLowerInvariant())
        {
            case "database":
            case "databasemultiple":
            case "windowdatabase":
                icon = DeckIconName.Database;
                return true;
            case "box":
            case "boxmultiple":
                icon = DeckIconName.Container;
                return true;
            case "apps":
                icon = DeckIconName.AppsList;
                return true;
            case "key":
                icon = DeckIconName.Parameters;
                return true;
            case "plugconnectedsettings":
                icon = DeckIconName.Link;
                return true;
            case "settingscogmultiple":
                icon = DeckIconName.Settings;
                return true;
            case "server":
                icon = DeckIconName.Server;
                return true;
            case "mail":
                icon = DeckIconName.Mail;
                return true;
            case "braincircuit":
            case "agents":
            case "agentsadd":
                // AI-flavored resources (MCP servers, Foundry agents) -> the Deck sparkle affordance.
                icon = DeckIconName.Sparkle;
                return true;
            case "globedesktop":
            case "globearrowforward":
            case "cloudbidirectional":
            case "cloudarrowup":
            case "virtualnetwork":
                icon = DeckIconName.External;
                return true;
            case "codecsrectangle":
            case "codefsrectangle":
            case "codevbrectangle":
            case "codejsrectangle":
            case "codepyrectangle":
            case "codecircle":
                // Language "code rectangle" glyphs -> the Deck executable/code glyph (angle brackets).
                icon = DeckIconName.Executable;
                return true;
            default:
                icon = default;
                return false;
        }
    }
}
