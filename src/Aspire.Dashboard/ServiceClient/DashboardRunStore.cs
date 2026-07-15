// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.IO.Hashing;
using System.Text;
using System.Text.Json;
using Aspire.Dashboard.Configuration;
using Microsoft.Extensions.Options;

namespace Aspire.Dashboard.ServiceClient;

internal interface IDashboardRunStore
{
    bool SupportsRunSelection { get; }
    IReadOnlyList<DashboardRunDescriptor> GetRuns();
}

internal sealed class DashboardRunStore : IDashboardRunStore, IDisposable
{
    internal const int MaxApplicationDirectoryNameLength = 80;
    internal const int MaxRuns = 10;
    internal const int SchemaVersion = DashboardSqliteDatabase.SchemaVersion;

    private static readonly JsonSerializerOptions s_jsonOptions = new() { WriteIndented = true };

    private readonly string? _runsDirectory;
    private readonly string? _metadataPath;
    private readonly string? _temporaryDirectory;
    private readonly DashboardRunMetadata _metadata;
    private readonly ILogger<DashboardRunStore> _logger;

    public DashboardRunStore(IOptions<DashboardOptions> options, ILogger<DashboardRunStore> logger)
        : this(options, logger, static directory => Directory.Delete(directory, recursive: true))
    {
    }

    internal DashboardRunStore(
        IOptions<DashboardOptions> options,
        ILogger<DashboardRunStore> logger,
        Action<string> deleteRunDirectory)
    {
        _logger = logger;
        var applicationName = string.IsNullOrWhiteSpace(options.Value.ApplicationName) ? "Aspire" : options.Value.ApplicationName;
        var startedAt = DateTimeOffset.UtcNow;
        var runId = $"{startedAt:yyyyMMddTHHmmssfffZ}-{Guid.NewGuid():N}";
        PersistenceMode = options.Value.Data.PersistenceMode;

        // Persistent run directories should be located under a directory scoped to the current user. Rely on that
        // directory's inherited permissions instead of modifying the SQLite database, WAL, and shared-memory files individually.
        switch (PersistenceMode)
        {
            case DashboardPersistenceMode.None:
                _temporaryDirectory = Directory.CreateTempSubdirectory("aspire-dashboard-").FullName;
                RunDirectory = _temporaryDirectory;
                DatabasePath = Path.Combine(RunDirectory, "dashboard.db");
                break;
            case DashboardPersistenceMode.Runs:
                var applicationDirectory = GetApplicationDirectory(options.Value.Data.Directory, applicationName);
                _runsDirectory = Path.Combine(applicationDirectory, "runs");
                RunDirectory = Path.Combine(_runsDirectory, runId);
                Directory.CreateDirectory(RunDirectory);
                DatabasePath = Path.Combine(RunDirectory, "dashboard.db");
                _metadataPath = Path.Combine(RunDirectory, "run.json");
                break;
            case DashboardPersistenceMode.Append:
                RunDirectory = GetApplicationDirectory(options.Value.Data.Directory, applicationName);
                Directory.CreateDirectory(RunDirectory);
                DatabasePath = Path.Combine(RunDirectory, "dashboard.db");
                if (File.Exists(DatabasePath) && !DashboardSqliteDatabase.IsCompatible(DatabasePath))
                {
                    DeleteDatabaseFiles(DatabasePath);
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
        if (_metadataPath is not null)
        {
            WriteMetadata(_metadata);
            PruneRuns(deleteRunDirectory);
        }
    }

    public string RunDirectory { get; }
    public string DatabasePath { get; }
    public string RunId => _metadata.RunId;
    public DashboardPersistenceMode PersistenceMode { get; }
    public bool SupportsRunSelection => PersistenceMode == DashboardPersistenceMode.Runs;

    public IReadOnlyList<DashboardRunDescriptor> GetRuns()
    {
        var runs = new List<DashboardRunDescriptor>
        {
            CreateDescriptor(_metadata, RunDirectory, isCurrent: true)
        };

        if (!SupportsRunSelection || !Directory.Exists(_runsDirectory))
        {
            return runs;
        }

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
                    var descriptor = CreateDescriptor(metadata, directory, isCurrent: false);
                    if (DashboardSqliteDatabase.IsCompatible(descriptor.DatabasePath))
                    {
                        runs.Add(descriptor);
                    }
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
            {
                // Ignore incomplete or unreadable run metadata. A later dashboard process may still be writing it.
            }
        }

        return runs.OrderByDescending(run => run.IsCurrent).ThenByDescending(run => run.StartedAtUtc).ToList();
    }

    public void Dispose()
    {
        if (_metadataPath is not null)
        {
            WriteMetadata(_metadata with { EndedAtUtc = DateTimeOffset.UtcNow, CleanShutdown = true });
        }
        else if (_temporaryDirectory is not null && Directory.Exists(_temporaryDirectory))
        {
            Directory.Delete(_temporaryDirectory, recursive: true);
        }
    }

    private void WriteMetadata(DashboardRunMetadata metadata)
    {
        File.WriteAllText(_metadataPath!, JsonSerializer.Serialize(metadata, s_jsonOptions));
    }

    private void PruneRuns(Action<string> deleteRunDirectory)
    {
        // Run directory names start with a fixed-width UTC timestamp, so ordinal ordering matches creation order.
        var expiredRunDirectories = Directory.EnumerateDirectories(_runsDirectory!)
            .Where(directory => !string.Equals(directory, RunDirectory, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(Path.GetFileName, StringComparer.Ordinal)
            .Skip(MaxRuns - 1);

        foreach (var directory in expiredRunDirectories)
        {
            try
            {
                deleteRunDirectory(directory);
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

    private static DashboardRunDescriptor CreateDescriptor(DashboardRunMetadata metadata, string runDirectory, bool isCurrent)
    {
        return new DashboardRunDescriptor(
            metadata.RunId,
            metadata.StartedAtUtc,
            metadata.EndedAtUtc,
            metadata.CleanShutdown,
            metadata.ApplicationName,
            Path.Combine(runDirectory, metadata.DatabaseFileName),
            isCurrent);
    }

    private static string GetApplicationDirectory(string? dataRoot, string applicationName)
    {
        if (string.IsNullOrWhiteSpace(dataRoot))
        {
            dataRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Aspire", "Dashboard");
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

internal sealed record DashboardRunDescriptor(
    string RunId,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset? EndedAtUtc,
    bool CleanShutdown,
    string? ApplicationName,
    string DatabasePath,
    bool IsCurrent);