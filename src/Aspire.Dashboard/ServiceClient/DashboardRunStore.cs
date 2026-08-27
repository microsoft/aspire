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
    /// Gets the current dashboard run.
    /// </summary>
    /// <returns>The current dashboard run.</returns>
    DashboardRunDescriptor GetCurrentRun();

    /// <summary>
    /// Gets the dashboard run with the specified ID.
    /// </summary>
    /// <param name="runId">The ID of the dashboard run.</param>
    /// <returns>The dashboard run, or <see langword="null"/> when the run is not available.</returns>
    DashboardRunDescriptor? GetRunById(string runId);

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
    /// Deletes dashboard runs beyond the retention limit.
    /// </summary>
    void PruneExpiredRuns();
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
    private readonly FileLock? _runLock;
    private DashboardRunMetadata _metadata;
    private readonly ILogger<DashboardRunStore> _logger;
    private readonly TimeProvider _timeProvider;
    private readonly Action<string> _deleteRunDirectory;
    private readonly Lazy<IReadOnlyList<DashboardRunDescriptor>> _runs;
    private readonly object _runStateLock = new();
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

    public DashboardRunDescriptor GetCurrentRun() => GetRuns().Single(run => run.IsCurrent);

    public DashboardRunDescriptor? GetRunById(string runId) =>
        GetRuns().SingleOrDefault(run => string.Equals(run.RunId, runId, StringComparison.Ordinal));

    public void SetRunPinned(DashboardRunDescriptor run, bool isPinned)
    {
        var storedRun = GetRunById(run.RunId);
        if (storedRun is null)
        {
            throw new InvalidOperationException($"Dashboard run '{run.RunId}' is no longer available.");
        }

        var runDirectory = Path.GetDirectoryName(storedRun.DatabasePath)!;
        lock (_runStateLock)
        {
            // The current run has the store's lifetime lock, and a selected historical run has a lease.
            // Only an unselected historical run needs a temporary lock while its metadata is updated.
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
        var metadata = JsonSerializer.Deserialize<DashboardRunMetadata>(File.ReadAllText(metadataPath));
        if (metadata is not { SchemaVersion: SchemaVersion } ||
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
    /// Kept separate from <see cref="PublishRun"/> because pruning walks every run directory, takes a cross-process
    /// lock on each, and recursively deletes it. On a slow or contended file system it can take a long time, so
    /// pruning is background housekeeping and must not hold up the dashboard accepting requests.
    /// </remarks>
    public void PruneExpiredRuns()
    {
        if (!_metadataPublished || _runsDirectory is null || !Directory.Exists(_runsDirectory))
        {
            return;
        }

        PruneRuns(_deleteRunDirectory);
    }

    public IDisposable? TryAcquireRunLease(DashboardRunDescriptor run)
    {
        var storedRun = GetRunById(run.RunId);
        if (storedRun is null)
        {
            return null;
        }

        var runDirectory = Path.GetDirectoryName(storedRun.DatabasePath)!;
        lock (_runStateLock)
        {
            var runLock = TryOpenRunLock(runDirectory);
            if (runLock is null)
            {
                return null;
            }

            storedRun.IsLeased = true;
            return new RunLease(this, storedRun, runLock);
        }
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
                        using var runLock = TryOpenRunLock(directory);
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
            .ThenByDescending(run => run.IsPinned)
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
            else if (PersistenceMode == DashboardPersistenceMode.Run && Directory.Exists(RunDirectory))
            {
                // Run metadata is published only after the host starts listening. If startup fails first, remove
                // the initialized database so the attempted run never appears as empty historical data.
                _deleteRunDirectory(RunDirectory);
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
        foreach (var run in _runs.Value.Where(run => !run.IsPinned).Skip(MaxRuns))
        {
            var directory = Path.GetDirectoryName(run.DatabasePath)!;
            using var runLock = TryOpenRunLock(directory);
            // Pinning can happen after the candidate list is created. Recheck while holding the same lock used by
            // SetRunPinned so a successful pin always completes before pruning decides whether to delete the run.
            if (runLock is null || IsPinnedRunDirectory(directory))
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

    private static bool IsPinnedRunDirectory(string runDirectory)
    {
        try
        {
            var metadataPath = Path.Combine(runDirectory, "run.json");
            return JsonSerializer.Deserialize<DashboardRunMetadata>(File.ReadAllText(metadataPath))?.IsPinned == true;
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
            isCurrent)
        {
            IsPinned = metadata.IsPinned
        };
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

    private sealed class RunLease(DashboardRunStore owner, DashboardRunDescriptor run, FileLock runLock) : IDisposable
    {
        private FileLock? _runLock = runLock;

        public void Dispose()
        {
            lock (owner._runStateLock)
            {
                var runLock = Interlocked.Exchange(ref _runLock, null);
                if (runLock is not null)
                {
                    try
                    {
                        runLock.Dispose();
                    }
                    finally
                    {
                        run.IsLeased = false;
                    }
                }
            }

            GC.SuppressFinalize(this);
        }
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
        public bool IsPinned { get; init; }
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

    /// <summary>
    /// Gets a value indicating whether the dashboard run is pinned.
    /// </summary>
    public bool IsPinned { get; internal set; }

    internal bool IsLeased { get; set; }
}
