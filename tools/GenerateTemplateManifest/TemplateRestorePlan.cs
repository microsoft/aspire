// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.TemplateEngine.Abstractions.Installer;
using Microsoft.TemplateEngine.Edge;
using Microsoft.TemplateEngine.Edge.Settings;
using Microsoft.TemplateEngine.Edge.Template;

namespace GenerateTemplateManifest;

internal sealed class TemplateRestorePlan
{
    private readonly Dictionary<string, RestoreProject> _projects = new(StringComparer.Ordinal);

    internal IReadOnlyCollection<RestoreProject> Projects => _projects.Values;
    internal int ConfigurationCount { get; private set; }
    internal int ProjectCount { get; private set; }
    internal HashSet<(string Name, string Version)> Sdks { get; } = [];

    internal async Task DiscoverAsync(string sourceDirectory, string processedDirectory, string workDirectory)
    {
        var snapshot = Path.Combine(workDirectory, "template-inputs");
        Directory.CreateDirectory(snapshot);
        var configs = Directory.GetFiles(sourceDirectory, "template.json", SearchOption.AllDirectories).Order(StringComparer.Ordinal).ToArray();
        foreach (var configPath in configs)
        {
            var templateRoot = Path.GetDirectoryName(Path.GetDirectoryName(configPath))!;
            var relativeRoot = Path.GetRelativePath(sourceDirectory, templateRoot);
            var destination = Path.Combine(snapshot, relativeRoot);
            Directory.CreateDirectory(Path.Combine(destination, ".template.config"));

            // Render only dependency-bearing inputs, but leave the engine's conditions, computed
            // symbols, replacements and source exclusions intact. No post-actions are executed.
            foreach (var sourcePath in Directory.EnumerateFiles(templateRoot, "*", SearchOption.AllDirectories).Where(IsDependencyInput))
            {
                if (Path.GetExtension(sourcePath) is ".props" or ".targets")
                {
                    throw new InvalidDataException($"Shared template MSBuild input needs explicit restore-plan support: {sourcePath}");
                }
                var relative = Path.GetRelativePath(sourceDirectory, sourcePath);
                var output = Path.Combine(snapshot, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(output)!);
                File.Copy(Path.Combine(processedDirectory, relative), output, overwrite: true);
            }
            File.Copy(Path.Combine(processedDirectory, Path.GetRelativePath(sourceDirectory, configPath)),
                Path.Combine(destination, ".template.config", "template.json"), overwrite: true);
        }

        var components = Microsoft.TemplateEngine.Edge.Components.AllComponents
            .Concat(Microsoft.TemplateEngine.Orchestrator.RunnableProjects.Components.AllComponents).ToList();
        using var host = new DefaultTemplateEngineHost("dotnetcli", "1.0.0", builtIns: components);
        using var settings = new EngineEnvironmentSettings(host, virtualizeSettings: true,
            settingsLocation: Path.Combine(workDirectory, "template-hive"));
        using var manager = new TemplatePackageManager(settings);
        var installed = await manager.GetBuiltInManagedProvider().InstallAsync([new InstallRequest(snapshot)], CancellationToken.None).ConfigureAwait(false);
        if (installed.Any(result => !result.Success))
        {
            throw new InvalidDataException($"Could not load dependency templates: {string.Join(", ", installed.Select(r => r.Error))}");
        }
        var templates = await manager.GetTemplatesAsync(CancellationToken.None).ConfigureAwait(false);
        if (templates.Count != configs.Length)
        {
            throw new InvalidDataException($"Expected {configs.Length} templates, discovered {templates.Count}.");
        }
        var creator = new TemplateCreator(settings);
        foreach (var template in templates.OrderBy(t => t.Identity, StringComparer.Ordinal))
        {
            var configPath = Path.Combine(snapshot, template.ConfigPlace.TrimStart('/', '\\').Replace('/', Path.DirectorySeparatorChar));
            var config = JsonNode.Parse(File.ReadAllText(configPath))!.AsObject();
            var templateRoot = Path.GetDirectoryName(Path.GetDirectoryName(configPath))!;
            var content = string.Join("\n", Directory.EnumerateFiles(templateRoot, "*", SearchOption.AllDirectories)
                .Where(IsDependencyInput).Select(File.ReadAllText));

            foreach (var parameters in GetConfigurations(config, content))
            {
                ConfigurationCount++;
                var output = Path.Combine(workDirectory, "rendered", ConfigurationCount.ToString("D4", CultureInfo.InvariantCulture));
                var result = await creator.InstantiateAsync(template, "Template", null, output, parameters).ConfigureAwait(false);
                if (result.Status != CreationResultStatus.Success)
                {
                    throw new InvalidDataException($"Could not render {template.Identity}: {result.Status}: {result.ErrorMessage}");
                }
                AddConfiguration(output, parameters);
            }
        }
    }

