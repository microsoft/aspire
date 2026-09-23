// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.RegularExpressions;

namespace Aspire.Cli.Agents.Hooks;

/// <summary>
/// Reads the telemetry allowlists from the embedded, release-verified canonical hook.
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

    internal static AgentTelemetryCatalog Parse(string script)
    {
        var declarations = Declarations().Matches(script);
        return new(Read("ASPIRE_SKILLS"), Read("ASPIRE_MCP_TOOLS"), Read("ASPIRE_REFERENCE_FILES"));

        HashSet<string> Read(string name)
        {
            var matches = declarations.Where(match => match.Groups["name"].Value == name).ToArray();
            if (matches.Length != 1)
            {
                throw new InvalidDataException($"The bundled telemetry hook must declare {name} exactly once.");
            }

            var values = matches[0].Groups["values"].Value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (values.Length == 0 || values.Any(value => !Identifier().IsMatch(value)))
            {
                throw new InvalidDataException($"The bundled telemetry hook contains an invalid {name} allowlist.");
            }

            return new HashSet<string>(values, StringComparer.OrdinalIgnoreCase);
        }
    }

    private static AgentTelemetryCatalog LoadBundled()
    {
        // Read the compiled resource, never the mutable installed script. Updating the canonical
        // skills bundle then updates native classification too, without another maintained list.
        using var stream = typeof(AgentTelemetryCatalog).Assembly.GetManifestResourceStream("track-telemetry.sh")
            ?? throw new InvalidDataException("The bundled telemetry hook is missing.");
        using var reader = new StreamReader(stream);
        return Parse(reader.ReadToEnd());
    }

    // Canonical declarations are literal, whitespace-separated names, optionally on multiple lines:
    // ASPIRE_SKILLS="aspire aspire-init"
    // ASPIRE_REFERENCE_FILES="
    // aspire-init/references/templates.md
    // "
    // Do not evaluate shell expressions or accept an unrecognized format as an empty allowlist.
    [GeneratedRegex("""^(?<name>ASPIRE_SKILLS|ASPIRE_MCP_TOOLS|ASPIRE_REFERENCE_FILES)="(?<values>[^"]*)"[ \t]*\r?$""", RegexOptions.Multiline)]
    private static partial Regex Declarations();

    [GeneratedRegex("""\A[a-zA-Z0-9_./-]+\z""")]
    private static partial Regex Identifier();
}
