// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Aspire.Cli.Acquisition;

/// <summary>
/// Default <see cref="IInstallPathResolver"/> implementation that locates the
/// <c>.aspire-install.json</c> sidecar by inspecting filesystem paths.
/// </summary>
/// <remarks>
/// The resolver is AOT-safe: it only uses <see cref="File"/> / <see cref="Path"/> APIs and
/// does not perform reflection-based serialization. Symbolic links are resolved via
/// <see cref="File.ResolveLinkTarget(string, bool)"/> so that callers can pass the
/// <c>aspire</c> launcher path directly even when it is a symlink (e.g. <c>/usr/local/bin/aspire</c>
/// linking into a packager-managed install directory).
/// </remarks>
internal sealed class InstallPathResolver : IInstallPathResolver
{
    private const string SidecarFileName = ".aspire-install.json";

    /// <inheritdoc />
    public (InstallMode Mode, string Prefix) Resolve(string binaryPath)
    {
        ArgumentException.ThrowIfNullOrEmpty(binaryPath);

        var realBinaryPath = ResolveSymlinks(binaryPath);

        var binaryDir = Path.GetDirectoryName(realBinaryPath);
        if (string.IsNullOrEmpty(binaryDir))
        {
            return (InstallMode.Unknown, string.Empty);
        }

        // Mode B: sidecar lives next to the binary (packager-managed: winget, brew, dotnet-tool).
        if (File.Exists(Path.Combine(binaryDir, SidecarFileName)))
        {
            return (InstallMode.ModeB, binaryDir);
        }

        // Mode A: sidecar lives one directory above the binary (script / PR routes where the
        // binary sits in a `bin/` subdirectory under the install prefix).
        var parentDir = Path.GetDirectoryName(binaryDir);
        if (!string.IsNullOrEmpty(parentDir) &&
            File.Exists(Path.Combine(parentDir, SidecarFileName)))
        {
            return (InstallMode.ModeA, parentDir);
        }

        return (InstallMode.Unknown, string.Empty);
    }

    private static string ResolveSymlinks(string path)
    {
        try
        {
            var resolved = File.ResolveLinkTarget(path, returnFinalTarget: true);
            return resolved is null ? path : resolved.FullName;
        }
        catch (IOException)
        {
            // Path is not a link, does not exist, or cycle detected — fall back to the original
            // path. Sidecar discovery using the raw path is still valid in the non-link case.
            return path;
        }
    }
}
