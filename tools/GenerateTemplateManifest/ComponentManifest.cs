// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using NuGet.Versioning;

namespace GenerateTemplateManifest;

internal sealed class ComponentManifest(IReadOnlySet<string> builtPackages)
{
    private readonly SortedDictionary<string, JsonObject> _registrations = new(StringComparer.Ordinal);

    internal int Count => _registrations.Count;

    internal void RegisterNuGetAssets(JsonElement assets, string source)
    {
        foreach (var library in assets.GetProperty("libraries").EnumerateObject())
        {
            if (library.Value.GetProperty("type").GetString() == "package")
            {
                // Assets library keys are "<package-id>/<resolved-version>", not version ranges.
                var separator = library.Name.LastIndexOf('/');
                if (separator <= 0 || separator == library.Name.Length - 1)
                {
                    throw new InvalidDataException($"Invalid NuGet library '{library.Name}' in {source}");
                }
                Register("nuget", library.Name[..separator], library.Name[(separator + 1)..], source);
            }
        }

        foreach (var framework in assets.GetProperty("project").GetProperty("frameworks").EnumerateObject())
        {
            if (!framework.Value.TryGetProperty("downloadDependencies", out var downloads))
            {
                continue;
            }
            foreach (var download in downloads.EnumerateArray())
            {
                // PackageDownload entries live outside "libraries", e.g.
                // "downloadDependencies": [{"name":"Example","version":"[1.2.3, 1.2.3]"}].
                // Multiple exact downloads use "[1.2.3, 1.2.3];[2.0.0, 2.0.0]".
                var name = download.GetProperty("name").GetString() ?? "";
                var version = download.GetProperty("version").GetString() ?? "";
                foreach (var entry in version.Split(';'))
                {
                    if (!VersionRange.TryParse(entry, out var range) ||
                        range.IsFloating || !range.IsMinInclusive || !range.IsMaxInclusive ||
                        range.MinVersion is null || range.MaxVersion is null || range.MinVersion != range.MaxVersion)
                    {
                        throw new InvalidDataException($"Unsupported PackageDownload version '{version}' for '{name}' in {source}; an exact version is required.");
                    }
                    Register("nuget", name, range.MinVersion.ToNormalizedString(), source);
                }
            }
        }
    }

    internal void Register(string type, string name, string version, string source)
    {
        if (string.IsNullOrWhiteSpace(name) || !NuGetVersion.TryParse(version, out var parsedVersion))
        {
            throw new InvalidDataException($"Invalid {type} package '{name}' version '{version}' in {source}");
        }
        if (type == "nuget")
        {
            name = name.ToLowerInvariant();
            version = parsedVersion.ToNormalizedString().ToLowerInvariant();
            if (builtPackages.Contains($"{name}/{version}"))
            {
                return;
            }
        }
        _registrations[$"{type}/{name}/{version}"] = new JsonObject
        {
            ["component"] = new JsonObject
            {
                ["type"] = type,
                [type] = new JsonObject { ["name"] = name, ["version"] = version }
            }
        };
    }

    internal string Serialize()
    {
        var entries = new JsonArray();
        foreach (var registration in _registrations.Values)
        {
            entries.Add(registration.DeepClone());
        }
        var manifest = new JsonObject
        {
            ["$schema"] = "https://json.schemastore.org/component-detection-manifest.json",
            ["version"] = 1,
            ["registrations"] = entries
        };
        return manifest.ToJsonString(new JsonSerializerOptions { WriteIndented = true }).ReplaceLineEndings("\n") + "\n";
    }

    internal static HashSet<string> ReadBuiltPackages(string directory)
    {
        if (!Directory.Exists(directory))
        {
            throw new DirectoryNotFoundException($"Shipping packages not found: {directory}. Build with -pack before generating the template manifest.");
        }
        var packages = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in Directory.EnumerateFiles(directory, "*.nupkg"))
        {
            using var archive = ZipFile.OpenRead(path);
            var nuspec = archive.Entries.Single(entry => entry.FullName.EndsWith(".nuspec", StringComparison.OrdinalIgnoreCase));
            using var stream = nuspec.Open();
            var document = XDocument.Load(stream);
            var ns = document.Root!.Name.Namespace;
            var metadata = document.Root.Element(ns + "metadata")!;
            var id = metadata.Element(ns + "id")!.Value;
            var version = NuGetVersion.Parse(metadata.Element(ns + "version")!.Value).ToNormalizedString();
            packages.Add($"{id}/{version}");
        }
        if (packages.Count == 0)
        {
            throw new InvalidDataException($"No shipping NuGet packages found in {directory}.");
        }
        return packages;
    }

}
