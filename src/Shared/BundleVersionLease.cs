// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// This file is source-linked into multiple projects.
// Do not add project-specific dependencies.

using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

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

    private BundleVersionLease(
        string? versionId,
        string versionDirectory,
        string leasePath,
        int processId,
        long processStartTimeUtcTicks,
        string holderKind,
        string? commandName,
        DateTimeOffset acquiredUtc,
        FileStream stream)
    {
        VersionId = versionId;
        VersionDirectory = versionDirectory;
        LeasePath = leasePath;
        ProcessId = processId;
        ProcessStartTimeUtcTicks = processStartTimeUtcTicks;
        HolderKind = holderKind;
        CommandName = commandName;
        AcquiredUtc = acquiredUtc;
        _stream = stream;
    }

    /// <summary>
    /// Gets the leased version id.
    /// </summary>
    public string? VersionId { get; }

    /// <summary>
    /// Gets the leased version directory.
    /// </summary>
    public string VersionDirectory { get; }

    /// <summary>
    /// Gets the lease metadata path.
    /// </summary>
    [JsonIgnore]
    public string LeasePath { get; }

    /// <summary>
    /// Gets the process id that acquired the lease.
    /// </summary>
    public int ProcessId { get; }

    /// <summary>
    /// Gets the UTC start time ticks for the process that acquired the lease.
    /// </summary>
    public long ProcessStartTimeUtcTicks { get; }

    /// <summary>
    /// Gets the kind of process holding the lease.
    /// </summary>
    public string HolderKind { get; }

    /// <summary>
    /// Gets the command name associated with the lease, if any.
    /// </summary>
    public string? CommandName { get; }

    /// <summary>
    /// Gets when the lease was acquired.
    /// </summary>
    public DateTimeOffset AcquiredUtc { get; }

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
            var versionId = Path.GetFileName(fullVersionDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            var lease = new BundleVersionLease(
                versionId,
                fullVersionDirectory,
                leasePath,
                Environment.ProcessId,
                GetCurrentProcessStartTimeTicks(),
                holderKind,
                commandName,
                DateTimeOffset.UtcNow,
                stream);

            JsonSerializer.Serialize(stream, lease, BundleVersionLeaseJsonSerializerContext.Default.BundleVersionLease);
            stream.Flush(flushToDisk: true);

            return lease;
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
    /// Adds bundle lease handoff environment variables to a child process environment.
    /// </summary>
    public void AddEnvironment(IDictionary<string, string> environmentVariables)
        => AddEnvironment(environmentVariables, VersionDirectory);

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
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true)]
[JsonSerializable(typeof(BundleVersionLease))]
internal sealed partial class BundleVersionLeaseJsonSerializerContext : JsonSerializerContext;
