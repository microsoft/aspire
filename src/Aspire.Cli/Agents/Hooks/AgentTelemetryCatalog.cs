// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Formats.Tar;
using System.IO.Compression;
using System.Text.Json;
using System.Text.RegularExpressions;
using Aspire.Cli.Agents.AspireSkills;

namespace Aspire.Cli.Agents.Hooks;

/// <summary>
/// Reads skills and references from the embedded skill manifest, and MCP tools from the canonical hook.
/// </summary>
internal sealed partial class AgentTelemetryCatalog
{
    private static readonly Lazy<AgentTelemetryCatalog> s_bundled = new(LoadBundled);

    internal static AgentTelemetryCatalog Bundled => s_bundled.Value;

    internal IReadOnlySet<string> Skills { get; }
    internal IReadOnlySet<string> Tools { get; }
    internal IReadOnlySet<string> References { get; }

    private AgentTelemetryCatalog(HashSet<string> skills, HashSet<string> tools, HashSet<string> references)
    {
        Skills = skills;
        Tools = tools;
        References = references;
    }

    internal static AgentTelemetryCatalog Parse(SkillBundleManifest manifest, string script)
    {
        if (manifest.Skills is not { Length: > 0 })
        {
            throw new InvalidDataException("The bundled skill manifest must contain skills.");
        }

        var skills = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var references = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var skill in manifest.Skills)
        {
            if (skill?.Name is not { Length: > 0 } name)
            {
                throw new InvalidDataException("The bundled skill manifest contains an unnamed skill.");
            }

            AspireSkillsBundleProvider.ValidateSkillName(name);
            if (!skills.Add(name))
            {
                throw new InvalidDataException($"The bundled skill manifest contains duplicate skill '{name}'.");
            }

            if (skill.Files is not { } files)
            {
                throw new InvalidDataException($"The bundled skill '{name}' is missing its file inventory.");
            }
            foreach (var file in files)
            {
                var path = AspireSkillsBundleProvider.NormalizeRelativePath(file?.RelativePath).Replace('\\', '/');
                // SKILL.md is an invocation, not a reference. Evals, scripts, and other manifest
                // assets must not become new telemetry dimensions merely because they are shipped.
                if (path.StartsWith("references/", StringComparison.Ordinal))
                {
                    references.Add($"{name}/{path}");
                }
            }
        }

        // The manifest does not yet describe MCP tools. Retain the canonical tool allowlist until
        // structured tool metadata is available, without parsing shell declarations for skills.
        var declarations = McpToolsDeclaration().Matches(script);
        if (declarations.Count != 1)
        {
            throw new InvalidDataException("The bundled telemetry hook must declare ASPIRE_MCP_TOOLS exactly once.");
        }
        var tools = declarations[0].Groups["values"].Value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (tools.Length == 0 || tools.Any(tool => !ToolIdentifier().IsMatch(tool)))
        {
            throw new InvalidDataException("The bundled telemetry hook contains an invalid MCP tool allowlist.");
        }

        return new(skills, new HashSet<string>(tools, StringComparer.OrdinalIgnoreCase), references);
    }

    private static AgentTelemetryCatalog LoadBundled()
    {
        // Read only compiled resources, never user-installed skills or scripts. No extraction,
        // network lookup, or skill-content reads are needed on the hook path.
        using var archive = typeof(AgentTelemetryCatalog).Assembly.GetManifestResourceStream("aspire-skills.bundle.tgz")
            ?? throw new InvalidDataException("The bundled skills archive is missing.");
        var manifest = ReadManifest(archive);
        using var stream = typeof(AgentTelemetryCatalog).Assembly.GetManifestResourceStream("track-telemetry.sh")
            ?? throw new InvalidDataException("The bundled telemetry hook is missing.");
        using var reader = new StreamReader(stream);
        return Parse(manifest, reader.ReadToEnd());
    }

    internal static SkillBundleManifest ReadManifest(Stream archive)
    {
        using var gzip = new GZipStream(archive, CompressionMode.Decompress, leaveOpen: true);
        using var tar = new TarReader(gzip);
        SkillBundleManifest? manifest = null;
        while (tar.GetNextEntry() is { } entry)
        {
            // The manifest is at the archive root or one wrapper directory below it:
            // skill-manifest.json or aspire-skills-v0.0.3/skill-manifest.json.
            var parts = entry.Name.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (parts is not (["skill-manifest.json"] or [_, "skill-manifest.json"]))
            {
                continue;
            }
            if (manifest is not null || entry.DataStream is null ||
                entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile))
            {
                throw new InvalidDataException("The bundled skills archive must contain one regular skill manifest.");
            }
            manifest = JsonSerializer.Deserialize(entry.DataStream, AspireSkillsJsonSerializerContext.Default.SkillBundleManifest)
                ?? throw new InvalidDataException("The bundled skill manifest is empty.");
        }

        return manifest ?? throw new InvalidDataException("The bundled skills archive is missing skill-manifest.json.");
    }

    // ASPIRE_MCP_TOOLS="doctor list_resources ..." is literal, whitespace-separated data.
    [GeneratedRegex("""^ASPIRE_MCP_TOOLS="(?<values>[^"]*)"[ \t]*\r?$""", RegexOptions.Multiline)]
    private static partial Regex McpToolsDeclaration();

    [GeneratedRegex("""\A[a-zA-Z0-9_-]+\z""")]
    private static partial Regex ToolIdentifier();
}