    internal static IEnumerable<Dictionary<string, string?>> GetConfigurations(JsonObject config, string content)
    {
        var symbols = config["symbols"]?.AsObject() ?? [];
        var relevant = content + "\nFramework\n" + config["sources"]?.ToJsonString();
        var included = new HashSet<string>(StringComparer.Ordinal);
        bool changed;
        do
        {
            changed = false;
            foreach (var (name, symbol) in symbols)
            {
                if (!included.Contains(name) &&
                    (Regex.IsMatch(relevant, $@"\b{Regex.Escape(name)}\b") ||
                     symbol?["replaces"]?.GetValue<string>() is { Length: > 0 } replacement &&
                     relevant.Contains(replacement, StringComparison.Ordinal)))
                {
                    included.Add(name);
                    relevant += "\n" + symbol!.ToJsonString();
                    changed = true;
                }
            }
        } while (changed);

        IEnumerable<Dictionary<string, string?>> configurations = [new(StringComparer.Ordinal)];
        foreach (var (name, symbol) in symbols.Where(s => included.Contains(s.Key) && s.Value?["type"]?.GetValue<string>() == "parameter"))
        {
            string[] values = symbol!["datatype"]?.GetValue<string>() switch
            {
                "bool" => ["false", "true"],
                "choice" => symbol["choices"]!.AsArray().Select(c => c!["choice"]!.GetValue<string>()).ToArray(),
                _ => throw new InvalidDataException($"Dependency input uses unbounded parameter '{name}'. Define finite coverage for it.")
            };
            configurations = configurations.SelectMany(c => values.Select(value =>
            {
                var copy = new Dictionary<string, string?>(c, StringComparer.Ordinal) { [name] = value };
                return copy;
            })).ToArray();
        }
        return configurations;
    }

