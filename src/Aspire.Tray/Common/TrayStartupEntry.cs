// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Buffers.Binary;
using System.Reflection.PortableExecutable;

namespace Aspire.Tray;

/// <summary>
/// Validates the installed CLI entry point separately from the current backend.
/// </summary>
internal static class TrayStartupEntry
{
    internal const string Unavailable = "Launch at sign-in requires a verified stable native CLI installation. Start the tray from a script/PR-installed Aspire CLI; package-manager, unmanaged, and development paths cannot be registered automatically.";

    internal static string? GetUnavailableReason(TrayOptions options, bool nativeFrontend, bool windows)
    {
        if (!nativeFrontend || options.SmokeSeconds is not null || options.StartupCliPath is not { } startup || options.BundleRoot is null
            || !Path.IsPathFullyQualified(startup) || !File.Exists(startup))
        {
            return Unavailable;
        }
        try
        {
            var resolved = Resolve(startup);
            var root = Resolve(options.BundleRoot);
            if (IsPackageStorePath(startup) || IsPackageStorePath(resolved))
            {
                return Unavailable;
            }
            if (IsInside(startup, options.BundleRoot, windows) || IsInside(resolved, root, windows))
            {
                return "Launch at sign-in cannot use a CLI inside an extracted version bundle.";
            }
            if (!string.Equals(resolved, Resolve(options.CliPath), windows ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)
                || !IsNativeExecutable(resolved, windows, requireGui: false))
            {
                return Unavailable;
            }
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or BadImageFormatException)
        {
            return "The installed native CLI could not be verified for launch at sign-in.";
        }
    }

    internal static bool IsNativeExecutable(string path, bool windows, bool requireGui)
    {
        // Managed apphosts are native PE/Mach-O loaders, but depend on an adjacent runtime
        // config. Do not mistake a development apphost for the self-contained native CLI.
        if (File.Exists(Path.ChangeExtension(path, ".runtimeconfig.json")))
        {
            return false;
        }
        using var stream = File.OpenRead(path);
        if (windows)
        {
            using var reader = new PEReader(stream);
            var headers = reader.PEHeaders;
            return headers.CoffHeader.Machine is Machine.Amd64 or Machine.Arm64
                && headers.PEHeader?.Magic == PEMagic.PE32Plus && headers.CorHeader is null
                && (headers.CoffHeader.Characteristics & Characteristics.Dll) == 0
                && headers.PEHeader.Subsystem == (requireGui ? Subsystem.WindowsGui : Subsystem.WindowsCui);
        }

        Span<byte> header = stackalloc byte[16];
        if (stream.Read(header) != header.Length)
        {
            return false;
        }
        // Mach-O 64-bit little-endian MH_EXECUTE, or a universal container whose first
        // slice has that executable header. Never accept a dylib or a shell script.
        // https://github.com/apple-oss-distributions/xnu/blob/main/EXTERNAL_HEADERS/mach-o/loader.h
        if (BinaryPrimitives.ReadUInt32BigEndian(header) == 0xcafebabe)
        {
            stream.Position = 16; // fat_header (8) + cputype/cpusubtype (8)
            Span<byte> offset = stackalloc byte[4];
            stream.ReadExactly(offset);
            stream.Position = BinaryPrimitives.ReadUInt32BigEndian(offset);
            stream.ReadExactly(header);
        }
        return BinaryPrimitives.ReadUInt32LittleEndian(header) == 0xfeedfacf
            && BinaryPrimitives.ReadUInt32LittleEndian(header[12..]) == 2;
    }

    private static bool IsInside(string path, string directory, bool windows)
    {
        var comparison = windows ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        var full = Path.GetFullPath(path);
        return full.Equals(root, comparison) || full.StartsWith(root + Path.DirectorySeparatorChar, comparison);
    }

    private static bool IsPackageStorePath(string path)
        => Path.GetFullPath(path).Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries).Any(component =>
                component.Equals("node_modules", StringComparison.OrdinalIgnoreCase)
                || component.Equals(".store", StringComparison.OrdinalIgnoreCase));

    private static string Resolve(string path)
        => ResolveCore(path, 32);

    private static string ResolveCore(string path, int remainingLinks)
    {
        if (remainingLinks == 0)
        {
            throw new IOException("The installed CLI contains too many symbolic links.");
        }
        var full = Path.GetFullPath(path);
        var current = Path.GetPathRoot(full)!;
        foreach (var component in full[current.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, component);
            FileSystemInfo entry = Directory.Exists(current) ? new DirectoryInfo(current) : new FileInfo(current);
            if (entry.ResolveLinkTarget(returnFinalTarget: true) is { } target)
            {
                // A link target can itself contain linked ancestors (for example macOS
                // /var -> /private/var). Normalize those too before comparing installations.
                current = ResolveCore(target.FullName, remainingLinks - 1);
            }
        }
        return Path.GetFullPath(current);
    }
}
