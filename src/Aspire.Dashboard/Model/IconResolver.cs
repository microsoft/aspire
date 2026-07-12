// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using Microsoft.FluentUI.AspNetCore.Components;
using Icons = Microsoft.FluentUI.AspNetCore.Components.Icons;

namespace Aspire.Dashboard.Model;

public sealed class IconResolver(ILogger<IconResolver> logger)
{
    private sealed record IconKey(string IconName, IconSize DesiredIconSize, IconVariant IconVariant);

    private static readonly IconSize[] s_iconSizes = [IconSize.Size16, IconSize.Size20, IconSize.Size24];
    private static readonly Icon s_unknownCommandIcon = new Icons.Regular.Size16.QuestionCircle();
    private static readonly Icon s_defaultHighlightedCommandIcon = new Icons.Regular.Size16.Flash();
    private readonly ConcurrentDictionary<IconKey, Icon?> _iconCache = new();

    public Icon? ResolveIconName(string iconName, IconSize? desiredIconSize, IconVariant? iconVariant)
    {
        return _iconCache.GetOrAdd(new IconKey(iconName, desiredIconSize ?? IconSize.Size20, iconVariant ?? IconVariant.Regular), key =>
        {
            if (TryGetIconCore(key, key.DesiredIconSize, out var icon))
            {
                return icon;
            }

            var triedSizes = new List<IconSize> { key.DesiredIconSize };
            foreach (var size in s_iconSizes.OrderBy(size => Math.Abs((int)size - (int)key.DesiredIconSize)))
            {
                if (!triedSizes.Contains(size) && TryGetIconCore(key, size, out icon))
                {
                    return icon;
                }
            }

            logger.LogWarning("Icon '{IconName}' (variant: {IconVariant}, size: {IconSize}) could not be resolved.", key.IconName, key.IconVariant, key.DesiredIconSize);
            return null;
        });
    }

    public Icon? ResolveCommandIcon(string? iconName, IconVariant? iconVariant)
    {
        return !string.IsNullOrEmpty(iconName)
            ? ResolveIconName(iconName, IconSize.Size16, iconVariant) ?? s_unknownCommandIcon
            : null;
    }

    public Icon ResolveHighlightedCommandIcon(string? iconName, IconVariant? iconVariant)
    {
        return ResolveCommandIcon(iconName, iconVariant) ?? s_defaultHighlightedCommandIcon;
    }

    private static bool TryGetIconCore(IconKey key, IconSize size, [NotNullWhen(true)] out CustomIcon? icon)
    {
        return new IconInfo { Name = key.IconName, Variant = key.IconVariant, Size = size }.TryGetInstance(out icon);
    }
}
