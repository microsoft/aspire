// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;
using System.Text.Json.Serialization;
using Aspire.Dogfooder.State;

namespace Aspire.Dogfooder.Services;

/// <summary>
/// JSON-file implementation of <see cref="ISessionStoreFile"/>.
/// Stores sessions at <c>~/.aspire/dogfooder/sessions.json</c> in a versioned
/// envelope so Phase 2+ additions (preparer scripts, scenario IDs, recording
/// paths) can extend the schema without breaking older installs.
/// </summary>
/// <remarks>
/// Writes go through a temp-file + atomic-rename dance: corruption from a
/// crash mid-write would orphan sessions, so we always write the complete
/// JSON to a sibling <c>.tmp</c> file and then <see cref="File.Move(string, string, bool)"/>
/// over the real one. On a non-existent target the load returns an empty
/// list (first-run case), not an error.
/// </remarks>
internal sealed class SessionStoreFile : ISessionStoreFile
{
    public SessionStoreFile(string? overridePath = null)
    {
        _path = overridePath ?? DefaultPath();
    }

    private readonly string _path;

    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static string DefaultPath()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(home, ".aspire", "dogfooder", "sessions.json");
    }

    public async Task<IReadOnlyList<DogfoodSession>> LoadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_path))
        {
            return Array.Empty<DogfoodSession>();
        }

        await using var stream = File.OpenRead(_path);
        var envelope = await JsonSerializer
            .DeserializeAsync<SessionFileEnvelope>(stream, s_jsonOptions, cancellationToken)
            .ConfigureAwait(false);

        if (envelope is null)
        {
            return Array.Empty<DogfoodSession>();
        }

        // Older versions are forward-compatible: unknown future fields are
        // simply ignored by the deserializer. If we ever ship a v2 with
        // breaking semantics we'll branch on envelope.Version here.
        return envelope.Sessions
            .Select(s => new DogfoodSession(s.Id, s.Name, new DogfoodSessionConfig(
                Channel: s.Channel,
                PrNumber: s.PrNumber,
                VersionOverride: s.VersionOverride,
                CommitOverride: s.CommitOverride,
                NuGetServiceIndexOverride: s.NuGetServiceIndexOverride)))
            .ToList();
    }

    public async Task SaveAsync(IReadOnlyList<DogfoodSession> sessions, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var envelope = new SessionFileEnvelope
        {
            Version = 1,
            Sessions = sessions.Select(s => new PersistedSession
            {
                Id = s.Id,
                Name = s.Name,
                Channel = s.Config.Channel,
                PrNumber = s.Config.PrNumber,
                VersionOverride = s.Config.VersionOverride,
                CommitOverride = s.Config.CommitOverride,
                NuGetServiceIndexOverride = s.Config.NuGetServiceIndexOverride,
            }).ToList(),
        };

        var tempPath = _path + ".tmp";
        await using (var stream = File.Create(tempPath))
        {
            await JsonSerializer
                .SerializeAsync(stream, envelope, s_jsonOptions, cancellationToken)
                .ConfigureAwait(false);
        }

        // File.Move overwrite=true is atomic on POSIX and best-effort on
        // Windows (rename-via-MOVEFILE_REPLACE_EXISTING); both are safer than
        // overwriting in place because a crash mid-Serialize would otherwise
        // leave a truncated sessions.json that fails to deserialize on next
        // launch.
        File.Move(tempPath, _path, overwrite: true);
    }

    private sealed class SessionFileEnvelope
    {
        public int Version { get; set; }
        public List<PersistedSession> Sessions { get; set; } = new();
    }

    private sealed class PersistedSession
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public ChannelKind Channel { get; set; }
        public int? PrNumber { get; set; }
        public string? VersionOverride { get; set; }
        public string? CommitOverride { get; set; }
        public string? NuGetServiceIndexOverride { get; set; }
    }
}
