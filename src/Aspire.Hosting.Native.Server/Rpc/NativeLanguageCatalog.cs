// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Aspire.Hosting.Native.Rpc;

/// <summary>Serves build-time language tooling metadata without loading providers into the native runtime.</summary>
internal sealed class NativeLanguageCatalog
{
    private readonly NativeLanguageRegistration[] _languages;
    private readonly Func<string, JsonObject> _readSdk;

    internal NativeLanguageCatalog(NativeLanguageRegistration[] languages, Func<string, JsonObject> readSdk)
    {
        _languages = languages.ToArray();
        _readSdk = readSdk;
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var language in _languages)
        {
            if (language.RuntimeSpec.ValueKind != JsonValueKind.Object ||
                !language.RuntimeSpec.TryGetProperty("language", out var id) ||
                !language.RuntimeSpec.TryGetProperty("codeGenLanguage", out var codeGenLanguage) ||
                id.ValueKind != JsonValueKind.String || codeGenLanguage.ValueKind != JsonValueKind.String ||
                string.IsNullOrWhiteSpace(id.GetString()) || !ids.Add(id.GetString()!) ||
                string.IsNullOrWhiteSpace(codeGenLanguage.GetString()) ||
                string.IsNullOrWhiteSpace(language.SdkResourcePrefix))
            {
                throw new InvalidDataException("Language registrations must have unique IDs and a code generation target.");
            }
        }
    }

    public static NativeLanguageCatalog LoadEmbedded()
    {
        var assembly = typeof(NativeLanguageCatalog).Assembly;
        using var stream = assembly.GetManifestResourceStream("NativeLanguages.json")
            ?? throw new InvalidOperationException("The executable is missing its language catalog.");
        var languages = JsonSerializer.Deserialize(stream, NativeLanguageJsonContext.Default.NativeLanguageRegistrationArray)
            ?? throw new InvalidDataException("Invalid native language catalog.");

        return new NativeLanguageCatalog(languages, prefix => ReadSdk(assembly, prefix));
    }

    public JsonObject RuntimeSpec(string language)
    {
        var registration = _languages.SingleOrDefault(entry =>
            string.Equals(entry.RuntimeSpec.GetProperty("language").GetString(), language, StringComparison.OrdinalIgnoreCase))
            ?? throw new NotSupportedException("The requested guest runtime is not included in this server build.");

        // RuntimeSpec is opaque provider-owned metadata. Forward its exact shape
        // rather than duplicating the evolving language contract in the kernel.
        return JsonNode.Parse(registration.RuntimeSpec.GetRawText())!.AsObject();
    }

    public JsonObject GeneratedSdk(string language)
    {
        var registrations = _languages.Where(entry =>
            string.Equals(entry.RuntimeSpec.GetProperty("language").GetString(), language, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(entry.RuntimeSpec.GetProperty("codeGenLanguage").GetString(), language, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var prefixes = registrations.Select(entry => entry.SdkResourcePrefix)
            .Distinct(StringComparer.Ordinal).ToArray();
        if (prefixes.Length != 1)
        {
            throw new NotSupportedException("The requested SDK is absent or ambiguous in this server build.");
        }

        return _readSdk(prefixes[0]).DeepClone().AsObject();
    }

    private static JsonObject ReadSdk(Assembly assembly, string prefix)
    {
        var files = new JsonObject();
        foreach (var name in assembly.GetManifestResourceNames().Where(name => name.StartsWith(prefix, StringComparison.Ordinal)))
        {
            using var reader = new StreamReader(assembly.GetManifestResourceStream(name)!);
            files[name[prefix.Length..]] = reader.ReadToEnd();
        }
        if (files.Count == 0)
        {
            throw new InvalidOperationException("The native executable is missing its generated SDK.");
        }

        return files;
    }
}

internal sealed record NativeLanguageRegistration(JsonElement RuntimeSpec, string SdkResourcePrefix);

[JsonSerializable(typeof(NativeLanguageRegistration[]))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, RespectNullableAnnotations = true)]
internal sealed partial class NativeLanguageJsonContext : JsonSerializerContext;
