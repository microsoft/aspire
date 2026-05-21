// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Aspire.Cli.Acquisition;
using Aspire.Hosting.Backchannel;
using Microsoft.Extensions.Logging;

namespace Aspire.Cli.Utils;

internal static class CliPathHelper
{
    internal static string GetAspireHomeDirectory(string? processPath = null, ILogger? logger = null)
    {
        var effectiveProcessPath = processPath ?? Environment.ProcessPath;

        return TryGetAspireHomeDirectoryFromInstallRoute(effectiveProcessPath, logger)
            ?? Path.Combine(GetUserProfileDirectory(), ".aspire");
    }

    internal static string? TryGetAspireHomeDirectoryFromInstallRoute(string? processPath, ILogger? logger = null)
    {
        if (string.IsNullOrEmpty(processPath))
        {
            return null;
        }

        var realBinaryPath = ResolveSymlinkOrOriginalPath(processPath, logger);
        var binaryDir = Path.GetDirectoryName(realBinaryPath);
        if (string.IsNullOrEmpty(binaryDir))
        {
            return null;
        }

        var sidecarPath = Path.Combine(binaryDir, InstallSidecarReader.SidecarFileName);
        var source = InstallSidecarReader.ReadSourceField(sidecarPath);

        return source switch
        {
            InstallSourceExtensions.ScriptWire
                or InstallSourceExtensions.LocalHiveWire => Path.GetDirectoryName(binaryDir) ?? binaryDir,
            InstallSourceExtensions.PrWire => TryGetPrInstallPrefix(binaryDir),
            _ => null
        };
    }

    private static string? TryGetPrInstallPrefix(string binaryDir)
    {
        var prDir = Path.GetDirectoryName(binaryDir);
        if (string.IsNullOrEmpty(prDir))
        {
            return null;
        }

        var dogfoodDir = Path.GetDirectoryName(prDir);
        if (string.IsNullOrEmpty(dogfoodDir) ||
            !string.Equals(Path.GetFileName(dogfoodDir), InstallationDiscoveryLayout.DogfoodDirectoryName, StringComparison.Ordinal))
        {
            return null;
        }

        return Path.GetDirectoryName(dogfoodDir);
    }

    internal static string GetUserProfileDirectory()
        => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    internal static string ResolveSymlinkOrOriginalPath(string path, ILogger? logger = null)
    {
        if (string.IsNullOrEmpty(path))
        {
            return path;
        }

        return MaybeStripMacOSFirmlinkPrefix(TryResolveSymlinkTarget(path, logger, "using the raw path") ?? path);
    }

    internal static string? ResolveSymlinkToFullPath(string? path, ILogger? logger = null)
    {
        if (string.IsNullOrEmpty(path))
        {
            return null;
        }

        var resolved = TryResolveSymlinkTarget(path, logger, "trying the normalized path");
        if (resolved is not null)
        {
            return MaybeStripMacOSFirmlinkPrefix(resolved);
        }

        try
        {
            return MaybeStripMacOSFirmlinkPrefix(Path.GetFullPath(path));
        }
        catch (Exception ex) when (IsPathResolutionException(ex))
        {
            logger?.LogDebug(ex, "Could not normalize path {Path}; skipping it.", path);
            return null;
        }
    }

