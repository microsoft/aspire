// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// This file is source-linked into multiple projects.
// Do not add project-specific dependencies.

namespace Aspire.Shared;

/// <summary>
/// Describes whether a directory contains a lease whose OS handle is still held.
/// </summary>
internal enum HeldFileLeaseProbeResult
{
    None,
    Active,
    Unknown
}

/// <summary>
/// Holds a unique lease file open exclusively until disposal.
/// </summary>
/// <remarks>
/// A verified exclusive OS handle is authoritative. A lease whose exclusive lock cannot be
/// verified is marked in its file name so probes retain it conservatively.
/// </remarks>
internal sealed class HeldFileLease : IDisposable
{
    private const string UnverifiedLockMarker = ".unverified-lock";
    private const int WindowsSharingViolationHResult = unchecked((int)0x80070020);
    private const int WindowsLockViolationHResult = unchecked((int)0x80070021);
    private const int LinuxWouldBlockHResult = 11;
    private const int MacOsWouldBlockHResult = 35;

    private readonly FileStream _stream;

    private HeldFileLease(string leasePath, FileStream stream)
    {
        LeasePath = leasePath;
        _stream = stream;
    }

    /// <summary>
    /// Gets the path of the held lease file.
    /// </summary>
    public string LeasePath { get; }

    /// <summary>
    /// Gets the held stream so a caller can write advisory metadata.
    /// </summary>
    public FileStream Stream => _stream;

    /// <summary>
    /// Creates and exclusively holds a uniquely named lease file.
    /// </summary>
    public static HeldFileLease Acquire(string leaseDirectory, string fileNamePrefix, string fileNameExtension)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(leaseDirectory);
        ArgumentNullException.ThrowIfNull(fileNamePrefix);
        ArgumentException.ThrowIfNullOrWhiteSpace(fileNameExtension);

        var fullLeaseDirectory = Path.GetFullPath(leaseDirectory);
        Directory.CreateDirectory(fullLeaseDirectory);

        var leaseFileName = string.Concat(fileNamePrefix, Guid.NewGuid().ToString("N"));
        var verifiedLeasePath = Path.Combine(
            fullLeaseDirectory,
            string.Concat(leaseFileName, fileNameExtension));
        var leasePath = OperatingSystem.IsWindows()
            ? verifiedLeasePath
            : Path.Combine(
                fullLeaseDirectory,
                string.Concat(leaseFileName, UnverifiedLockMarker, fileNameExtension));
        var stream = new FileStream(
            leasePath,
            FileMode.CreateNew,
            FileAccess.ReadWrite,
            FileShare.None,
            bufferSize: 4096,
            OperatingSystem.IsWindows() ? FileOptions.DeleteOnClose : FileOptions.None);

        if (!OperatingSystem.IsWindows() && IsExclusiveLockEnforced(leasePath))
        {
            try
            {
                File.Move(leasePath, verifiedLeasePath);
                leasePath = verifiedLeasePath;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                // Keep the marker so other processes retain this lease conservatively.
            }
        }

