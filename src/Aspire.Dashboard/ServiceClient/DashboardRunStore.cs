// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using System.IO.Hashing;
using System.Text;
using System.Text.Json;
using Aspire.Dashboard.Configuration;
using Aspire.Shared;
using Microsoft.Extensions.Options;

namespace Aspire.Dashboard.ServiceClient;

/// <summary>
/// Provides the dashboard runs available for selection.
/// </summary>
public interface IDashboardRunStore
{
    /// <summary>
    /// Gets a value indicating whether historical dashboard runs can be selected.
    /// </summary>
    bool SupportsRunSelection { get; }

    /// <summary>
    /// Gets the current and historical dashboard runs available for selection.
    /// </summary>
    /// <returns>The available dashboard runs.</returns>
    IReadOnlyList<DashboardRunDescriptor> GetRuns();

    /// <summary>
    /// Attempts to acquire a lease that keeps the specified dashboard run available while it is selected.
    /// </summary>
    /// <param name="run">The dashboard run to lease.</param>
    /// <returns>A lease for the dashboard run, or <see langword="null"/> when the run is no longer available.</returns>
    IDisposable? TryAcquireRunLease(DashboardRunDescriptor run);
}

internal sealed class DashboardRunStore : IDashboardRunStore, IDisposable
{
    private const string TemporaryDirectoryPrefix = "aspire-dashboard-";

    internal const string DatabaseFileName = "dashboard.db";
    internal const int MaxApplicationDirectoryNameLength = 80;
    internal const int MaxRuns = 10;
    internal const int SchemaVersion = DashboardSqliteDatabase.SchemaVersion;

    private static readonly JsonSerializerOptions s_jsonOptions = new() { WriteIndented = true };

    private readonly string? _runsDirectory;
    private readonly string? _metadataPath;
    private readonly string? _temporaryDirectory;
    private readonly FileStream? _runLock;
    private readonly DashboardRunMetadata _metadata;
    private readonly ILogger<DashboardRunStore> _logger;
    private readonly TimeProvider _timeProvider;
    private readonly Action<string> _deleteRunDirectory;
    private readonly Lazy<IReadOnlyList<DashboardRunDescriptor>> _runs;
    private bool _metadataPublished;

    public DashboardRunStore(IOptions<DashboardOptions> options, ILogger<DashboardRunStore> logger, TimeProvider timeProvider)
        : this(options, logger, timeProvider, static directory => Directory.Delete(directory, recursive: true))
    {
    }

