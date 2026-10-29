// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using System.IO.Hashing;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Aspire.Dashboard.Configuration;
using Aspire.Dashboard.Serialization;
using Aspire.Shared;
using Microsoft.Extensions.Options;

namespace Aspire.Dashboard.ServiceClient;

/// <summary>
/// Provides discovered dashboard runs.
/// </summary>
public interface IDashboardRunStore
{
    /// <summary>
    /// Gets a value indicating whether historical dashboard runs can be selected.
    /// </summary>
    bool SupportsRunSelection { get; }

    /// <summary>
    /// Gets the current and historical dashboard runs that have not been pruned.
    /// </summary>
    /// <returns>The discovered dashboard runs.</returns>
    IReadOnlyList<DashboardRunDescriptor> GetRuns();

    /// <summary>
    /// Gets the current dashboard run.
    /// </summary>
    /// <returns>The current dashboard run.</returns>
    DashboardRunDescriptor GetCurrentRun();

    /// <summary>
    /// Gets the dashboard run with the specified ID.
    /// </summary>
    /// <param name="runId">The ID of the dashboard run.</param>
    /// <param name="onlyCompatible"><see langword="true"/> to return only runs compatible with the current dashboard version; otherwise, <see langword="false"/>.</param>
    /// <returns>The dashboard run, or <see langword="null"/> when the run is not available.</returns>
    DashboardRunDescriptor? GetRunById(string runId, bool onlyCompatible);

    /// <summary>
    /// Pins or unpins the specified dashboard run.
    /// </summary>
    /// <param name="run">The dashboard run to update.</param>
    /// <param name="isPinned"><see langword="true"/> to pin the dashboard run; <see langword="false"/> to unpin it.</param>
    void SetRunPinned(DashboardRunDescriptor run, bool isPinned);

    /// <summary>
    /// Attempts to acquire a lease that keeps the specified dashboard run available while it is selected.
    /// </summary>
    /// <param name="run">The dashboard run to lease.</param>
    /// <returns>A lease for the dashboard run, or <see langword="null"/> when the run is no longer available.</returns>
    IDisposable? TryAcquireRunLease(DashboardRunDescriptor run);

    /// <summary>
    /// Publishes the current dashboard run so it can be discovered by future dashboard processes.
    /// </summary>
    void PublishRun();

    /// <summary>
    /// Deletes dashboard runs beyond the retention limit and abandoned run lock files.
    /// </summary>
    void PruneExpiredRuns();
}

internal sealed class DashboardRunStore : IDashboardRunStore, IDisposable
{
    private const string TemporaryDirectoryPrefix = "aspire-dashboard-";

    private static readonly TimeSpan s_minimumLockAge = TimeSpan.FromDays(1);

    internal const string DatabaseFileName = "dashboard.db";
    internal const int MaxApplicationDirectoryNameLength = 80;
    internal const int MaxRuns = 10;
    internal const int SchemaVersion = DashboardSqliteDatabase.SchemaVersion;

    // Preserve the persisted PascalCase names and indentation without changing the shared context's defaults.
    private static readonly DashboardJsonSerializerContext s_jsonContext = new(new JsonSerializerOptions { WriteIndented = true });

    private readonly string? _runsDirectory;
    private string? _metadataPath;
    private readonly string _applicationMarkerFileName;
    private readonly string? _temporaryDirectory;
    private FileLock? _runLock;
    private DashboardRunMetadata _metadata;
    private readonly ILogger<DashboardRunStore> _logger;
    private readonly TimeProvider _timeProvider;
    private readonly Action<string> _deleteRunDirectory;
    private Lazy<IReadOnlyList<DashboardRunDescriptor>> _runs;
    private string? _appHostRunId;
    private readonly object _runStateLock = new();
    // Retired current readers and read-only history share directory ownership, even though
    // their SQLite connections differ. Descriptor snapshots are rebuilt on every rollover.
    private readonly Dictionary<string, OwnedRunLock> _leasedRuns = new(StringComparer.Ordinal);
    private int _references = 1;
    private bool _disposeRequested;
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
        var applicationName = options.Value.GetApplicationNameOrDefault();
        _applicationMarkerFileName = GetApplicationDirectoryName(applicationName);
        var startedAt = timeProvider.GetUtcNow();
        // A millisecond timestamp collision is very unlikely. The exclusive run lock below also ensures that if two
        // Dashboard instances resolve the same run ID concurrently, the second fails instead of sharing the database.
        // Format invariantly. This value is a durable directory name and the ordinal sort key used by
        // PruneRuns, so a non-Gregorian current culture (th-TH, ar-SA) would produce IDs that sort
        // against previous runs incorrectly and let retention delete newer runs.
        var runId = startedAt.ToString("yyyyMMddTHHmmssfffZ", CultureInfo.InvariantCulture);
        PersistenceMode = options.Value.Data.PersistenceMode;

