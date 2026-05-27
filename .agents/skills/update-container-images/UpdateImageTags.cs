#!/usr/bin/env dotnet run
// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// This script discovers all *ImageTags.cs files in the repository, parses them
// to extract (registry, image, tag) tuples, queries each registry for available
// tags, and outputs a JSON report for LLM-driven version analysis.
//
// Output conventions:
//   stdout — structured JSON report (redirect with 1>report.json)
//   stderr — progress messages and diagnostics (redirect with 2>progress.txt)

using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;

var repoRoot = FindRepoRoot(Directory.GetCurrentDirectory());
if (repoRoot is null)
{
    Console.Error.WriteLine("Error: Could not find repository root (no .git directory found).");
    return 1;
}

Console.Error.WriteLine($"Repository root: {repoRoot}");

// Phase 1: Parse all *ImageTags.cs files
var imageTagFiles = Directory.GetFiles(Path.Combine(repoRoot, "src"), "*ImageTags.cs", SearchOption.AllDirectories);
Console.Error.WriteLine($"Found {imageTagFiles.Length} image tag files.");

var allImageEntries = new List<ImageFileReport>();

foreach (var file in imageTagFiles.OrderBy(f => f))
{
    var relativePath = Path.GetRelativePath(repoRoot, file);
    var content = File.ReadAllText(file);
    var entries = ParseImageTagsFile(content, relativePath);
    if (entries.Count > 0)
    {
        allImageEntries.Add(new ImageFileReport { File = relativePath, Entries = entries });
    }
}

Console.Error.WriteLine($"Parsed {allImageEntries.Sum(f => f.Entries.Count)} image entries across {allImageEntries.Count} files.");

// Phase 2: Query registries for available tags
using var httpClient = new HttpClient();
httpClient.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("AspireImageTagUpdater", "1.0"));

var uniqueImages = allImageEntries
    .SelectMany(f => f.Entries)
    .Where(e => e.Skipped != true)
    .GroupBy(e => (e.Registry, e.Image))
    .Select(g => (g.Key.Registry, g.Key.Image, CurrentTags: g.Select(e => e.CurrentTag).Distinct().ToList()))
    .ToList();

Console.Error.WriteLine($"Querying {uniqueImages.Count} unique images across registries...");

var tagCache = new Dictionary<(string Registry, string Image), List<string>>();

foreach (var (registry, image, currentTags) in uniqueImages)
{
    Console.Error.Write($"  {registry}/{image} ... ");
    try
    {
        var tags = await FetchTags(httpClient, registry, image, currentTags.FirstOrDefault());
        tagCache[(registry, image)] = tags;
        Console.Error.WriteLine($"{tags.Count} tags");
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"FAILED: {ex.Message}");
        tagCache[(registry, image)] = [];
    }
}

// Phase 3: Build and output the report
foreach (var fileReport in allImageEntries)
{
    foreach (var entry in fileReport.Entries)
    {
        if (entry.Skipped != true && tagCache.TryGetValue((entry.Registry, entry.Image), out var tags))
        {
            entry.AvailableTags = tags;
        }
    }
}

// Write report as JSON using Utf8JsonWriter (AOT-compatible)
using var stream = Console.OpenStandardOutput();
using var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true });

writer.WriteStartObject();
writer.WriteStartArray("images");

foreach (var fileReport in allImageEntries)
{
    writer.WriteStartObject();
    writer.WriteString("file", fileReport.File);
    writer.WriteStartArray("entries");

    foreach (var entry in fileReport.Entries)
    {
        writer.WriteStartObject();
        if (entry.FieldPrefix is not null)
        {
            writer.WriteString("fieldPrefix", entry.FieldPrefix);
        }
        writer.WriteString("registry", entry.Registry);
        writer.WriteString("image", entry.Image);
        writer.WriteString("currentTag", entry.CurrentTag);
        if (entry.IsDerived == true)
        {
            writer.WriteBoolean("isDerived", true);
        }
        if (entry.Skipped == true)
        {
            writer.WriteBoolean("skipped", true);
        }
        if (entry.SkipReason is not null)
        {
            writer.WriteString("skipReason", entry.SkipReason);
        }
        if (entry.AvailableTags is not null)
        {
            writer.WriteStartArray("availableTags");
            foreach (var t in entry.AvailableTags)
            {
                writer.WriteStringValue(t);
            }
            writer.WriteEndArray();
        }
        writer.WriteEndObject();
    }

    writer.WriteEndArray();
    writer.WriteEndObject();
}

