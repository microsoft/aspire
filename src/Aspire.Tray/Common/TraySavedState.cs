// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json.Serialization;

namespace Aspire.Tray;

/// <summary>
/// Stores source paths in most-recent-first order, independently of live process metadata.
/// </summary>
internal sealed record TraySavedState(IReadOnlyList<SavedAppHost> AppHosts)
{
    internal const int DefaultRecentAppHostLimit = 10;
    internal const int MaximumRecentAppHosts = 50;

    public static TraySavedState Empty { get; } = new([]);

    // The source generator otherwise supplies default(bool) for an absent init-only
    // property, overriding its initializer. Make the legacy-file default explicit in
    // the JSON constructor so old files never silently opt out of stop confirmation.
    [JsonConstructor]
    public TraySavedState(IReadOnlyList<SavedAppHost> appHosts, bool confirmStop = true) : this(appHosts)
    {
        ConfirmStop = confirmStop;
    }

    /// <summary>
    /// Requires confirmation before stopping an AppHost, including for files written before this preference existed.
    /// </summary>
    public bool ConfirmStop { get; init; } = true;

    public TraySavedState Remember(string appHostPath) => Remember(appHostPath, DefaultRecentAppHostLimit);

    public TraySavedState Remember(string appHostPath, int recentAppHostLimit)
    {
        var existing = AppHosts.SingleOrDefault(host => TrayAppHostPath.Comparer.Equals(host.AppHostPath, appHostPath));
        return (this with
        {
            AppHosts = [new(appHostPath, existing?.IsPinned ?? false, true),
                .. AppHosts.Where(host => !TrayAppHostPath.Comparer.Equals(host.AppHostPath, appHostPath))]
        }).Trim(recentAppHostLimit);
    }

    public TraySavedState SetPinned(string appHostPath, bool pinned)
    {
        var existing = AppHosts.SingleOrDefault(host => TrayAppHostPath.Comparer.Equals(host.AppHostPath, appHostPath));
        if (existing is null)
        {
            return pinned ? this with { AppHosts = [.. AppHosts, new(appHostPath, true, false)] } : this;
        }
        return this with
        {
            AppHosts = AppHosts.Select(host => host == existing ? host with { IsPinned = pinned } : host)
                .Where(host => host.IsPinned || host.IsRecent).ToArray()
        };
    }

    public TraySavedState ClearRecent()
        => this with { AppHosts = AppHosts.Where(host => host.IsPinned).Select(host => host with { IsRecent = false }).ToArray() };

    public TraySavedState RemoveRecent(string appHostPath)
        => this with
        {
            AppHosts = AppHosts.Select(host => TrayAppHostPath.Comparer.Equals(host.AppHostPath, appHostPath)
                ? host with { IsRecent = false } : host)
                .Where(host => host.IsPinned || host.IsRecent).ToArray()
        };

    public TraySavedState RemoveMissingPins(IReadOnlySet<string> missingPaths)
        => this with { AppHosts = AppHosts.Where(host => !host.IsPinned || !missingPaths.Contains(host.AppHostPath)).ToArray() };

    public TraySavedState Trim(int recentAppHostLimit)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(recentAppHostLimit);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(recentAppHostLimit, MaximumRecentAppHosts);
        var recent = 0;
        return this with
        {
            AppHosts = AppHosts.Select(host => host.IsRecent && ++recent > recentAppHostLimit
                ? host with { IsRecent = false } : host)
                .Where(host => host.IsPinned || host.IsRecent).ToArray()
        };
    }
}

internal sealed record SavedAppHost(string AppHostPath, bool IsPinned, bool IsRecent);

/// <summary>
/// Persists tray history and pins without process identities or dashboard credentials.
/// </summary>
internal interface ITraySavedStateStore
{
    TraySavedState Load();
    void Save(TraySavedState state);
}

internal sealed class MemoryTraySavedStateStore : ITraySavedStateStore
{
    private TraySavedState _state = TraySavedState.Empty;

    public TraySavedState Load() => _state;

    public void Save(TraySavedState state) => _state = state;
}

internal static class TrayAppHostPath
{
    public static StringComparer Comparer { get; } = OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    public static string Normalize(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path) || path.Contains('\0'))
        {
            throw new ArgumentException("An absolute AppHost source path is required.", nameof(path));
        }
        return Path.GetFullPath(path);
    }

    public static string RequireExistingFile(string path)
    {
        var normalized = Normalize(path);
        try
        {
            if ((GetSourceAttributes(normalized) & FileAttributes.Directory) == 0)
            {
                return normalized;
            }
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
        }
        throw new FileNotFoundException("The AppHost source file no longer exists.");
    }

    public static bool IsMissing(string path)
    {
        try
        {
            GetSourceAttributes(path);
            return false;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return true;
        }
    }

    private static FileAttributes GetSourceAttributes(string path)
    {
        // File.Exists also returns false for access and I/O failures. Those failures must
        // preserve preferences, not be treated as evidence that a project was deleted.
        var attributes = File.GetAttributes(path);
        if ((attributes & FileAttributes.ReparsePoint) != 0
            && File.ResolveLinkTarget(path, returnFinalTarget: true) is { } target)
        {
            return File.GetAttributes(target.FullName);
        }
        return attributes;
    }
}
