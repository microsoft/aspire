// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// This file is source-linked into multiple projects.
// Do not add project-specific dependencies.

using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace Aspire.Shared;

/// <summary>
/// Holds an exclusive file handle that marks a versioned CLI bundle directory as in use.
/// </summary>
internal sealed class BundleVersionLease : IDisposable
{
    /// <summary>
    /// Directory name under a versioned bundle directory that contains lease files.
    /// </summary>
    public const string LeasesDirectoryName = ".leases";

    private const string LeaseExtension = ".lease";
    private readonly FileStream _stream;

    private BundleVersionLease(string versionDirectory, string leasePath, FileStream stream)
    {
        VersionDirectory = versionDirectory;
        LeasePath = leasePath;
        _stream = stream;
    }

    /// <summary>
    /// Gets the leased version directory.
    /// </summary>
    public string VersionDirectory { get; }

    /// <summary>
    /// Gets the lease metadata path.
    /// </summary>
    public string LeasePath { get; }

    /// <summary>
    /// Creates a lease for <paramref name="versionDirectory"/>.
    /// </summary>
    public static BundleVersionLease Acquire(string versionDirectory, string holderKind, string? commandName = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(versionDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(holderKind);

        var fullVersionDirectory = Path.GetFullPath(versionDirectory);
        if (!Directory.Exists(fullVersionDirectory))
        {
            throw new DirectoryNotFoundException($"Bundle version directory '{fullVersionDirectory}' does not exist.");
        }

        var leasesDirectory = Path.Combine(fullVersionDirectory, LeasesDirectoryName);
        Directory.CreateDirectory(leasesDirectory);

        var leasePath = Path.Combine(leasesDirectory, CreateLeaseFileName());
        var stream = new FileStream(
            leasePath,
            FileMode.CreateNew,
            FileAccess.ReadWrite,
            FileShare.None,
            bufferSize: 4096,
            FileOptions.DeleteOnClose);

        try
        {
            var metadata = CreateMetadataJson(fullVersionDirectory, holderKind, commandName);
            var bytes = Encoding.UTF8.GetBytes(metadata);
            stream.Write(bytes);
            stream.Flush(flushToDisk: true);

            return new BundleVersionLease(fullVersionDirectory, leasePath, stream);
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Acquires a lease from <see cref="BundleDiscovery.BundleVersionDirectoryEnvVar"/> when the environment variable is set.
    /// </summary>
    public static BundleVersionLease? TryAcquireFromEnvironment(string holderKind, string? commandName = null)
    {
        var versionDirectory = Environment.GetEnvironmentVariable(BundleDiscovery.BundleVersionDirectoryEnvVar);
        if (string.IsNullOrWhiteSpace(versionDirectory))
        {
            return null;
        }

        return Acquire(versionDirectory, holderKind, commandName);
    }

    /// <summary>
    /// Adds bundle lease handoff environment variables to a child process environment.
    /// </summary>
    public static void AddEnvironment(IDictionary<string, string> environmentVariables, string versionDirectory)
    {
        ArgumentNullException.ThrowIfNull(environmentVariables);
        ArgumentException.ThrowIfNullOrWhiteSpace(versionDirectory);

        environmentVariables[BundleDiscovery.BundleVersionDirectoryEnvVar] = Path.GetFullPath(versionDirectory);
    }

    /// <summary>
    /// Returns <see langword="true"/> if <paramref name="versionDirectory"/> has any active leases.
    /// Orphaned lease files are removed as they are discovered.
    /// </summary>
    public static bool HasActiveLease(string versionDirectory)
    {
        var leasesDirectory = Path.Combine(versionDirectory, LeasesDirectoryName);
        if (!Directory.Exists(leasesDirectory))
        {
            return false;
        }

        foreach (var leasePath in EnumerateLeaseFiles(leasesDirectory))
        {
            if (!TryDeleteOrphanedLease(leasePath))
            {
                return true;
            }
        }

        TryDeleteEmptyLeaseDirectory(leasesDirectory);
        return false;
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        _stream.Dispose();
    }

    private static IEnumerable<string> EnumerateLeaseFiles(string leasesDirectory)
    {
        try
        {
            return Directory.EnumerateFiles(leasesDirectory, $"*{LeaseExtension}").ToArray();
        }
        catch (Exception ex) when (ex is DirectoryNotFoundException or IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static bool TryDeleteOrphanedLease(string leasePath)
    {
        try
        {
            using var stream = new FileStream(
                leasePath,
                FileMode.Open,
                FileAccess.ReadWrite,
                FileShare.None,
                bufferSize: 1,
                FileOptions.DeleteOnClose);
            return true;
        }
        catch (FileNotFoundException)
        {
            return true;
        }
        catch (DirectoryNotFoundException)
        {
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static void TryDeleteEmptyLeaseDirectory(string leasesDirectory)
    {
        try
        {
            Directory.Delete(leasesDirectory);
        }
        catch (Exception ex) when (ex is DirectoryNotFoundException or IOException or UnauthorizedAccessException)
        {
        }
    }

    private static string CreateLeaseFileName()
    {
        var startTicks = GetCurrentProcessStartTimeTicks();
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{Environment.ProcessId}-{startTicks}-{Guid.NewGuid():N}{LeaseExtension}");
    }

    private static long GetCurrentProcessStartTimeTicks()
    {
        try
        {
            using var process = Process.GetCurrentProcess();
            return process.StartTime.ToUniversalTime().Ticks;
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or System.ComponentModel.Win32Exception)
        {
            return 0;
        }
    }

    private static string CreateMetadataJson(string versionDirectory, string holderKind, string? commandName)
    {
        var versionId = Path.GetFileName(versionDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        var startTicks = GetCurrentProcessStartTimeTicks();
        var acquired = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);

        return $$"""
            {
              "versionId": "{{EscapeJson(versionId)}}",
              "versionDirectory": "{{EscapeJson(versionDirectory)}}",
              "processId": {{Environment.ProcessId}},
              "processStartTimeUtcTicks": {{startTicks}},
              "holderKind": "{{EscapeJson(holderKind)}}",
              "commandName": "{{EscapeJson(commandName)}}",
              "acquiredUtc": "{{acquired}}"
            }
            """;
    }

    private static string EscapeJson(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(value.Length);
        foreach (var ch in value)
        {
            _ = ch switch
            {
                '"' => builder.Append("\\\""),
                '\\' => builder.Append("\\\\"),
                '\b' => builder.Append("\\b"),
                '\f' => builder.Append("\\f"),
                '\n' => builder.Append("\\n"),
                '\r' => builder.Append("\\r"),
                '\t' => builder.Append("\\t"),
                < ' ' => builder.Append(CultureInfo.InvariantCulture, $"\\u{(int)ch:x4}"),
                _ => builder.Append(ch)
            };
        }

        return builder.ToString();
    }
}
