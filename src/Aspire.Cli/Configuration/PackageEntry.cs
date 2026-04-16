// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Aspire.Cli.Configuration;

/// <summary>
/// One entry in the <c>aspire.config.json</c> <c>packages</c> dictionary.
///
/// <para>Two wire shapes are accepted:</para>
///
/// <list type="bullet">
///   <item>
///     <b>Short form (string)</b> — always NuGet. An empty string means the SDK version; a
///     non-empty string is an explicit NuGet package version.
///     <code>
///     "Aspire.Hosting.Redis": "",         // NuGet, SDK version
///     "Aspire.Hosting.Kafka": "9.2.0"     // NuGet, explicit version
///     </code>
///   </item>
///   <item>
///     <b>Long form (object)</b> — requires a <c>source</c> discriminator. Each source has its
///     own required fields.
///     <code>
///     "Aspire.Hosting.Redis":  { "source": "nuget",   "version": "9.2.0" }
///     "Aspire.Hosting.Local":  { "source": "project", "path": "../Local/Local.csproj" }
///     "@spike/aspire-kafka":   { "source": "npm",     "path": "./kafka-integration/host.ts" }
///     </code>
///   </item>
/// </list>
///
/// On write-back, a NuGet entry is emitted as the short string form when possible. Project and
/// npm entries are always emitted as objects.
/// </summary>
[JsonConverter(typeof(PackageEntryConverter))]
internal sealed class PackageEntry
{
    /// <summary>
    /// The source ecosystem this entry came from.
    /// </summary>
    public required IntegrationSource Source { get; init; }

    /// <summary>
    /// NuGet package version. <c>null</c> means "use the SDK version" (short form <c>""</c>).
    /// Non-null only when <see cref="Source"/> is <see cref="IntegrationSource.Nuget"/>.
    /// </summary>
    public string? Version { get; init; }

    /// <summary>
    /// On-disk path associated with this entry:
    /// the <c>.csproj</c> path for <see cref="IntegrationSource.Project"/>, or the integration
    /// host entry point (e.g. <c>./kafka-integration/host.ts</c>) for <see cref="IntegrationSource.Npm"/>.
    /// Stored as it was written in the config — relative or absolute. Resolved to absolute in
    /// <see cref="AspireConfigFile.GetIntegrationReferences"/>.
    /// </summary>
    public string? Path { get; init; }

    /// <summary>
    /// Short-hand for the common case: create a NuGet entry with an explicit version.
    /// </summary>
    public static PackageEntry Nuget(string? version) => new()
    {
        Source = IntegrationSource.Nuget,
        Version = version
    };

    /// <summary>
    /// Short-hand: create a local .NET project reference entry.
    /// </summary>
    public static PackageEntry Project(string path) => new()
    {
        Source = IntegrationSource.Project,
        Path = path
    };

    /// <summary>
    /// Short-hand: create an npm integration host entry.
    /// </summary>
    public static PackageEntry Npm(string path) => new()
    {
        Source = IntegrationSource.Npm,
        Path = path
    };
}

/// <summary>
/// Reads the string-or-object polymorphic shape for <see cref="PackageEntry"/>. A bare string
/// token is interpreted as a NuGet version (empty = SDK version). An object token requires a
/// <c>source</c> discriminator and per-source fields.
/// </summary>
internal sealed class PackageEntryConverter : JsonConverter<PackageEntry>
{
    public override PackageEntry Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
        {
            var versionString = reader.GetString();
            // Empty string means "use the SDK version" — stored as null so the caller can
            // substitute the effective SDK version at resolution time.
            return PackageEntry.Nuget(string.IsNullOrWhiteSpace(versionString) ? null : versionString);
        }

        if (reader.TokenType != JsonTokenType.StartObject)
        {
            throw new JsonException(
                $"Package entry must be a string (NuGet version short form) or an object with a \"source\" discriminator. Got token: {reader.TokenType}.");
        }

        string? source = null;
        string? version = null;
        string? path = null;

        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            if (reader.TokenType != JsonTokenType.PropertyName)
            {
                throw new JsonException($"Unexpected token in package entry: {reader.TokenType}.");
            }

            var propertyName = reader.GetString();
            reader.Read();

            switch (propertyName)
            {
                case "source":
                    source = reader.GetString();
                    break;
                case "version":
                    version = reader.GetString();
                    break;
                case "path":
                    path = reader.GetString();
                    break;
                default:
                    // Unknown fields are ignored — forward compatibility.
                    reader.Skip();
                    break;
            }
        }

        if (string.IsNullOrWhiteSpace(source))
        {
            throw new JsonException(
                "Package entry object is missing the required \"source\" discriminator. Expected one of: \"nuget\", \"project\", \"npm\".");
        }

        switch (source.ToLowerInvariant())
        {
            case "nuget":
                return PackageEntry.Nuget(string.IsNullOrWhiteSpace(version) ? null : version);

            case "project":
                if (string.IsNullOrWhiteSpace(path))
                {
                    throw new JsonException(
                        "Package entry with source \"project\" is missing the required \"path\" field (absolute or relative path to a .csproj).");
                }
                return PackageEntry.Project(path);

            case "npm":
                if (string.IsNullOrWhiteSpace(path))
                {
                    throw new JsonException(
                        "Package entry with source \"npm\" is missing the required \"path\" field (path to the integration host entry point, e.g. \"./kafka-integration/host.ts\").");
                }
                return PackageEntry.Npm(path);

            default:
                throw new JsonException(
                    $"Package entry has unknown source \"{source}\". Expected one of: \"nuget\", \"project\", \"npm\".");
        }
    }

    public override void Write(Utf8JsonWriter writer, PackageEntry value, JsonSerializerOptions options)
    {
        switch (value.Source)
        {
            case IntegrationSource.Nuget:
                // Round-trip to the short string form: empty means SDK version.
                writer.WriteStringValue(value.Version ?? string.Empty);
                break;

            case IntegrationSource.Project:
                writer.WriteStartObject();
                writer.WriteString("source", "project");
                writer.WriteString("path", value.Path ?? string.Empty);
                writer.WriteEndObject();
                break;

            case IntegrationSource.Npm:
                writer.WriteStartObject();
                writer.WriteString("source", "npm");
                writer.WriteString("path", value.Path ?? string.Empty);
                writer.WriteEndObject();
                break;

            default:
                throw new JsonException($"Unknown package entry source: {value.Source}.");
        }
    }
}