    internal DashboardRunStore(
        IOptions<DashboardOptions> options,
        ILogger<DashboardRunStore> logger,
        TimeProvider timeProvider,
        Action<string> deleteRunDirectory)
    {
        _logger = logger;
        _timeProvider = timeProvider;
        _deleteRunDirectory = deleteRunDirectory;
        var applicationName = string.IsNullOrWhiteSpace(options.Value.ApplicationName) ? "Aspire" : options.Value.ApplicationName;
        var startedAt = timeProvider.GetUtcNow();
        // A millisecond timestamp collision is very unlikely. The exclusive run lock below also ensures that if two
        // Dashboard instances resolve the same run ID concurrently, the second fails instead of sharing the database.
        // Format invariantly. This value is a durable directory name and the ordinal sort key used by
        // PruneRuns, so a non-Gregorian current culture (th-TH, ar-SA) would produce IDs that sort
        // against previous runs incorrectly and let retention delete newer runs.
        var runId = startedAt.ToString("yyyyMMddTHHmmssfffZ", CultureInfo.InvariantCulture);
        PersistenceMode = options.Value.Data.PersistenceMode;

        // Persistent data can contain environment variables, telemetry, and console logs. Restrict the
        // application directory so the database, WAL, shared-memory, and metadata files aren't exposed
        // to other local users even when the data root was created with a permissive umask.
        switch (PersistenceMode)
        {
            case DashboardPersistenceMode.None:
                _temporaryDirectory = Directory.CreateTempSubdirectory(TemporaryDirectoryPrefix).FullName;
                RunDirectory = _temporaryDirectory;
                DatabasePath = Path.Combine(RunDirectory, DatabaseFileName);
                _runLock = OpenRunLock(RunDirectory);
                DeleteAbandonedTemporaryDirectories(deleteRunDirectory);
                break;
            case DashboardPersistenceMode.Run:
                var applicationDirectory = GetApplicationDirectory(options.Value.Data.Directory, applicationName);
                DirectoryHelper.CreateWithOwnerOnlyPermissions(applicationDirectory);
                _runsDirectory = Path.Combine(applicationDirectory, "runs");
                RunDirectory = Path.Combine(_runsDirectory, runId);
                DatabasePath = Path.Combine(RunDirectory, DatabaseFileName);
                Directory.CreateDirectory(RunDirectory);
                _runLock = OpenRequiredRunLock(
                    RunDirectory,
                    $"Dashboard run '{runId}' is already in use by another dashboard process.");
                _metadataPath = Path.Combine(RunDirectory, "run.json");
                break;
            case DashboardPersistenceMode.Resume:
                RunDirectory = GetApplicationDirectory(options.Value.Data.Directory, applicationName);
                DatabasePath = Path.Combine(RunDirectory, DatabaseFileName);
                DirectoryHelper.CreateWithOwnerOnlyPermissions(RunDirectory);
                var resumeRunLock = OpenRequiredRunLock(
                    RunDirectory,
                    $"Dashboard data for application '{applicationName}' is already in use by another dashboard process. Database path: '{DatabasePath}'.");
                try
                {
                    if (!File.Exists(DatabasePath))
                    {
                        _logger.LogDebug("Creating dashboard database at '{DatabasePath}'.", DatabasePath);
                    }
                    else if (!DashboardSqliteDatabase.IsCompatible(DatabasePath))
                    {
                        _logger.LogInformation(
                            "Existing dashboard database at '{DatabasePath}' is incompatible with schema version {SchemaVersion} and will be replaced.",
                            DatabasePath,
                            SchemaVersion);
                        DeleteDatabaseFiles(DatabasePath);
                    }
                    else
                    {
                        _logger.LogDebug("Resuming dashboard database at '{DatabasePath}'.", DatabasePath);
                    }

                    _runLock = resumeRunLock;
                }
                catch
                {
                    resumeRunLock.Dispose();
                    throw;
                }
                break;
            default:
                throw new InvalidOperationException($"Unexpected dashboard persistence mode: {PersistenceMode}");
        }

        _metadata = new DashboardRunMetadata
        {
            SchemaVersion = SchemaVersion,
            RunId = runId,
            StartedAtUtc = startedAt,
            ApplicationName = options.Value.ApplicationName,
            DatabaseFileName = Path.GetFileName(DatabasePath)
        };
        _runs = new(LoadRuns);

        _logger.LogDebug(
            "Dashboard run store initialized with persistence mode '{PersistenceMode}'. Run directory: '{RunDirectory}'. Database path: '{DatabasePath}'.",
            PersistenceMode,
            RunDirectory,
            DatabasePath);
    }

    private void DeleteAbandonedTemporaryDirectories(Action<string> deleteRunDirectory)
    {
        var temporaryRoot = Directory.GetParent(RunDirectory)!.FullName;
        foreach (var directory in Directory.EnumerateDirectories(temporaryRoot, $"{TemporaryDirectoryPrefix}*"))
        {
            if (string.Equals(directory, RunDirectory, StringComparison.OrdinalIgnoreCase) ||
                !File.Exists(Path.Combine(directory, DatabaseFileName)))
            {
                continue;
            }

            using var runLock = TryOpenRunLock(directory);
            if (runLock is null)
            {
                continue;
            }

            try
            {
                deleteRunDirectory(directory);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(
                    exception,
                    "Failed to delete abandoned dashboard temporary directory '{RunDirectory}'. The directory may still be in use by another dashboard process.",
                    directory);
            }
        }
    }

    public string RunDirectory { get; }
    public string DatabasePath { get; }
    public string RunId => _metadata.RunId;
    public DashboardPersistenceMode PersistenceMode { get; }
    public bool SupportsRunSelection => PersistenceMode == DashboardPersistenceMode.Run;

    public IReadOnlyList<DashboardRunDescriptor> GetRuns()
    {
        var runs = _runs.Value;
        return runs.Any(run => run.IsPruned || !run.IsSelectable)
            ? runs.Where(run => !run.IsPruned && run.IsSelectable).ToArray()
            : runs;
    }

    internal void PublishRun()
    {
        if (_metadataPath is null || _metadataPublished)
        {
            return;
        }

        WriteMetadata(_metadata);
        _metadataPublished = true;
    }

