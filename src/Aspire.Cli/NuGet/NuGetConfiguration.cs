// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Cli.NuGet;

/// <summary>
/// Holds the evaluated ambient snapshot, selected source aliases, and composed policy overlay.
/// </summary>
internal sealed class NuGetConfiguration
{
    public NuGetConfiguration(
        NuGetSettingsInfo settings,
        IReadOnlyList<NuGetConfigSource> configSources,
        NuGetConfigOverlay? overlay)
    {
        Settings = settings with
        {
            ConfigPaths = [.. settings.ConfigPaths],
            Sources = [.. settings.Sources],
            SensitiveSourceValues = [.. settings.SensitiveSourceValues],
            PackageSourceMappings = [.. settings.PackageSourceMappings],
            DisabledPackageSourceKeys = [.. settings.DisabledPackageSourceKeys],
            ReservedPackageSourceKeys = [.. settings.ReservedPackageSourceKeys],
            SourceIdentityKey = [.. settings.SourceIdentityKey]
        };
        ConfigSources = [.. configSources];
        Overlay = overlay;
    }

    public NuGetSettingsInfo Settings { get; }

    public IReadOnlyList<NuGetConfigSource> ConfigSources { get; }

    public NuGetConfigOverlay? Overlay { get; }

    /// <summary>
    /// Gets whether the composed overlay changes the evaluated source or mapping policy.
    /// </summary>
    public bool HasSourcePolicyChanges
    {
        get
        {
            if (Overlay is not { } overlay)
            {
                return false;
            }

            if (overlay.Sources.Count > 0 ||
                overlay.ClearPackageSources || overlay.SourceAttributes.Count > 0)
            {
                return true;
            }

            // Inherited retired definitions can remain after a previous update. They do
            // not change effective policy when already disabled and unmapped.
            if (overlay.RetiredSourceKeys.Any(key =>
                Settings.Sources.Any(source => source.IsEnabled &&
                    string.Equals(source.Name, key, StringComparison.OrdinalIgnoreCase)) ||
                Settings.PackageSourceMappings.Any(mapping =>
                    string.Equals(mapping.SourceKey, key, StringComparison.OrdinalIgnoreCase))))
            {
                return true;
            }

            if (overlay.ClearDisabledPackageSources)
            {
                var disabledKeys = overlay.DisabledPackageSourceKeys.ToHashSet(StringComparer.OrdinalIgnoreCase);
                if (!disabledKeys.SetEquals(Settings.DisabledPackageSourceKeys) ||
                    Settings.Sources.Any(source => source.IsEnabled == disabledKeys.Contains(source.Name)))
                {
                    return true;
                }
            }

            if (overlay.PackageSourceMappings.Count == 0 && !overlay.ClearPackageSourceMappings)
            {
                return false;
            }

            var original = Settings.PackageSourceMappings.ToLookup(
                static mapping => mapping.SourceKey, StringComparer.OrdinalIgnoreCase);
            var proposed = overlay.PackageSourceMappings.ToLookup(
                static mapping => mapping.SourceKey, StringComparer.OrdinalIgnoreCase);
            return original.Count != proposed.Count || proposed.Any(group =>
                !group.SelectMany(static mapping => mapping.Patterns)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase)
                    .SetEquals(original[group.Key].SelectMany(static mapping => mapping.Patterns)));
        }
    }
}

internal sealed record NuGetConfigSource(
    string Key,
    string Source,
    bool IsAmbient,
    bool IsEnabled);