        // Persistent data can contain environment variables, telemetry, and console logs. Restrict the
        // persistence directory so the database, WAL, shared-memory, and metadata files aren't exposed
        // to other local users even when the data root was created with a permissive umask.
        switch (PersistenceMode)
        {
            case DashboardPersistenceMode.None:
                // CreateTempSubdirectory uses owner-only permissions (0700) on Unix and inherits the
                // current user's temporary directory ACL on Windows.
                _temporaryDirectory = Directory.CreateTempSubdirectory(TemporaryDirectoryPrefix).FullName;
                CurrentWorkingDirectory = _temporaryDirectory;
                DatabasePath = Path.Combine(CurrentWorkingDirectory, DatabaseFileName);
                _runLock = OpenRunLock(CurrentWorkingDirectory);
                DeleteAbandonedTemporaryDirectories(deleteRunDirectory);
                break;
            case DashboardPersistenceMode.Run:
                _runsDirectory = GetRunsDirectory(options.Value.Data.Directory);
                DirectoryHelper.CreateWithOwnerOnlyPermissions(_runsDirectory);
                CurrentWorkingDirectory = Path.Combine(_runsDirectory, runId);
                DatabasePath = Path.Combine(CurrentWorkingDirectory, DatabaseFileName);
                // Assume that if the run directory can be created, then files in the run directory can be written as well.
                Directory.CreateDirectory(CurrentWorkingDirectory);
                var runLock = OpenRequiredRunLock(
                    CurrentWorkingDirectory,
                    $"Dashboard run '{runId}' is already in use by another dashboard process.");
                try
                {
                    File.WriteAllText(Path.Combine(CurrentWorkingDirectory, _applicationMarkerFileName), string.Empty);
                    _runLock = runLock;
                }
                catch
                {
                    runLock.Dispose();
                    throw;
                }
                _metadataPath = Path.Combine(CurrentWorkingDirectory, "run.json");
                break;
            case DashboardPersistenceMode.Resume:
                var resumesDirectory = GetResumesDirectory(options.Value.Data.Directory);
                DirectoryHelper.CreateWithOwnerOnlyPermissions(resumesDirectory);
                CurrentWorkingDirectory = Path.Combine(resumesDirectory, _applicationMarkerFileName);
                DatabasePath = Path.Combine(CurrentWorkingDirectory, DatabaseFileName);
                DirectoryHelper.CreateWithOwnerOnlyPermissions(CurrentWorkingDirectory);
                var resumeRunLock = OpenRequiredRunLock(
                    CurrentWorkingDirectory,
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
            ApplicationName = applicationName,
            DatabaseFileName = Path.GetFileName(DatabasePath)
        };
        _runs = new(LoadRuns);

        _logger.LogDebug(
            "Dashboard run store initialized with persistence mode '{PersistenceMode}'. Current working directory: '{CurrentWorkingDirectory}'. Database path: '{DatabasePath}'.",
            PersistenceMode,
            CurrentWorkingDirectory,
            DatabasePath);
    }

