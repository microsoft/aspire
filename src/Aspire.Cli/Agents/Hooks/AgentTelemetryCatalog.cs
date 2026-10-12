// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.RegularExpressions;

namespace Aspire.Cli.Agents.Hooks;

/// <summary>
/// Reads telemetry allowlists from the embedded canonical hook.
/// </summary>
internal sealed partial class AgentTelemetryCatalog
{
    private static readonly Lazy<AgentTelemetryCatalog> s_bundled = new(LoadEmbedded);

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
        var skills = ParseIdentifiers(script, SkillsDeclaration(), "ASPIRE_SKILLS");
        var tools = ParseIdentifiers(script, McpToolsDeclaration(), "ASPIRE_MCP_TOOLS");
        var references = ParseValues(script, ReferenceFilesDeclaration(), "ASPIRE_REFERENCE_FILES");
        foreach (var reference in references)
        {
            var segments = reference.Split('/', StringSplitOptions.None);
            if (segments.Length < 3 ||
                !skills.Contains(segments[0]) ||
                !segments[1].Equals("references", StringComparison.Ordinal) ||
                reference.Contains('\\') ||
                segments.Skip(2).Any(segment => segment is "" or "." or ".."))
            {
                throw new InvalidDataException("The canonical telemetry hook contains an invalid ASPIRE_REFERENCE_FILES allowlist.");
            }
        }

        return new(skills, tools, references);
    }

    private static HashSet<string> ParseIdentifiers(string script, Regex declaration, string variableName)
    {
        var values = ParseValues(script, declaration, variableName);
        if (values.Any(value => !Identifier().IsMatch(value)))
        {
            throw new InvalidDataException($"The canonical telemetry hook contains an invalid {variableName} allowlist.");
        }

        return values;
    }

    private static HashSet<string> ParseValues(string script, Regex declaration, string variableName)
    {
        var declarations = declaration.Matches(script);
        if (declarations.Count != 1)
        {
            throw new InvalidDataException($"The canonical telemetry hook must declare {variableName} exactly once.");
        }

        // Canonical declarations use these raw forms:
        //   ASPIRE_SKILLS="aspire aspire-init ..."
        //   ASPIRE_MCP_TOOLS="doctor list_resources ..."
        //   ASPIRE_REFERENCE_FILES="
        //   aspire-deployment/references/azure.md
        //   ..."
        // Split on Unicode whitespace so both single-line and multiline values use the same parser.
        var values = declarations[0].Groups["values"].Value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var result = new HashSet<string>(values, StringComparer.OrdinalIgnoreCase);
        if (values.Length == 0 || result.Count != values.Length)
        {
            throw new InvalidDataException($"The canonical telemetry hook contains an invalid {variableName} allowlist.");
        }

        return result;
    }

    private static AgentTelemetryCatalog LoadEmbedded()
    {
        // Read only the compiled canonical hook. User-installed scripts and plugin contents must not
        // be able to introduce new telemetry dimensions.
        using var stream = typeof(AgentTelemetryCatalog).Assembly.GetManifestResourceStream(TelemetryHookInstaller.ShellResourceName)
            ?? throw new InvalidDataException("The canonical telemetry hook is missing.");
        using var reader = new StreamReader(stream);
        return Parse(reader.ReadToEnd());
    }

    [GeneratedRegex("""^ASPIRE_SKILLS="(?<values>[^"]*)"[ \t]*\r?$""", RegexOptions.Multiline)]
    private static partial Regex SkillsDeclaration();

    [GeneratedRegex("""^ASPIRE_MCP_TOOLS="(?<values>[^"]*)"[ \t]*\r?$""", RegexOptions.Multiline)]
    private static partial Regex McpToolsDeclaration();

    [GeneratedRegex("""^ASPIRE_REFERENCE_FILES="(?<values>[^"]*)"[ \t]*\r?$""", RegexOptions.Multiline)]
    private static partial Regex ReferenceFilesDeclaration();

    [GeneratedRegex("""\A[a-zA-Z0-9_-]+\z""")]
    private static partial Regex Identifier();
}