writer.WriteEndArray();
writer.WriteEndObject();
writer.Flush();

Console.Error.WriteLine();
Console.Error.WriteLine("Done.");
return 0;

// ─── Helper Methods ──────────────────────────────────────────────────────────

static string? FindRepoRoot(string startDir)
{
    var dir = startDir;
    while (dir is not null)
    {
        // .git can be a directory (normal repo) or a file (worktree)
        if (Directory.Exists(Path.Combine(dir, ".git")) || File.Exists(Path.Combine(dir, ".git")))
        {
            return dir;
        }
        dir = Directory.GetParent(dir)?.FullName;
    }
    return null;
}

static List<ImageEntry> ParseImageTagsFile(string content, string relativePath)
{
    // Extract all const/static string field assignments
    var fieldPattern = new Regex(
        @"public\s+(?:const|static)\s+string\s+(\w+)\s*(?:{\s*get;\s*}\s*)?=\s*(.+?);",
        RegexOptions.Multiline);

    var fields = new Dictionary<string, string>();
    var derivedFields = new HashSet<string>();

    foreach (Match match in fieldPattern.Matches(content))
    {
        var fieldName = match.Groups[1].Value;
        var rawValue = match.Groups[2].Value.Trim();

        // Check if this is a derived/interpolated value
        if (rawValue.StartsWith("$\"") || rawValue.Contains('{'))
        {
            derivedFields.Add(fieldName);
            fields[fieldName] = rawValue;
        }
        else
        {
            // Extract the string literal value
            var literalMatch = Regex.Match(rawValue, @"""([^""]+)""");
            if (literalMatch.Success)
            {
                fields[fieldName] = literalMatch.Groups[1].Value;
            }
        }
    }

    // Group fields by prefix. The primary image has fields named "Registry", "Image", "Tag".
    // Secondary images have prefixed names like "PgAdminRegistry", "PgAdminImage", "PgAdminTag".
    var entries = new List<ImageEntry>();

    // Find all Tag fields to determine prefixes
    var tagFields = fields.Keys.Where(k => k.EndsWith("Tag")).ToList();

    foreach (var tagField in tagFields)
    {
        string prefix;
        if (tagField == "Tag")
        {
            prefix = "";
        }
        else
        {
            prefix = tagField[..^3]; // Remove "Tag" suffix
        }

        var registryField = prefix + "Registry";
        var imageField = prefix + "Image";

        // For the primary image, Registry/Image are always named "Registry"/"Image"
        // For secondary images, they might use prefixed names
        var registry = fields.GetValueOrDefault(registryField) ?? fields.GetValueOrDefault("Registry");
        var image = fields.GetValueOrDefault(imageField);
        var tag = fields.GetValueOrDefault(tagField);

        if (image is null)
        {
            continue;
        }

        var isDerived = derivedFields.Contains(tagField);
        var isLatestOrUnversioned = !isDerived && (tag == "latest" || tag == "vnext-preview");

        var entry = new ImageEntry
        {
            FieldPrefix = prefix == "" ? null : prefix,
            Registry = registry ?? "docker.io",
            Image = image,
            CurrentTag = isDerived ? fields[tagField] : tag ?? "",
            IsDerived = isDerived ? true : null,
            Skipped = (isDerived || isLatestOrUnversioned) ? true : null,
            SkipReason = isDerived ? "Derived/computed tag"
                       : isLatestOrUnversioned ? $"Tag is '{tag}'"
                       : null
        };

        entries.Add(entry);
    }

    return entries;
}

static async Task<List<string>> FetchTags(HttpClient httpClient, string registry, string image, string? currentTag)
{
    return registry switch
    {
        "docker.io" => await FetchDockerHubTags(httpClient, image, currentTag),
        "mcr.microsoft.com" => await FetchOciTags(httpClient, $"https://mcr.microsoft.com/v2/{image}/tags/list", authUrl: null),
        "ghcr.io" => await FetchOciTags(httpClient, $"https://ghcr.io/v2/{image}/tags/list",
            authUrl: $"https://ghcr.io/token?service=ghcr.io&scope=repository:{image}:pull"),
        "container-registry.oracle.com" => await FetchOciTags(httpClient, $"https://container-registry.oracle.com/v2/{image}/tags/list",
            authUrl: $"https://container-registry.oracle.com/auth?service=Oracle%20Registry&scope=repository:{image}:pull"),
        "quay.io" => await FetchOciTags(httpClient, $"https://quay.io/v2/{image}/tags/list", authUrl: null),
        _ => throw new NotSupportedException($"Unsupported registry: {registry}")
    };
}