        return new HeldFileLease(leasePath, stream);
    }

    /// <summary>
    /// Probes lease files and reclaims any orphan files that can be opened exclusively.
    /// </summary>
    /// <remarks>
    /// Probing has a documented side effect: an orphan is removed after exclusive access is
    /// acquired and, on Unix, verified with a second open of the same file.
    /// Unverified leases and enumeration or access failures are reported as
    /// <see cref="HeldFileLeaseProbeResult.Unknown"/> rather than being mistaken for inactive leases.
    /// </remarks>
    public static HeldFileLeaseProbeResult Probe(string leaseDirectory, string fileNameExtension)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(leaseDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(fileNameExtension);

        string[] leasePaths;
        try
        {
            leasePaths = Directory.GetFiles(leaseDirectory, string.Concat("*", fileNameExtension));
        }
        catch (DirectoryNotFoundException)
        {
            return File.Exists(leaseDirectory)
                ? HeldFileLeaseProbeResult.Unknown
                : HeldFileLeaseProbeResult.None;
        }
        catch (IOException)
        {
            return HeldFileLeaseProbeResult.Unknown;
        }
        catch (UnauthorizedAccessException)
        {
            return HeldFileLeaseProbeResult.Unknown;
        }
        catch (System.Security.SecurityException)
        {
            return HeldFileLeaseProbeResult.Unknown;
        }

        var result = HeldFileLeaseProbeResult.None;
        foreach (var leasePath in leasePaths)
        {
            var fileResult = ProbeLeaseFile(leasePath, fileNameExtension);
            if (fileResult is HeldFileLeaseProbeResult.Active)
            {
                return HeldFileLeaseProbeResult.Active;
            }

            if (fileResult is HeldFileLeaseProbeResult.Unknown)
            {
                result = HeldFileLeaseProbeResult.Unknown;
            }
        }

        return result;
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (!OperatingSystem.IsWindows())
        {
            try
            {
                // Unix has no kernel-backed DeleteOnClose, and an unverified lease may be renamed
                // after opening. Remove the path explicitly while any verified lock is still held.
                File.Delete(LeasePath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                // Retaining a stale lease is safer than making cleanup race a live holder.
            }
        }

        _stream.Dispose();
    }

    private static HeldFileLeaseProbeResult ProbeLeaseFile(string leasePath, string fileNameExtension)
    {
        if (leasePath.EndsWith(string.Concat(UnverifiedLockMarker, fileNameExtension), StringComparison.Ordinal))
        {
            return HeldFileLeaseProbeResult.Unknown;
        }

        try
        {
            using var stream = new FileStream(
                leasePath,
                FileMode.Open,
                FileAccess.ReadWrite,
                FileShare.None,
                bufferSize: 1,
                OperatingSystem.IsWindows() ? FileOptions.DeleteOnClose : FileOptions.None);

            if (!OperatingSystem.IsWindows())
            {
                if (!IsExclusiveLockEnforced(leasePath))
                {
                    return HeldFileLeaseProbeResult.Unknown;
                }

                try
                {
                    File.Delete(leasePath);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
                {
                    return HeldFileLeaseProbeResult.Unknown;
                }
            }

            return HeldFileLeaseProbeResult.None;
        }
        catch (FileNotFoundException)
        {
            return HeldFileLeaseProbeResult.None;
        }
        catch (DirectoryNotFoundException)
        {
            return HeldFileLeaseProbeResult.None;
        }
        catch (IOException ex) when (IsLeaseContention(ex))
        {
            return HeldFileLeaseProbeResult.Active;
        }
        catch (IOException)
        {
            return HeldFileLeaseProbeResult.Unknown;
        }
        catch (UnauthorizedAccessException)
        {
            return HeldFileLeaseProbeResult.Unknown;
        }
        catch (System.Security.SecurityException)
        {
            return HeldFileLeaseProbeResult.Unknown;
        }
    }

    private static bool IsExclusiveLockEnforced(string path)
    {
        try
        {
            // FileStream ignores Unix flock failures other than EWOULDBLOCK, including ENOTSUP.
            // A second open while the first handle is held distinguishes an enforced exclusive lock
            // from a successful open that acquired no lock. See:
            // https://github.com/dotnet/runtime/blob/main/src/libraries/System.Private.CoreLib/src/Microsoft/Win32/SafeHandles/SafeFileHandle.Unix.cs.
            using var verificationStream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.ReadWrite,
                FileShare.None,
                bufferSize: 1,
                FileOptions.None);
            return false;
        }
        catch (IOException ex) when (IsLeaseContention(ex))
        {
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return false;
        }
    }

    private static bool IsLeaseContention(IOException exception)
    {
        if (OperatingSystem.IsWindows())
        {
            return exception.HResult is WindowsSharingViolationHResult or WindowsLockViolationHResult;
        }

        // On Unix, FileStream implements FileShare.None with a non-blocking flock and exposes
        // EWOULDBLOCK as the raw errno in IOException.HResult: 11 on Linux and 35 on macOS.
        // See https://github.com/dotnet/runtime/blob/main/src/libraries/System.Private.CoreLib/src/Microsoft/Win32/SafeHandles/SafeFileHandle.Unix.cs.
        return exception.HResult is LinuxWouldBlockHResult or MacOsWouldBlockHResult;
    }
}