    /// <summary>
    /// Returns <paramref name="path"/> with a leading macOS firmlink prefix
    /// (<c>/private/var</c>, <c>/private/tmp</c>, <c>/private/etc</c>)
    /// rewritten back to the user-facing logical form (<c>/var</c>,
    /// <c>/tmp</c>, <c>/etc</c>). Returns the input unchanged when no
    /// firmlink prefix matches.
    /// </summary>
    /// <remarks>
    /// macOS Catalina (10.15) and later use APFS firmlinks to transparently
    /// redirect <c>/var</c> → <c>/private/var</c>, <c>/tmp</c> →
    /// <c>/private/tmp</c>, and <c>/etc</c> → <c>/private/etc</c> at the
    /// filesystem layer. Firmlinks are not symlinks — <c>lstat</c> reports
    /// the directory directly and <see cref="File.ResolveLinkTarget(string, bool)"/>
    /// returns <see langword="null"/>. Meanwhile, <see cref="Environment.ProcessPath"/>
    /// and libc <c>realpath(3)</c> return the <c>/private/*</c> form, while
    /// <see cref="Path.GetFullPath(string)"/>, <c>$PATH</c> walks via
    /// <see cref="PathLookupHelper"/>, NuGet's <c>packageSourceMapping</c>
    /// lookup, and user-typed paths use the un-prefixed form.
    /// 
    /// This asymmetry breaks every cross-surface path-string comparison
    /// when the CLI is installed under a firmlinked prefix
    /// (e.g. <c>/var/folders/...</c> from <c>mktemp</c>): the same
    /// physical binary shows up as two distinct strings, which breaks the
    /// dedup in <see cref="Acquisition.InstallationDiscovery"/> and causes
    /// NuGet to silently drop <c>&lt;packageSource&gt;</c> mappings whose
    /// key is in the <c>/private/*</c> form (NuGet canonicalizes path-named
    /// sources by stripping <c>/private/</c> when registering the source,
    /// but the <c>packageSourceMapping</c> key is matched against the
    /// stored name as-written — so any mapping authored with the
    /// <c>/private/*</c> key is unreachable and <c>Aspire*</c> patterns
    /// fall through to the catch-all source).
    /// 
    /// Normalizing all canonical paths back to the un-prefixed form keeps
    /// every comparison site consistent. Do not "fix" the resolve helpers
    /// above to return the realpath / <c>/private/*</c> form without also
    /// updating every downstream consumer (dedup, nuget.config writer,
    /// path-status check): the un-prefixed form is the one that crosses
    /// tool boundaries correctly.
    /// 
    /// The match is <see cref="StringComparison.Ordinal"/> because
    /// case-sensitive APFS volumes distinguish <c>/Private/Var/...</c>
    /// (a real user-created path) from <c>/private/var/...</c> (the
    /// firmlink). The match is also boundary-aware: <c>/private/varlog</c>
    /// is preserved because <c>varlog</c> is not the <c>var</c> path
    /// component followed by a separator.
    /// 
    /// See https://support.apple.com/guide/security/firmlinks-secf3a9d2014/web
    /// for Apple's firmlink reference.
    /// </remarks>
    internal static string StripMacOSFirmlinkPrefix(string path)
    {
        if (string.IsNullOrEmpty(path) || !path.StartsWith("/private/", StringComparison.Ordinal))
        {
            return path;
        }

        foreach (var firmlink in s_macosFirmlinkPrefixes)
        {
            if (path.Length >= firmlink.Length &&
                path.StartsWith(firmlink, StringComparison.Ordinal) &&
                (path.Length == firmlink.Length || path[firmlink.Length] == '/'))
            {
                return path[PrivateSegmentLength..];
            }
        }

        return path;
    }

    // "/private".Length — the byte we trim off when rewriting a firmlink path.
    private const int PrivateSegmentLength = 8;

    // Apple-documented user-visible firmlinks that take a `/private/<dir>` form
    // on macOS Catalina and later. Other macOS firmlinks (under
    // /System/Volumes/Data) do not surface as user paths and are not relevant
    // to install-path comparisons.
    private static readonly string[] s_macosFirmlinkPrefixes = ["/private/var", "/private/tmp", "/private/etc"];

    private static string MaybeStripMacOSFirmlinkPrefix(string path)
        => OperatingSystem.IsMacOS() ? StripMacOSFirmlinkPrefix(path) : path;

    /// <summary>
    /// Creates a randomized CLI-managed socket path.
    /// </summary>
    /// <param name="socketPrefix">The socket file prefix.</param>
    internal static string CreateUnixDomainSocketPath(string socketPrefix)
        => CreateSocketPath(socketPrefix, isGuestAppHost: false);

    internal static string CreateGuestAppHostSocketPath(string socketPrefix)
        => CreateSocketPath(socketPrefix, isGuestAppHost: true);

    private static string CreateSocketPath(string socketPrefix, bool isGuestAppHost)
    {
        var socketName = $"{socketPrefix}.{BackchannelConstants.CreateRandomIdentifier()}";

        if (isGuestAppHost && OperatingSystem.IsWindows())
        {
            return socketName;
        }

        var socketDirectory = GetCliSocketDirectory();
        Directory.CreateDirectory(socketDirectory);
        return Path.Combine(socketDirectory, socketName);
    }

    private static string GetCliHomeDirectory()
        => Path.Combine(GetAspireHomeDirectory(), "cli");

    private static string GetCliRuntimeDirectory()
        => Path.Combine(GetCliHomeDirectory(), "runtime");

    private static string GetCliSocketDirectory()
        => Path.Combine(GetCliRuntimeDirectory(), "sockets");

    private static string? TryResolveSymlinkTarget(string path, ILogger? logger, string fallbackDescription)
    {
        try
        {
            var resolved = File.ResolveLinkTarget(path, returnFinalTarget: true);
            return resolved?.FullName;
        }
        catch (Exception ex) when (IsPathResolutionException(ex))
        {
            logger?.LogDebug(ex, "Could not resolve symlink target for {Path}; {FallbackDescription}.", path, fallbackDescription);
            return null;
        }
    }

    private static bool IsPathResolutionException(Exception ex)
        => ex is IOException
            or UnauthorizedAccessException
            or ArgumentException
            or NotSupportedException
            or PathTooLongException
            or System.Security.SecurityException;
}