    private void AddConfiguration(string directory, IReadOnlyDictionary<string, string?> parameters)
    {
        var documents = new Dictionary<string, XElement>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories).Where(IsProject).Order(StringComparer.Ordinal))
        {
            documents.Add(Path.GetFullPath(path), XDocument.Load(path).Root!);
        }
        foreach (var path in Directory.EnumerateFiles(directory, "*.cs", SearchOption.AllDirectories))
        {
            var directives = File.ReadLines(path).Where(line => line.StartsWith("#:", StringComparison.Ordinal)).ToArray();
            if (directives.Length == 0)
            {
                continue;
            }
            var project = new XElement("Project", new XAttribute("Sdk", "Microsoft.NET.Sdk"),
                new XElement("PropertyGroup",
                    new XElement("TargetFramework", parameters["Framework"]),
                    new XElement("OutputType", "Exe"),
                    new XElement("FileBasedProgram", "true")));
            foreach (var directive in directives)
            {
                // File-based inputs use #:sdk Name@Version, #:package Name@Version, and #:property Name=Value.
                var match = Regex.Match(directive, @"^#:(sdk|package|property)\s+([^@=\s]+)[@=](.+)$");
                if (!match.Success)
                {
                    throw new InvalidDataException($"Unsupported file-based dependency directive: {directive}");
                }
                var name = match.Groups[2].Value;
                var value = match.Groups[3].Value;
                switch (match.Groups[1].Value)
                {
                    case "sdk":
                        project.SetAttributeValue("Sdk", $"{name}/{value}");
                        break;
                    case "package":
                        project.Add(new XElement("ItemGroup", new XElement("PackageReference", new XAttribute("Include", name), new XAttribute("Version", value))));
                        break;
                    case "property":
                        project.Element("PropertyGroup")!.Add(new XElement(name, value));
                        break;
                }
            }
            documents.Add(Path.ChangeExtension(path, ".csproj"), project);
        }

        var resolved = new Dictionary<string, RestoreProject>(StringComparer.OrdinalIgnoreCase);
        var visiting = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in documents.Keys)
        {
            Resolve(path);
        }

        RestoreProject Resolve(string path)
        {
            if (resolved.TryGetValue(path, out var existing))
            {
                return existing;
            }
            if (!visiting.Add(path))
            {
                throw new InvalidDataException($"Cyclic template project reference: {path}");
            }
            var project = Normalize(documents[path]);
            foreach (var reference in project.Descendants("ProjectReference"))
            {
                var relative = reference.Attribute("Include")!.Value.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar);
                var referencedPath = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(path)!, relative));
                if (!documents.ContainsKey(referencedPath))
                {
                    throw new InvalidDataException($"Missing template project reference: {referencedPath}");
                }
                var child = Resolve(referencedPath);
                reference.SetAttributeValue("Include", $"../{child.Name}/{child.Name}.csproj");
            }
            foreach (var sdk in (project.Attribute("Sdk")?.Value ?? "").Split(';'))
            {
                var parts = sdk.Split('/', 2);
                if (parts.Length == 2)
                {
                    Sdks.Add((parts[0], parts[1]));
                }
            }

            var framework = project.Descendants("TargetFramework").SingleOrDefault()?.Value;
            project.Descendants("TargetFramework").Remove();
            var key = project.ToString(SaveOptions.DisableFormatting);
            if (!_projects.TryGetValue(key, out var planned))
            {
                planned = new RestoreProject($"Graph{_projects.Count:D3}", project);
                _projects.Add(key, planned);
            }
            if (framework is not null)
            {
                planned.Frameworks.Add(framework);
            }
            ProjectCount++;
            resolved.Add(path, planned);
            visiting.Remove(path);
            return planned;
        }
    }

    internal static XElement Normalize(XElement source)
    {
        var project = new XElement(source);
        project.DescendantNodes().OfType<XComment>().Remove();
        // Choose/Import/Target can consume preceding properties/items. Moving groups across
        // those elements changes evaluation order, so preserve these project shapes verbatim.
        if (project.Elements().Any(element => element.Name.LocalName is not ("PropertyGroup" or "ItemGroup")))
        {
            return project;
        }
        // These affect compilation or app identity, never restore. Preserve all other properties
        // (including runner switches) and all dependency metadata rather than comparing package IDs alone.
        string[] compilationOnly = ["ImplicitUsings", "Nullable", "UserSecretsId", "IsPackable", "IsAspireSharedProject"];
        project.Elements("PropertyGroup").Elements().Where(p => compilationOnly.Contains(p.Name.LocalName, StringComparer.Ordinal)).Remove();
        foreach (var group in project.Elements("ItemGroup"))
        {
            group.Elements().Where(item => item.Name.LocalName == "Using").Remove();
        }
        foreach (var group in project.Elements().Where(g => g.Name.LocalName is "PropertyGroup" or "ItemGroup").ToArray())
        {
            if (!group.HasElements)
            {
                group.Remove();
            }
        }
        // Re-group unconditional properties/items so property ordering and group boundaries don't
        // defeat deduplication. Conditional groups remain intact for MSBuild to evaluate.
        foreach (var groupName in new[] { "PropertyGroup", "ItemGroup" })
        {
            var groups = project.Elements(groupName).ToArray();
            if (groups.Any(g => g.HasAttributes) ||
                groups.SelectMany(g => g.Elements()).Any(e => e.ToString().Contains("$(", StringComparison.Ordinal)) ||
                groups.SelectMany(g => g.Elements()).Any(e => e.Attribute("Update") is not null || e.Attribute("Remove") is not null) ||
                groupName == "PropertyGroup" && groups.SelectMany(g => g.Elements()).GroupBy(e => e.Name).Any(g => g.Count() > 1))
            {
                continue;
            }
            var elements = groups.SelectMany(g => g.Elements()).ToArray();
            if (groupName == "PropertyGroup")
            {
                elements = elements.OrderBy(e => e.ToString(SaveOptions.DisableFormatting), StringComparer.Ordinal).ToArray();
            }
            // Items are evaluated in declaration order, including across groups. For example,
            // <PackageReference Include="@(TemplatePackage)" /> must stay after its TemplatePackage items.
            groups.Remove();
            if (elements.Length > 0)
            {
                project.Add(new XElement(groupName, elements));
            }
        }
        return project;
    }

    internal string Write(string directory)
    {
        foreach (var project in Projects)
        {
            var root = new XElement(project.Project);
            root.AddFirst(new XElement("PropertyGroup",
                project.Frameworks.Count > 0 ? new XElement("TargetFrameworks", string.Join(";", project.Frameworks)) : null,
                new XElement("EnableDefaultItems", "false"),
                new XElement("ManagePackageVersionsCentrally", "false"),
                new XElement("NuGetAudit", "false")));
            var projectDirectory = Path.Combine(directory, project.Name);
            Directory.CreateDirectory(projectDirectory);
            new XDocument(root).Save(Path.Combine(projectDirectory, $"{project.Name}.csproj"));
        }
        var solutionPath = Path.Combine(directory, "TemplateDependencies.slnx");
        new XDocument(new XElement("Solution", Projects.Select(project =>
            new XElement("Project", new XAttribute("Path", $"{project.Name}/{project.Name}.csproj"))))).Save(solutionPath);
        return solutionPath;
    }

    private static bool IsProject(string path) => Path.GetExtension(path) is ".csproj" or ".esproj";
    private static bool IsDependencyInput(string path) => IsProject(path) || Path.GetExtension(path) is ".props" or ".targets" ||
        Path.GetExtension(path) == ".cs" && File.ReadLines(path).Any(line => line.StartsWith("#:", StringComparison.Ordinal));

    internal sealed class RestoreProject(string name, XElement project)
    {
        internal string Name { get; } = name;
        internal XElement Project { get; } = project;
        internal SortedSet<string> Frameworks { get; } = new(StringComparer.Ordinal);
    }
}
