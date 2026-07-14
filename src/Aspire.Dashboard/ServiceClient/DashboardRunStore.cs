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
    IReadOnlyList<DashboardRunDescriptor> GetRuns();
}

internal sealed class DashboardRunStore : IDashboardRunStore, IDisposable
{
    internal const int MaxApplicationDirectoryNameLength = 80;
    internal const int SchemaVersion = DashboardSqliteDatabase.SchemaVersion;

    private static readonly JsonSerializerOptions s_jsonOptions = new() { WriteIndented = true };

    private readonly string _runsDirectory;
    private readonly string _metadataPath;
    private readonly DashboardRunMetadata _metadata;

    public DashboardRunStore(IOptions<DashboardOptions> options)
    {
        var dataRoot = options.Value.Data.Directory;
        if (string.IsNullOrWhiteSpace(dataRoot))
        {
            dataRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Aspire", "Dashboard");
        }

        var applicationName = string.IsNullOrWhiteSpace(options.Value.ApplicationName) ? "Aspire" : options.Value.ApplicationName;
        var applicationDirectoryName = GetApplicationDirectoryName(applicationName);
        var startedAt = DateTimeOffset.UtcNow;
        var runId = $"{startedAt:yyyyMMddTHHmmssfffZ}-{Guid.NewGuid():N}";
        _runsDirectory = Path.Combine(Path.GetFullPath(dataRoot), applicationDirectoryName, "runs");
        RunDirectory = Path.Combine(_runsDirectory, runId);
        Directory.CreateDirectory(RunDirectory);
        DatabasePath = Path.Combine(RunDirectory, "dashboard.db");
        _metadataPath = Path.Combine(RunDirectory, "run.json");
        _metadata = new DashboardRunMetadata
        {
            SchemaVersion = SchemaVersion,
            RunId = runId,
            StartedAtUtc = startedAt,
            ApplicationName = options.Value.ApplicationName,
            DatabaseFileName = Path.GetFileName(DatabasePath)
        };
        WriteMetadata(_metadata);
    }

    public string RunDirectory { get; }
    public string DatabasePath { get; }
    public string RunId => _metadata.RunId;

    public IReadOnlyList<DashboardRunDescriptor> GetRuns()
    {
        var runs = new List<DashboardRunDescriptor>
        {
            CreateDescriptor(_metadata, RunDirectory, isCurrent: true)
        };

        if (!Directory.Exists(_runsDirectory))
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
        WriteMetadata(_metadata with { EndedAtUtc = DateTimeOffset.UtcNow, CleanShutdown = true });
    }

    private void WriteMetadata(DashboardRunMetadata metadata)
    {
        File.WriteAllText(_metadataPath, JsonSerializer.Serialize(metadata, s_jsonOptions));
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