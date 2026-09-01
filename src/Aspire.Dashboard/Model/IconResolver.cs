// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using Microsoft.FluentUI.AspNetCore.Components;
using Icons = Microsoft.FluentUI.AspNetCore.Components.Icons;

namespace Aspire.Dashboard.Model;

public sealed class IconResolver
{
    private sealed record IconKey(string IconName, IconSize DesiredIconSize, IconVariant IconVariant);
    private static readonly List<IconSize> s_iconSizes = [IconSize.Size16, IconSize.Size20, IconSize.Size24];
    private readonly ConcurrentDictionary<IconKey, Icon?> _iconCache = new();
    private readonly ILogger<IconResolver> _logger;

    // Fallback icon when a command specifies an icon name that can't be resolved to a FluentUI icon.
    private static readonly Icon s_unknownCommandIcon = new Icons.Regular.Size16.QuestionCircle();

    // Default icon for highlighted commands that don't specify any icon name.
    private static readonly Icon s_defaultHighlightedCommandIcon = new Icons.Regular.Size16.Flash();

    public IconResolver(ILogger<IconResolver> logger)
    {
        _logger = logger;
    }

    public Icon? ResolveIconName(string iconName, IconSize? desiredIconSize, IconVariant? iconVariant)
    {
        // Icons.GetInstance isn't efficient. Cache icon lookup.
        return _iconCache.GetOrAdd(new IconKey(iconName, desiredIconSize ?? IconSize.Size20, iconVariant ?? IconVariant.Regular), key =>
        {
            // Try to get the desired size.
            CustomIcon? icon;
            if (TryGetIconCore(key, key.DesiredIconSize, out icon))
            {
                return icon;
            }

            var iconSizesTried = new List<IconSize>
            {
                key.DesiredIconSize
            };

            // Some icons aren't available in all sizes. Try until we find a match. Prefer size bigger than desired.
            if (key.DesiredIconSize <= IconSize.Size16 && TryGetIcon(iconSizesTried, key, IconSize.Size16, out icon))
            {
                return icon;
            }
            if (key.DesiredIconSize <= IconSize.Size20 && TryGetIcon(iconSizesTried, key, IconSize.Size20, out icon))
            {
                return icon;
            }
            if (key.DesiredIconSize <= IconSize.Size24 && TryGetIcon(iconSizesTried, key, IconSize.Size24, out icon))
            {
                return icon;
            }

            // Usually icons are always available in bigger sizes and smaller sizes are removed.
            // This is because it isn't possible to scale down the detail. For example, BrainCircuit has a size 20 icon, but not a size 16 icon.
            // There are some rare icons that only have smaller sizes, For example, CodePyRectangle only has size 16 icon.
            // To handle the situation where we ask for a bigger than available icon, fall back to try any remaining icon sizes, including smaller.
            foreach (var size in s_iconSizes)
            {
                if (TryGetIcon(iconSizesTried, key, size, out icon))
                {
                    return icon;
                }
            }

            _logger.LogWarning("Icon '{IconName}' (variant: {IconVariant}, size: {IconSize}) could not be resolved.", key.IconName, key.IconVariant, key.DesiredIconSize);
            return null;
        });

        static bool TryGetIcon(List<IconSize> triedSizes, IconKey key, IconSize size, [NotNullWhen(true)] out CustomIcon? icon)
        {
            if (triedSizes.Contains(size))
            {
                icon = null;
                return false;
            }

            triedSizes.Add(size);
            return TryGetIconCore(key, size, out icon);
        }
    }

    private static bool TryGetIconCore(IconKey key, IconSize size, [NotNullWhen(true)] out CustomIcon? icon)
    {
        var iconInfo = new IconInfo
        {
            Name = key.IconName,
            Variant = key.IconVariant,
            Size = size
        };

        return iconInfo.TryGetInstance(out icon);
    }

    /// <summary>
    /// Resolves the icon for a command. Returns the resolved icon, a QuestionCircle fallback
    /// for unrecognized names, or <see langword="null"/> if no icon name is specified.
    /// </summary>
    public Icon? ResolveCommandIcon(string? iconName, IconVariant? iconVariant)
    {
        if (!string.IsNullOrEmpty(iconName))
        {
            return ResolveIconName(iconName, IconSize.Size16, iconVariant) ?? s_unknownCommandIcon;
        }

        return null;
    }

    /// <summary>
    /// Resolves the icon for a highlighted command button. Always returns a non-null icon so
    /// highlighted commands render as compact icon buttons and never as text labels.
    /// </summary>
    public Icon ResolveHighlightedCommandIcon(string? iconName, IconVariant? iconVariant)
    {
        return ResolveCommandIcon(iconName, iconVariant) ?? s_defaultHighlightedCommandIcon;
    }
}