    /// <summary>
    /// Deletes run directories beyond the retention limit.
    /// </summary>
    /// <remarks>
    /// Kept separate from <see cref="PublishRun"/> so it can run after the host has started. Pruning walks every
    /// run directory, takes a cross-process lock on each, and recursively deletes it, so on a slow or contended
    /// file system it can take a long time. Publishing must happen during startup because other processes read
    /// the metadata, but pruning is housekeeping and must not hold up the dashboard accepting requests.
    /// </remarks>
    internal void PruneExpiredRuns()
    {
        if (!_metadataPublished || _runsDirectory is null || !Directory.Exists(_runsDirectory))
        {
            return;
        }

        PruneRuns(_deleteRunDirectory);
    }

    public IDisposable? TryAcquireRunLease(DashboardRunDescriptor run)
    {
        var runDirectory = Path.GetDirectoryName(run.DatabasePath)!;
        return TryOpenRunLock(runDirectory);
    }

    private IReadOnlyList<DashboardRunDescriptor> LoadRuns()
    {
        var runs = new List<DashboardRunDescriptor>
        {
            CreateDescriptor(_metadata, RunDirectory, isCurrent: true)
        };

        if (SupportsRunSelection && Directory.Exists(_runsDirectory))
        {
            foreach (var directory in Directory.EnumerateDirectories(_runsDirectory))
            {
                if (string.Equals(directory, RunDirectory, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var metadataPath = Path.Combine(directory, "run.json");
                try
                {
                    var metadata = JsonSerializer.Deserialize<DashboardRunMetadata>(File.ReadAllText(metadataPath));
                    if (metadata is { SchemaVersion: SchemaVersion })
                    {
                        var run = CreateDescriptor(metadata, directory, isCurrent: false);
                        // Filter out in-progress runs that are owned by other Dashboard instances.
                        using var runLock = TryAcquireRunLease(run);
                        run.IsSelectable = runLock is not null;
                        runs.Add(run);
                    }
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
                {
                    // Ignore incomplete or unreadable run metadata.
                }
            }
        }

        var orderedRuns = runs
            .OrderByDescending(run => run.IsCurrent)
            .ThenByDescending(run => run.StartedAtUtc)
            .ToArray();
        _logger.LogDebug(
            "Dashboard run discovery completed in directory '{RunsDirectory}'. Run count: {RunCount}. Run IDs: {RunIds}.",
            _runsDirectory ?? RunDirectory,
            orderedRuns.Length,
            string.Join(", ", orderedRuns.Select(run => run.RunId)));

        return orderedRuns;
    }

    public void Dispose()
    {
        try
        {
            if (_metadataPublished)
            {
                WriteMetadata(_metadata with { EndedAtUtc = _timeProvider.GetUtcNow(), CleanShutdown = true });
            }
            else if (_temporaryDirectory is not null && Directory.Exists(_temporaryDirectory))
            {
                Directory.Delete(_temporaryDirectory, recursive: true);
            }
        }
        finally
        {
            _runLock?.Dispose();
        }
    }

    private void WriteMetadata(DashboardRunMetadata metadata)
    {
        // Write to a sibling temp file and rename over the target. Overwriting run.json in place means a
        // crash or power loss part-way through leaves a truncated file and the run becomes unreadable on
        // the next start. A rename within the same directory is atomic on both Windows and Unix.
        var metadataPath = _metadataPath!;
        var temporaryPath = $"{metadataPath}.tmp";

        try
        {
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(metadata, s_jsonOptions));
            File.Move(temporaryPath, metadataPath, overwrite: true);
        }
        catch
        {
            try
            {
                File.Delete(temporaryPath);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Best effort. A leftover temp file doesn't affect run discovery, which only reads run.json.
            }

            throw;
        }
    }

    private void PruneRuns(Action<string> deleteRunDirectory)
    {
        foreach (var run in _runs.Value.Skip(MaxRuns))
        {
            var directory = Path.GetDirectoryName(run.DatabasePath)!;
            using var runLock = TryOpenRunLock(directory);
            if (runLock is null)
            {
                continue;
            }

            try
            {
                deleteRunDirectory(directory);
                run.IsPruned = true;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(
                    exception,
                    "Failed to delete expired dashboard run directory '{RunDirectory}'. The directory may still be in use by another dashboard process.",
                    directory);
            }
        }
    }

    private static FileStream OpenRunLock(string runDirectory)
    {
        // An exclusive FileStream is the best cross-platform option for locking across processes because .NET named
        // semaphores are only supported on Windows. Keep the lock beside the run directory so pruning can hold it
        // while recursively deleting the directory on Windows.
        return new FileStream(
            GetRunLockPath(runDirectory),
            FileMode.OpenOrCreate,
            FileAccess.ReadWrite,
            FileShare.None,
            bufferSize: 1,
            FileOptions.DeleteOnClose);
    }

    private static FileStream OpenRequiredRunLock(string runDirectory, string errorMessage)
    {
        try
        {
            return OpenRunLock(runDirectory);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException(errorMessage, exception);
        }
    }

    private static FileStream? TryOpenRunLock(string runDirectory)
    {
        try
        {
            var runLock = OpenRunLock(runDirectory);
            // The lock file is adjacent to the run directory, so OpenOrCreate can recreate it after pruning has already
            // deleted the directory. Check after acquiring the lock to avoid racing with a cooperating pruner.
            if (!Directory.Exists(runDirectory))
            {
                runLock.Dispose();
                return null;
            }

            return runLock;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    internal static string GetRunLockPath(string runDirectory) => $"{runDirectory}.lock";

    private static DashboardRunDescriptor CreateDescriptor(DashboardRunMetadata metadata, string runDirectory, bool isCurrent)
    {
        return new DashboardRunDescriptor(
            metadata.RunId,
            metadata.SchemaVersion,
            metadata.StartedAtUtc,
            metadata.EndedAtUtc,
            metadata.CleanShutdown,
            metadata.ApplicationName,
            Path.Combine(runDirectory, metadata.DatabaseFileName),
            isCurrent);
    }

    internal static string GetApplicationDirectory(string? dataRoot, string applicationName)
    {
        if (string.IsNullOrWhiteSpace(dataRoot))
        {
            dataRoot = Path.Combine(AspireHomeDirectory.GetDefault(), "dashboard");
        }

        return Path.Combine(Path.GetFullPath(dataRoot), GetApplicationDirectoryName(applicationName));
    }

    private static void DeleteDatabaseFiles(string databasePath)
    {
        foreach (var path in new[] { databasePath, $"{databasePath}-wal", $"{databasePath}-shm" })
        {
            File.Delete(path);
        }
    }

    internal static string GetApplicationDirectoryName(string applicationName)
    {
        ArgumentException.ThrowIfNullOrEmpty(applicationName);

        const int hashLength = 16;
        const int separatorLength = 1;
        var maxPrefixLength = MaxApplicationDirectoryNameLength - separatorLength - hashLength;
        var prefixBuilder = new StringBuilder(Math.Min(applicationName.Length, maxPrefixLength));

        foreach (var character in applicationName)
        {
            if (prefixBuilder.Length == maxPrefixLength)
            {
                break;
            }

            prefixBuilder.Append(character is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '-' or '_'
                ? character
                : '-');
        }

        var prefix = prefixBuilder.ToString().Trim('-', '_');
        if (prefix.Length == 0)
        {
            prefix = "dashboard";
        }

        var hash = Convert.ToHexString(XxHash3.Hash(Encoding.UTF8.GetBytes(applicationName))).ToLowerInvariant();
        return $"{prefix}-{hash}";
    }

    private sealed record DashboardRunMetadata
    {
        public required int SchemaVersion { get; init; }
        public required string RunId { get; init; }
        public required DateTimeOffset StartedAtUtc { get; init; }
        public DateTimeOffset? EndedAtUtc { get; init; }
        public bool CleanShutdown { get; init; }
        public string? ApplicationName { get; init; }
        public required string DatabaseFileName { get; init; }
    }
}

/// <summary>
/// Describes a dashboard run available for selection.
/// </summary>
/// <param name="RunId">The unique identifier for the dashboard run.</param>
/// <param name="SchemaVersion">The dashboard database schema version used by the run.</param>
/// <param name="StartedAtUtc">The time at which the run started.</param>
/// <param name="EndedAtUtc">The time at which the run ended, or <see langword="null"/> when it has not ended.</param>
/// <param name="CleanShutdown">A value indicating whether the run shut down cleanly.</param>
/// <param name="ApplicationName">The application name associated with the run.</param>
/// <param name="DatabasePath">The path to the dashboard database for the run.</param>
/// <param name="IsCurrent">A value indicating whether this is the current dashboard run.</param>
public sealed record DashboardRunDescriptor(
    string RunId,
    int SchemaVersion,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset? EndedAtUtc,
    bool CleanShutdown,
    string? ApplicationName,
    string DatabasePath,
    bool IsCurrent)
{
    /// <summary>
    /// Gets or sets a value indicating whether the dashboard run was pruned.
    /// </summary>
    public bool IsPruned { get; set; }

    internal bool IsSelectable { get; set; } = true;
}