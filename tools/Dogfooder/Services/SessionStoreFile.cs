// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;
using System.Text.Json.Serialization;
using Aspire.Dogfooder.State;

namespace Aspire.Dogfooder.Services;

/// <summary>
/// JSON-file implementation of <see cref="ISessionStoreFile"/>.
/// Stores sessions at <c>~/.aspire/dogfooder/sessions.json</c> in a versioned
/// envelope so future additions (scenario inputs, recording paths, etc.) can
/// extend the schema without breaking older installs.
/// </summary>
/// <remarks>
/// Schema evolution:
/// <list type="bullet">
///   <item>v1: original Channel/PrNumber/Version/Commit/NuGet bag.</item>
///   <item>v2: added Build/Suffix/PackageSourceDir/UseProxy/IncludeNative knobs.</item>
///   <item>v3: collapsed to (ScenarioId, Inputs); per-scenario plan is recomputed at launch
///       from the registry so we don't persist denormalised plan fields.</item>
/// </list>
/// v1/v2 records load as the <c>repro-vcurrent-published</c> scenario with no
/// inputs — the closest no-op equivalent (no rebuild, no proxy) so existing
/// sessions stay openable rather than vanishing on upgrade.
///
/// Writes go through a temp-file + atomic-rename dance: corruption from a
/// crash mid-write would orphan sessions, so we always write the complete
/// JSON to a sibling <c>.tmp</c> file and then <see cref="File.Move(string, string, bool)"/>
/// over the real one.
/// </remarks>
internal sealed class SessionStoreFile : ISessionStoreFile
{
    public SessionStoreFile(string? overridePath = null)
    {
        _path = overridePath ?? DefaultPath();
    }

    private readonly string _path;

    // The scenario id used as the fallback when loading a v1/v2 envelope or a
    // v3 record whose ScenarioId is unknown. Keep in sync with the catalog.
    private const string FallbackScenarioId = "repro-vcurrent-published";

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

        return envelope.Sessions
            .Select(s =>
            {
                // v3 stores ScenarioId + Inputs. v1/v2 records have neither,
                // and we deliberately discard the old denormalised fields:
                // the corresponding scenarios in the new catalog don't map
                // 1:1 (e.g. "Channel=Stable + Version=13.5.0" could be
                // either repro-vcurrent or vnext-minor). Falling back to a
                // safe no-op is more honest than guessing.
                var scenarioId = string.IsNullOrEmpty(s.ScenarioId)
                    ? FallbackScenarioId
                    : s.ScenarioId;
                var inputs = s.Inputs is { Count: > 0 }
                    ? new Dictionary<string, string?>(s.Inputs, StringComparer.Ordinal)
                    : new Dictionary<string, string?>(StringComparer.Ordinal);
                return new DogfoodSession(s.Id, s.Name, new DogfoodSessionConfig(scenarioId, inputs));
            })
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
            Version = 3,
            Sessions = sessions.Select(s => new PersistedSession
            {
                Id = s.Id,
                Name = s.Name,
                ScenarioId = s.Config.ScenarioId,
                Inputs = s.Config.Inputs.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal),
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
        // Windows (rename-via-MOVEFILE_REPLACE_EXISTING); both are safer
        // than overwriting in place because a crash mid-Serialize would
        // otherwise leave a truncated sessions.json that fails to
        // deserialize on next launch.
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

        // v3 fields.
        public string? ScenarioId { get; set; }
        public Dictionary<string, string?>? Inputs { get; set; }
    }
}