static async Task<List<string>> FetchDockerHubTags(HttpClient httpClient, string image, string? currentTag)
{
    var allTags = new HashSet<string>();

    // Fetch the most recent 50 tags by last_updated (gives a broad baseline)
    var baseUrl = $"https://hub.docker.com/v2/repositories/{image}/tags?page_size=50&ordering=-last_updated";
    await FetchDockerHubPage(httpClient, baseUrl, allTags);

    // For version-like current tags, also query with prefix filters to ensure we find
    // newer versions. Docker Hub's last_updated ordering often surfaces old patched releases
    // instead of the highest version numbers.
    if (currentTag is not null)
    {
        foreach (var prefix in GetVersionPrefixes(currentTag))
        {
            var prefixUrl = $"https://hub.docker.com/v2/repositories/{image}/tags?page_size=100&name={prefix}";
            await FetchDockerHubPage(httpClient, prefixUrl, allTags);
        }
    }

    return allTags.ToList();
}

static List<string> GetVersionPrefixes(string currentTag)
{
    var prefixes = new List<string>();

    // Strip leading 'v' for numeric parsing
    var stripped = currentTag.TrimStart('v');
    var vPrefix = currentTag.StartsWith('v') ? "v" : "";

    // Try to parse the major version number
    var dotIndex = stripped.IndexOf('.');
    var dashIndex = stripped.IndexOf('-');
    var majorStr = dotIndex > 0 ? stripped[..dotIndex]
                 : dashIndex > 0 ? stripped[..dashIndex]
                 : stripped;

    if (int.TryParse(majorStr, out var major))
    {
        // Query for current major and next two majors to catch new releases
        for (var m = major; m <= major + 2; m++)
        {
            prefixes.Add($"{vPrefix}{m}.");
        }
    }

    return prefixes;
}

static async Task FetchDockerHubPage(HttpClient httpClient, string url, HashSet<string> tags)
{
    try
    {
        var response = await httpClient.GetAsync(url);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);

        if (doc.RootElement.TryGetProperty("results", out var results))
        {
            foreach (var result in results.EnumerateArray())
            {
                if (result.TryGetProperty("name", out var name))
                {
                    tags.Add(name.GetString()!);
                }
            }
        }
    }
    catch
    {
        // Ignore failures on supplementary prefix queries
    }
}

static async Task<List<string>> FetchOciTags(HttpClient httpClient, string url, string? authUrl)
{
    var request = new HttpRequestMessage(HttpMethod.Get, url);

    if (authUrl is not null)
    {
        // Fetch anonymous token
        var tokenResponse = await httpClient.GetAsync(authUrl);
        tokenResponse.EnsureSuccessStatusCode();
        var tokenJson = await tokenResponse.Content.ReadAsStringAsync();
        using var tokenDoc = JsonDocument.Parse(tokenJson);
        var token = tokenDoc.RootElement.GetProperty("token").GetString();
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
    }

    var response = await httpClient.SendAsync(request);
    response.EnsureSuccessStatusCode();

    var json = await response.Content.ReadAsStringAsync();
    using var doc = JsonDocument.Parse(json);

    var tags = new List<string>();
    if (doc.RootElement.TryGetProperty("tags", out var tagsArray))
    {
        foreach (var tag in tagsArray.EnumerateArray())
        {
            tags.Add(tag.GetString()!);
        }
    }

    // Return last 50 tags (OCI API doesn't sort by date, but tends to be roughly chronological)
    return tags.TakeLast(50).ToList();
}

// ─── Models ──────────────────────────────────────────────────────────────────

class ImageFileReport
{
    public string File { get; set; } = "";
    public List<ImageEntry> Entries { get; set; } = [];
}

class ImageEntry
{
    public string? FieldPrefix { get; set; }
    public string Registry { get; set; } = "";
    public string Image { get; set; } = "";
    public string CurrentTag { get; set; } = "";
    public bool? IsDerived { get; set; }
    public bool? Skipped { get; set; }
    public string? SkipReason { get; set; }
    public List<string>? AvailableTags { get; set; }
}
