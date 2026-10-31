// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using System.Text.RegularExpressions;

namespace Aspire.Cli.EndToEnd.Tests.Helpers;

/// <summary>
/// The Aspire project templates pick every launch profile port (dashboard, OTLP, resource service and
/// project endpoints) at random from a fixed 300-port range, and only avoid ports that are bound while
/// the template runs. Two projects generated before either is started therefore share a port a few
/// percent of the time, and the second AppHost then fails with "address already in use".
/// </summary>
internal static partial class LaunchSettingsPortHelpers
{
    private const int MaxPort = 65535;

    /// <summary>
    /// Rewrites the launch settings ports of <paramref name="project"/> that are also used by any of
    /// <paramref name="otherProjects"/>, so the projects can run side by side. Each colliding port is
    /// replaced by the next higher port not used by any of the projects; other ports are left unchanged.
    /// </summary>
    /// <returns>The original-to-new mapping of the rewritten ports.</returns>
    internal static IReadOnlyDictionary<int, int> MakePortsDistinct(DirectoryInfo project, params DirectoryInfo[] otherProjects)
    {
        var otherPorts = otherProjects.SelectMany(GetLaunchSettingsFiles).SelectMany(f => GetPorts(File.ReadAllText(f))).ToHashSet();
        var projectFiles = GetLaunchSettingsFiles(project).ToArray();
        var projectPorts = projectFiles.SelectMany(f => GetPorts(File.ReadAllText(f))).ToHashSet();

        var used = new HashSet<int>(otherPorts);
        used.UnionWith(projectPorts);

        var mapping = new Dictionary<int, int>();
        foreach (var port in projectPorts.Where(otherPorts.Contains).Order())
        {
            var candidate = port + 1;
            while (!used.Add(candidate))
            {
                candidate++;
            }

            if (candidate > MaxPort)
            {
                throw new InvalidOperationException($"No free replacement port found for {port}.");
            }

            mapping[port] = candidate;
        }

        if (mapping.Count > 0)
        {
            foreach (var file in projectFiles)
            {
                var content = File.ReadAllText(file);
                var updated = PortRegex().Replace(content, m =>
                    mapping.TryGetValue(int.Parse(m.Value, CultureInfo.InvariantCulture), out var newPort)
                        ? newPort.ToString(CultureInfo.InvariantCulture)
                        : m.Value);
                if (!string.Equals(content, updated, StringComparison.Ordinal))
                {
                    File.WriteAllText(file, updated);
                }
            }
        }

        return mapping;
    }

    internal static IEnumerable<int> GetPorts(string launchSettingsContent) =>
        PortRegex().Matches(launchSettingsContent).Select(m => int.Parse(m.Value, CultureInfo.InvariantCulture));

    internal static IEnumerable<string> GetLaunchSettingsFiles(DirectoryInfo project) =>
        Directory.EnumerateFiles(project.FullName, "launchSettings.json", SearchOption.AllDirectories).Order(StringComparer.Ordinal);

    // Matches the port of a URL such as "https://localhost:17179" or "http://app.dev.localhost:15203".
    [GeneratedRegex(@"(?<=://[^/\s"";]+:)\d+")]
    private static partial Regex PortRegex();
}
