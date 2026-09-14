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
    private readonly HeldFileLease _heldLease;

    private BundleVersionLease(
        string? versionId,
        string versionDirectory,
        string leasePath,
        int processId,
        long processStartTimeUtcTicks,
        string holderKind,
        string? commandName,
        DateTimeOffset acquiredUtc,
        HeldFileLease heldLease)
    {
        VersionId = versionId;
        VersionDirectory = versionDirectory;
        LeasePath = leasePath;
        ProcessId = processId;
        ProcessStartTimeUtcTicks = processStartTimeUtcTicks;
        HolderKind = holderKind;
        CommandName = commandName;
        AcquiredUtc = acquiredUtc;
        _heldLease = heldLease;
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

        var heldLease = HeldFileLease.Acquire(leasesDirectory, CreateLeaseFileNamePrefix(), LeaseExtension);

        try
        {
            var versionId = Path.GetFileName(fullVersionDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            var lease = new BundleVersionLease(
                versionId,
                fullVersionDirectory,
                heldLease.LeasePath,
                Environment.ProcessId,
                GetCurrentProcessStartTimeTicks(),
                holderKind,
                commandName,
                DateTimeOffset.UtcNow,
                heldLease);

            JsonSerializer.Serialize(heldLease.Stream, lease, BundleVersionLeaseJsonSerializerContext.Default.BundleVersionLease);
            heldLease.Stream.Flush(flushToDisk: true);

            return lease;
        }
        catch
        {
            heldLease.Dispose();
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
    /// Returns <see langword="true"/> if <paramref name="versionDirectory"/> has any active leases
    /// or the lease state cannot be determined. Orphaned lease files are removed as they are discovered.
    /// </summary>
    public static bool HasActiveLease(string versionDirectory)
    {
        var leasesDirectory = Path.Combine(versionDirectory, LeasesDirectoryName);
        var result = HeldFileLease.Probe(leasesDirectory, LeaseExtension);
        if (result is HeldFileLeaseProbeResult.None)
        {
            TryDeleteEmptyLeaseDirectory(leasesDirectory);
        }

        // Unknown is deliberately treated as active so cleanup never deletes a version whose
        // lease directory could not be enumerated or whose lease files could not be inspected.
        return result is not HeldFileLeaseProbeResult.None;
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        _heldLease.Dispose();
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

    private static string CreateLeaseFileNamePrefix()
    {
        var startTicks = GetCurrentProcessStartTimeTicks();
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{Environment.ProcessId}-{startTicks}-");
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
