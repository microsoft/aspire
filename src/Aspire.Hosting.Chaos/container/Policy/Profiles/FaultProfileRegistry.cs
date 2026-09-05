// <copyright file="FaultProfileRegistry.cs" company="Microsoft Corporation">
// Copyright (c) Microsoft Corporation. All rights reserved.
// </copyright>

using System.Reflection;
using System.Text.Json;

namespace ChaosProxy.Container.Policy.Profiles;

/// <summary>
/// Resolves <see cref="FaultProfile"/> instances by id. Loads every embedded
/// <c>*.json</c> resource under the <c>Policy/Profiles/</c> folder of the container
/// assembly at construction, so adding a profile is a data-only change (drop a JSON file
/// + mark it <c>EmbeddedResource</c>). The generic <c>service.http</c> profile ships in
/// the container; Azure profiles (<c>azure.cosmos</c>, …) are embedded here too so the
/// single container binary can sample them (the Azure companion package surfaces only the
/// authoring API, per D16 — the data stays in the one container).
/// </summary>
internal sealed class FaultProfileRegistry
{
    /// <summary>The fallback profile id used when a requested profile is unknown.</summary>
    public const string DefaultProfileId = "service.http";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly IReadOnlyDictionary<string, FaultProfile> profiles;

    private FaultProfileRegistry(IReadOnlyDictionary<string, FaultProfile> profiles)
    {
        this.profiles = profiles;
    }

    /// <summary>The set of profile ids known to this registry.</summary>
    public IReadOnlyCollection<string> Ids => (IReadOnlyCollection<string>)this.profiles.Keys;

    /// <summary>
    /// Builds a registry from the embedded profile resources of the container assembly.
    /// </summary>
    public static FaultProfileRegistry CreateDefault()
        => Create(typeof(FaultProfileRegistry).Assembly);

    /// <summary>
    /// Builds a registry from the embedded <c>*.json</c> profile resources of the given
    /// assembly (overload exists for tests that embed their own fixtures).
    /// </summary>
    public static FaultProfileRegistry Create(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);

        var map = new Dictionary<string, FaultProfile>(StringComparer.OrdinalIgnoreCase);
        foreach (var resourceName in assembly.GetManifestResourceNames())
        {
            if (!resourceName.Contains(".Profiles.", StringComparison.Ordinal) ||
                !resourceName.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            using var stream = assembly.GetManifestResourceStream(resourceName)
                ?? throw new InvalidOperationException($"Embedded profile resource '{resourceName}' could not be opened.");

            var profile = JsonSerializer.Deserialize<FaultProfile>(stream, JsonOptions)
                ?? throw new InvalidOperationException($"Embedded profile resource '{resourceName}' deserialized to null.");

            Validate(profile, resourceName);
            map[profile.Id] = profile;
        }

        return new FaultProfileRegistry(map);
    }

    /// <summary>Builds a registry from an explicit set of profiles (test seam).</summary>
    public static FaultProfileRegistry FromProfiles(params FaultProfile[] profiles)
    {
        ArgumentNullException.ThrowIfNull(profiles);
        var map = new Dictionary<string, FaultProfile>(StringComparer.OrdinalIgnoreCase);
        foreach (var profile in profiles)
        {
            Validate(profile, profile.Id);
            map[profile.Id] = profile;
        }

        return new FaultProfileRegistry(map);
    }

    /// <summary>Returns the profile with the given id, or null if unknown.</summary>
    public FaultProfile? TryGet(string id)
    {
        ArgumentNullException.ThrowIfNull(id);
        return this.profiles.TryGetValue(id, out var profile) ? profile : null;
    }

    /// <summary>
    /// Resolves a profile by id, falling back to <see cref="DefaultProfileId"/> when the
    /// id is unknown. Throws only if neither the requested profile nor the default exists
    /// (a packaging error).
    /// </summary>
    public FaultProfile Resolve(string? id)
    {
        if (!string.IsNullOrEmpty(id) && this.profiles.TryGetValue(id, out var profile))
        {
            return profile;
        }

        return this.profiles.TryGetValue(DefaultProfileId, out var fallback)
            ? fallback
            : throw new InvalidOperationException(
                $"Fault profile '{id}' is unknown and the default profile '{DefaultProfileId}' is not registered.");
    }

    private static void Validate(FaultProfile profile, string source)
    {
        if (string.IsNullOrWhiteSpace(profile.Id))
        {
            throw new InvalidOperationException($"Fault profile from '{source}' has no id.");
        }

        if (profile.Entries is null || profile.Entries.Count == 0)
        {
            throw new InvalidOperationException($"Fault profile '{profile.Id}' has no entries.");
        }

        foreach (var entry in profile.Entries)
        {
            if (entry.Weight <= 0)
            {
                throw new InvalidOperationException($"Fault profile '{profile.Id}' has an entry with non-positive weight.");
            }

            if (string.IsNullOrWhiteSpace(entry.Kind))
            {
                throw new InvalidOperationException($"Fault profile '{profile.Id}' has an entry with no kind.");
            }
        }
    }
}