    private void DeleteAbandonedTemporaryDirectories(Action<string> deleteRunDirectory)
    {
        var temporaryRoot = Directory.GetParent(CurrentWorkingDirectory)!.FullName;
        foreach (var directory in Directory.EnumerateDirectories(temporaryRoot, $"{TemporaryDirectoryPrefix}*"))
        {
            if (string.Equals(directory, CurrentWorkingDirectory, StringComparison.OrdinalIgnoreCase) ||
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

        DeleteUnheldLocks(
            temporaryRoot,
            $"{TemporaryDirectoryPrefix}*.lock",
            GetRunLockPath(CurrentWorkingDirectory));
    }

    private void DeleteUnheldLocks(string directory, string searchPattern, string currentLockPath)
    {
        var lockCutoff = _timeProvider.GetUtcNow().UtcDateTime - s_minimumLockAge;
        foreach (var lockPath in Directory.EnumerateFiles(directory, searchPattern, SearchOption.TopDirectoryOnly))
        {
            if (string.Equals(lockPath, currentLockPath, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            DateTime lastWriteTimeUtc;
            try
            {
                lastWriteTimeUtc = File.GetLastWriteTimeUtc(lockPath);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Permissions can change after enumeration, or a matching lock in the shared temporary root may
                // belong to another user. Lock cleanup is best effort and must not prevent dashboard startup.
                continue;
            }

            if (lastWriteTimeUtc > lockCutoff)
            {
                // On Unix, another process can observe a newly created lock file before FileStream has applied its
                // exclusive lock. Delay cleanup so initialization has ample time to establish ownership.
                continue;
            }

            // Acquiring the lock proves no dashboard process currently owns it. FileLock uses DeleteOnClose,
            // so disposing a successfully acquired stale lock removes the file while active locks remain untouched.
            using var runLock = FileLock.TryAcquire(lockPath);
        }
    }

    internal bool StartAppHostRun(string appHostRunId, out IDisposable? retiredRunOwnership)
    {
        lock (_runStateLock)
        {
            retiredRunOwnership = null;
            if (string.IsNullOrEmpty(appHostRunId) || _appHostRunId == appHostRunId)
            {
                return false;
            }

            if (_appHostRunId is null || !SupportsRunSelection)
            {
                _appHostRunId = appHostRunId;
                return false;
            }

            // The AppHost ID is only an identity token. Never use a remote-supplied value as a path.
            var now = _timeProvider.GetUtcNow();
            // Keep directory IDs monotonic even when two restarts occur within one clock tick.
            var startedAt = now > _metadata.StartedAtUtc ? now : _metadata.StartedAtUtc.AddMilliseconds(1);
            var runId = startedAt.ToString("yyyyMMddTHHmmssfffZ", CultureInfo.InvariantCulture);
            var directory = Path.Combine(_runsDirectory!, runId);
            if (directory == CurrentWorkingDirectory)
            {
                runId = startedAt.AddMilliseconds(1).ToString("yyyyMMddTHHmmssfffZ", CultureInfo.InvariantCulture);
                directory = Path.Combine(_runsDirectory!, runId);
            }
            Directory.CreateDirectory(directory);
            var runLock = OpenRequiredRunLock(directory, $"Dashboard run '{runId}' is already in use.");
            try
            {
                File.WriteAllText(Path.Combine(directory, _applicationMarkerFileName), string.Empty);
                WriteMetadata(_metadata with { EndedAtUtc = startedAt, CleanShutdown = true });
            }
            catch
            {
                runLock.Dispose();
                throw;
            }

            // Transfer the held OS lock without closing it. Reopening it after publishing the new
            // run would leave a gap in which another dashboard could acquire and prune this database.
            var retiredRunId = RunId;
            var retiredLock = new OwnedRunLock(_runLock!) { ReferenceCount = 1 };
            _leasedRuns.Add(retiredRunId, retiredLock);
            retiredRunOwnership = new RunLease(this, retiredRunId, retiredLock);
            _runLock = runLock;
            CurrentWorkingDirectory = directory;
            DatabasePath = Path.Combine(directory, DatabaseFileName);
            _metadataPath = Path.Combine(directory, "run.json");
            _metadata = _metadata with { RunId = runId, StartedAtUtc = startedAt, EndedAtUtc = null, CleanShutdown = false, IsPinned = false };
            _metadataPublished = false;
            _appHostRunId = appHostRunId;
            _runs = new(LoadRuns);
            return true;
        }
    }

    public string CurrentWorkingDirectory { get; private set; }
    public string DatabasePath { get; private set; }
    public string RunId => _metadata.RunId;
    public DashboardPersistenceMode PersistenceMode { get; }
    public bool SupportsRunSelection => PersistenceMode == DashboardPersistenceMode.Run;

    public IReadOnlyList<DashboardRunDescriptor> GetRuns()
    {
        // Lazy discovery reads both metadata and paths. Keep that snapshot in one run generation
        // when a browser opens the selector concurrently with an AppHost reconnect.
        lock (_runStateLock)
        {
            return _runs.Value.Where(run => !run.IsPruned).ToArray();
        }
    }

    public DashboardRunDescriptor GetCurrentRun() => GetRuns().Single(run => run.IsCurrent);

    public DashboardRunDescriptor? GetRunById(string runId, bool onlyCompatible) =>
        GetRuns().SingleOrDefault(run =>
            (!onlyCompatible || run.IsCompatible) &&
            string.Equals(run.RunId, runId, StringComparison.Ordinal));

    public void SetRunPinned(DashboardRunDescriptor run, bool isPinned)
    {
        lock (_runStateLock)
        {
            // Resolve inside the transition lock: a previously current descriptor can become history.
            var storedRun = GetRunById(run.RunId, onlyCompatible: false)
                ?? throw new InvalidOperationException($"Dashboard run '{run.RunId}' is no longer available.");
            var runDirectory = Path.GetDirectoryName(storedRun.DatabasePath)!;
            using var runLock = storedRun.IsCurrent || storedRun.IsLeased
                ? null
                : TryOpenRunLock(runDirectory)
                    ?? throw new InvalidOperationException($"Dashboard run '{storedRun.RunId}' is no longer available.");
            UpdatePinnedState(storedRun, runDirectory, isPinned);
        }
    }

    private void UpdatePinnedState(DashboardRunDescriptor run, string runDirectory, bool isPinned)
    {
        var metadataPath = Path.Combine(runDirectory, "run.json");
        var metadata = JsonSerializer.Deserialize(File.ReadAllText(metadataPath), s_jsonContext.DashboardRunMetadata);
        if (metadata?.SchemaVersion != run.SchemaVersion ||
            !string.Equals(metadata.RunId, run.RunId, StringComparison.Ordinal))
        {
            throw new InvalidDataException($"Dashboard run metadata for '{run.RunId}' is invalid.");
        }

        var updatedMetadata = metadata with { IsPinned = isPinned };
        WriteMetadata(updatedMetadata, metadataPath);
        if (string.Equals(run.RunId, RunId, StringComparison.Ordinal))
        {
            _metadata = updatedMetadata;
        }

        run.IsPinned = isPinned;
    }

    public void PublishRun()
    {
        // Publication shares both the metadata snapshot and its temporary file with pinning
        // and rollover. Keep the snapshot, destination, and published flag in one generation.
        lock (_runStateLock)
        {
            if (_metadataPath is null || _metadataPublished)
            {
                return;
            }

            WriteMetadata(_metadata);
            _metadataPublished = true;
        }
    }

    /// <summary>
    /// Deletes run directories beyond the retention limit and abandoned run lock files.
    /// </summary>
    /// <remarks>
    /// Kept separate from <see cref="PublishRun"/> because pruning walks every run directory, takes a cross-process
    /// lock on each, and recursively deletes it. On a slow or contended file system it can take a long time, so
    /// pruning is background housekeeping and must not hold up the dashboard accepting requests.
    /// </remarks>
    public void PruneExpiredRuns()
    {
        DashboardRunDescriptor[] candidates;
        string currentLockPath;
        lock (_runStateLock)
        {
            if (!_metadataPublished || _runsDirectory is null || !Directory.Exists(_runsDirectory))
            {
                return;
            }

            candidates = _runs.Value.Where(run => !run.IsPinned && !run.IsPruned).Skip(MaxRuns).ToArray();
            currentLockPath = GetRunLockPath(CurrentWorkingDirectory);
        }

        PruneRuns(candidates, _deleteRunDirectory);
        // A newer run may now be current, but its held file lock protects it from cleanup as well.
        DeleteUnheldLocks(_runsDirectory, "*.lock", currentLockPath);
    }

    internal Task PruneExpiredRunsInBackground()
    {
        IDisposable retention;
        try
        {
            retention = Retain();
        }
        catch (ObjectDisposedException)
        {
            return Task.CompletedTask;
        }

        // Retain the store until housekeeping ends, even if the host starts shutting down meanwhile.
        // Only candidate selection shares the transition lock; deletion holds just the candidate's file lock.
        return Task.Run(() =>
        {
            using (retention)
            {
                try
                {
                    PruneExpiredRuns();
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to prune expired dashboard runs.");
                }
            }
        });
    }

    public IDisposable? TryAcquireRunLease(DashboardRunDescriptor run)
    {
        lock (_runStateLock)
        {
            // Rollover replaces cached descriptors. Resolve and mark the lease in the same
            // generation so a concurrent selector refresh observes this dashboard's ownership.
            var storedRun = GetRunById(run.RunId, onlyCompatible: true);
            if (storedRun is null)
            {
                return null;
            }

            if (!_leasedRuns.TryGetValue(storedRun.RunId, out var ownedLock))
            {
                var runDirectory = Path.GetDirectoryName(storedRun.DatabasePath)!;
                var runLock = TryOpenRunLock(runDirectory);
                if (runLock is null)
                {
                    return null;
                }

                ownedLock = new OwnedRunLock(runLock);
                _leasedRuns.Add(storedRun.RunId, ownedLock);
            }

            ownedLock.ReferenceCount++;
            ownedLock.Descriptors.Add(storedRun);
            storedRun.IsLeased = true;
            return new RunLease(this, storedRun.RunId, ownedLock);
        }
    }

    private IReadOnlyList<DashboardRunDescriptor> LoadRuns()
    {
        var runs = new List<DashboardRunDescriptor>
        {
            CreateDescriptor(_metadata, CurrentWorkingDirectory, isCurrent: true)
        };

        if (SupportsRunSelection && Directory.Exists(_runsDirectory))
        {
            foreach (var directory in Directory.EnumerateDirectories(_runsDirectory))
            {
                if (string.Equals(directory, CurrentWorkingDirectory, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                // Each run has an empty file named with its application directory key. Checking that marker first
                // avoids opening and deserializing run.json for every other application in the shared runs directory.
                if (!File.Exists(Path.Combine(directory, _applicationMarkerFileName)))
                {
                    continue;
                }

                var metadataPath = Path.Combine(directory, "run.json");
                try
                {
                    var metadata = JsonSerializer.Deserialize(File.ReadAllText(metadataPath), s_jsonContext.DashboardRunMetadata);
                    if (metadata is not null)
                    {
                        var run = CreateDescriptor(metadata, directory, isCurrent: false);
                        if (run.IsCompatible)
                        {
                            // Filter out in-progress runs that are owned by other Dashboard instances.
                            using var runLock = TryOpenRunLock(directory);
                            run.IsSelectable = run.IsLeased || runLock is not null;
                        }
                        else
                        {
                            run.IsSelectable = false;
                        }

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
            .ThenByDescending(run => run.IsPinned)
            .ThenByDescending(run => run.StartedAtUtc)
            .ToArray();
        _logger.LogDebug(
            "Dashboard run discovery completed in directory '{RunsDirectory}'. Run count: {RunCount}. Run IDs: {RunIds}.",
            _runsDirectory ?? CurrentWorkingDirectory,
            orderedRuns.Length,
            string.Join(", ", orderedRuns.Select(run => run.RunId)));

        return orderedRuns;
    }

    internal IDisposable Retain()
    {
        lock (_runStateLock)
        {
            ObjectDisposedException.ThrowIf(_disposeRequested, this);
            _references++;
            return new Reference(this);
        }
    }

    public void Dispose()
    {
        lock (_runStateLock)
        {
            if (_disposeRequested)
            {
                return;
            }
            _disposeRequested = true;
        }
        Release();
    }

    private void Release()
    {
        lock (_runStateLock)
        {
            if (--_references != 0)
            {
                return;
            }
            DisposeCore();
        }
    }

    private void DisposeCore()
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
            else if (PersistenceMode == DashboardPersistenceMode.Run && Directory.Exists(CurrentWorkingDirectory))
            {
                // Run metadata is published only after the host starts listening. If startup fails first, remove
                // the initialized database so the attempted run never appears as empty historical data.
                _deleteRunDirectory(CurrentWorkingDirectory);
            }
        }
        finally
        {
            _runLock?.Dispose();
        }
    }

    private void WriteMetadata(DashboardRunMetadata metadata) => WriteMetadata(metadata, _metadataPath!);

    private static void WriteMetadata(DashboardRunMetadata metadata, string metadataPath)
    {
        // Write to a sibling temp file and rename over the target. Overwriting run.json in place means a
        // crash or power loss part-way through leaves a truncated file and the run becomes unreadable on
        // the next start. A rename within the same directory is atomic on both Windows and Unix.
        var temporaryPath = $"{metadataPath}.tmp";

        try
        {
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(metadata, s_jsonContext.DashboardRunMetadata));
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

    private void PruneRuns(IReadOnlyList<DashboardRunDescriptor> candidates, Action<string> deleteRunDirectory)
    {
        foreach (var candidate in candidates)
        {
            var directory = Path.GetDirectoryName(candidate.DatabasePath)!;
            FileLock? runLock;
            lock (_runStateLock)
            {
                // A rollover may have replaced the cached descriptors since candidates were selected.
                var run = GetRunById(candidate.RunId, onlyCompatible: false);
                if (run is null || run.IsCurrent || run.IsPinned)
                {
                    continue;
                }

                runLock = TryOpenRunLock(directory);
                // The per-run lock excludes leases and pins, including other dashboard processes.
                // Recheck persisted pin state after taking it, so a pin that won the race is preserved.
                if (runLock is not null && IsPinnedRunDirectory(directory))
                {
                    runLock.Dispose();
                    runLock = null;
                }
            }
            if (runLock is null)
            {
                continue;
            }

            using (runLock)
            {
                try
                {
                    // Recursive deletion can be slow. Never hold the global run-state lock here:
                    // ingestion, browser scopes, and another attachment all need that lock.
                    deleteRunDirectory(directory);
                    lock (_runStateLock)
                    {
                        candidate.IsPruned = true;
                        if (GetRunById(candidate.RunId, onlyCompatible: false) is { } currentDescriptor)
                        {
                            currentDescriptor.IsPruned = true;
                        }
                    }
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
    }

    private static bool IsPinnedRunDirectory(string runDirectory)
    {
        try
        {
            var metadataPath = Path.Combine(runDirectory, "run.json");
            return JsonSerializer.Deserialize(File.ReadAllText(metadataPath), s_jsonContext.DashboardRunMetadata)?.IsPinned == true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            // Run metadata is written locally by DashboardRunStore and is assumed to be reliable during normal usage.
            // Treat unreadable metadata as unpinned so incomplete or abandoned run directories can still be pruned.
            return false;
        }
    }

    private static FileLock OpenRunLock(string runDirectory)
    {
        // Keep the lock beside the run directory so pruning can hold it while recursively deleting the directory on Windows.
        return FileLock.Acquire(GetRunLockPath(runDirectory));
    }

    private static FileLock OpenRequiredRunLock(string runDirectory, string errorMessage)
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

    private static FileLock? TryOpenRunLock(string runDirectory)
    {
        var runLock = FileLock.TryAcquire(GetRunLockPath(runDirectory));
        if (runLock is null)
        {
            return null;
        }

        // The lock file is adjacent to the run directory, so OpenOrCreate can recreate it after pruning has already
        // deleted the directory. Check after acquiring the lock to avoid racing with a cooperating pruner.
        if (!Directory.Exists(runDirectory))
        {
            runLock.Dispose();
            return null;
        }

        return runLock;
    }

    internal static string GetRunLockPath(string runDirectory) => $"{runDirectory}.lock";

    private DashboardRunDescriptor CreateDescriptor(DashboardRunMetadata metadata, string runDirectory, bool isCurrent)
    {
        var descriptor = new DashboardRunDescriptor(
            metadata.RunId,
            metadata.SchemaVersion,
            metadata.StartedAtUtc,
            metadata.EndedAtUtc,
            metadata.CleanShutdown,
            // Older runs can contain an unset or blank name despite using the default application's disk key.
            DashboardOptions.GetApplicationNameOrDefault(metadata.ApplicationName),
            Path.Combine(runDirectory, metadata.DatabaseFileName),
            isCurrent)
        {
            IsPinned = metadata.IsPinned
        };
        if (_leasedRuns.TryGetValue(metadata.RunId, out var ownedLock))
        {
            descriptor.IsLeased = true;
            ownedLock.Descriptors.Add(descriptor);
        }

        return descriptor;
    }

    internal static string GetApplicationDirectory(string? dataRoot, string applicationName)
    {
        return Path.Combine(GetResumesDirectory(dataRoot), GetApplicationDirectoryName(applicationName));
    }

    internal static string GetRunsDirectory(string? dataRoot) => Path.Combine(GetDataRoot(dataRoot), "runs");

    internal static string GetResumesDirectory(string? dataRoot) => Path.Combine(GetDataRoot(dataRoot), "resumes");

    private static string GetDataRoot(string? dataRoot) => Path.GetFullPath(
        string.IsNullOrWhiteSpace(dataRoot)
            ? Path.Combine(AspireHomeDirectory.GetDefault(), "dashboard")
            : dataRoot);

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

    private sealed class OwnedRunLock(FileLock runLock)
    {
        public FileLock RunLock { get; } = runLock;
        public int ReferenceCount { get; set; }
        public HashSet<DashboardRunDescriptor> Descriptors { get; } = new(ReferenceEqualityComparer.Instance);
    }

    private sealed class RunLease(DashboardRunStore owner, string runId, OwnedRunLock ownedLock) : IDisposable
    {
        private OwnedRunLock? _ownedLock = ownedLock;

        public void Dispose()
        {
            lock (owner._runStateLock)
            {
                var ownedLock = Interlocked.Exchange(ref _ownedLock, null);
                if (ownedLock is not null && --ownedLock.ReferenceCount == 0)
                {
                    try
                    {
                        ownedLock.RunLock.Dispose();
                    }
                    finally
                    {
                        owner._leasedRuns.Remove(runId);
                        foreach (var descriptor in ownedLock.Descriptors)
                        {
                            descriptor.IsLeased = false;
                        }
                    }
                }
            }

            GC.SuppressFinalize(this);
        }
    }

    internal sealed record DashboardRunMetadata
    {
        public required int SchemaVersion { get; init; }
        public required string RunId { get; init; }
        public required DateTimeOffset StartedAtUtc { get; init; }
        public DateTimeOffset? EndedAtUtc { get; init; }
        public bool CleanShutdown { get; init; }
        public string? ApplicationName { get; init; }
        public required string DatabaseFileName { get; init; }
        public bool IsPinned { get; init; }
        [JsonExtensionData]
        public Dictionary<string, JsonElement>? ExtensionData { get; init; }
    }

    private sealed class Reference(DashboardRunStore owner) : IDisposable
    {
        private DashboardRunStore? _owner = owner;
        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.Release();
    }
}

/// <summary>
/// Describes a discovered dashboard run.
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
    internal bool IsCompatible => SchemaVersion == DashboardRunStore.SchemaVersion;

    /// <summary>
    /// Gets or sets a value indicating whether the dashboard run was pruned.
    /// </summary>
    public bool IsPruned { get; set; }

    internal bool IsSelectable { get; set; } = true;

    /// <summary>
    /// Gets a value indicating whether the dashboard run is pinned.
    /// </summary>
    public bool IsPinned { get; internal set; }

    internal bool IsLeased { get; set; }
}
