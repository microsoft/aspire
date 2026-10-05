// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Text.Json;
using System.Xml.Linq;
using GenerateTemplateManifest;
using NuGet.Configuration;

var command = new TemplateManifestCommand();
command.SetAction(async result =>
{
    var sourceDirectory = result.GetValue(command.SourceDirectory)!.FullName;
    var processedDirectory = result.GetValue(command.ProcessedDirectory)!.FullName;
    var manifestPath = result.GetValue(command.ManifestPath)!.FullName;
    var nugetConfigPath = result.GetValue(command.NuGetConfigPath)!.FullName;
    var localPackageFeed = result.GetValue(command.LocalPackageFeed)!.FullName;
    var restoreDirectory = result.GetValue(command.RestoreDirectory)!.FullName;
    var planOnly = result.GetValue(command.PlanOnly);
    var builtPackages = planOnly ? new HashSet<string>() : ComponentManifest.ReadBuiltPackages(localPackageFeed);
    var manifest = new ComponentManifest(builtPackages);
    var lockCount = 0;

    foreach (var lockPath in Directory.EnumerateFiles(sourceDirectory, "package-lock.json", SearchOption.AllDirectories))
    {
        lockCount++;
        var packagePath = Path.Combine(Path.GetDirectoryName(lockPath)!, "package.json");
        using var packageJson = JsonDocument.Parse(File.ReadAllText(packagePath));
        using var lockfile = JsonDocument.Parse(File.ReadAllText(lockPath));
        if (lockfile.RootElement.GetProperty("lockfileVersion").GetInt32() != 3)
        {
            throw new InvalidDataException($"Expected npm lockfile version 3 in {lockPath}");
        }

        var packages = lockfile.RootElement.GetProperty("packages");
        var root = packages.GetProperty("");
        foreach (var group in new[] { "dependencies", "devDependencies", "optionalDependencies" })
        {
            var declared = packageJson.RootElement.TryGetProperty(group, out var direct)
                ? direct.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString())
                : new Dictionary<string, string?>();
            var locked = root.TryGetProperty(group, out var resolved)
                ? resolved.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString())
                : new Dictionary<string, string?>();
            if (declared.Count != locked.Count || declared.Any(p => !locked.TryGetValue(p.Key, out var version) || p.Value != version))
            {
                throw new InvalidDataException($"{group} in {lockPath} differs from {packagePath}; update the npm lockfile");
            }
        }

        foreach (var package in packages.EnumerateObject())
        {
            if (package.Name.Length == 0)
            {
                continue; // The root entry is the template app, not an installed package.
            }

            var marker = "node_modules/";
            var index = package.Name.LastIndexOf(marker, StringComparison.Ordinal);
            if (index < 0)
            {
                throw new InvalidDataException($"Unexpected npm package path '{package.Name}' in {lockPath}");
            }

            // Aliases are installed under their alias path, e.g. node_modules/aliased-number,
            // but the lock entry's "name": "is-number" identifies the actual component.
            var name = package.Value.TryGetProperty("name", out var packageName)
                ? packageName.GetString() ?? ""
                : package.Name[(index + marker.Length)..];
            manifest.Register("npm", name,
                package.Value.GetProperty("version").GetString() ?? "", lockPath);
        }
    }

    foreach (var packagePath in Directory.EnumerateFiles(sourceDirectory, "package.json", SearchOption.AllDirectories))
    {
        if (!File.Exists(Path.Combine(Path.GetDirectoryName(packagePath)!, "package-lock.json")))
        {
            throw new InvalidDataException($"Missing npm lockfile for {packagePath}");
        }
    }

    Directory.CreateDirectory(restoreDirectory);
    File.WriteAllText(Path.Combine(restoreDirectory, "Directory.Build.props"), "<Project />");
    File.WriteAllText(Path.Combine(restoreDirectory, "Directory.Build.targets"), "<Project />");
    File.WriteAllText(Path.Combine(restoreDirectory, "Directory.Packages.props"), "<Project />");

    // Use a fresh discovery directory so removed templates/configurations cannot survive an
    // incremental run. Only the stable, deduplicated restore projects retain their assets/cache.
    var discoveryDirectory = Directory.CreateTempSubdirectory("aspire-template-cg-");
    var plan = new TemplateRestorePlan();
    var discoveryTime = Stopwatch.StartNew();
    try
    {
        await plan.DiscoverAsync(sourceDirectory, processedDirectory, discoveryDirectory.FullName).ConfigureAwait(false);
    }
    finally
    {
        discoveryDirectory.Delete(recursive: true);
    }
    if (plan.Projects.Count == 0)
    {
        throw new InvalidDataException($"No template projects found under {sourceDirectory}");
    }
    var solutionPath = plan.Write(restoreDirectory);
    discoveryTime.Stop();
    Console.WriteLine($"{plan.ConfigurationCount} template configurations, {plan.ProjectCount} project instances -> {plan.Projects.Count} unique projects ({plan.Projects.Sum(p => p.Frameworks.Count)} framework graphs), planned in {discoveryTime.Elapsed.TotalSeconds:F2}s.");
    if (planOnly)
    {
        return 0;
    }
    foreach (var (name, version) in plan.Sdks)
    {
        manifest.Register("nuget", name, version, solutionPath);
    }

    var startInfo = new ProcessStartInfo("dotnet")
    {
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
        WorkingDirectory = restoreDirectory
    };
    // SDK resolution also discovers config files by walking project directories, independently
    // of restore's --configfile. Always refresh the adjacent config, including when a local feed disappears.
    var config = XDocument.Load(nugetConfigPath);
    var configuration = config.Root ?? throw new InvalidDataException($"Invalid NuGet config: {nugetConfigPath}");
    var sources = configuration.Element("packageSources")
        ?? throw new InvalidDataException($"NuGet config has no packageSources: {nugetConfigPath}");
    var mappings = configuration.Element("packageSourceMapping")
        ?? throw new InvalidDataException($"NuGet config has no packageSourceMapping: {nugetConfigPath}");
    var builtIds = builtPackages.Select(package => package[..package.LastIndexOf('/')]).ToHashSet(StringComparer.OrdinalIgnoreCase);

    // A -dev nupkg can change without its version changing. Use a private extraction cache and
    // evict only this build's exact identities. The normal cache remains a read-only local feed
    // for external packages, avoiding downloads already performed by the managed build.
    var sharedCache = SettingsUtility.GetGlobalPackagesFolder(
        Settings.LoadSpecificSettings(Path.GetDirectoryName(nugetConfigPath)!, Path.GetFileName(nugetConfigPath)));
    var privateCache = Path.Combine(restoreDirectory, "packages");
    Directory.CreateDirectory(privateCache);
    foreach (var package in builtPackages)
    {
        var packagePath = Path.GetFullPath(Path.Combine(privateCache, package.ToLowerInvariant().Replace('/', Path.DirectorySeparatorChar)));
        if (!packagePath.StartsWith(privateCache + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"Invalid built package cache path: {package}");
        }
        if (Directory.Exists(packagePath))
        {
            Directory.Delete(packagePath, recursive: true);
        }
    }
    startInfo.Environment["NUGET_PACKAGES"] = privateCache;
    if (Directory.Exists(sharedCache) && !string.Equals(sharedCache, privateCache, StringComparison.OrdinalIgnoreCase))
    {
        const string cacheSource = "template-external-cache";
        sources.Add(new XElement("add", new XAttribute("key", cacheSource), new XAttribute("value", sharedCache)));
        var cachePatterns = mappings.Elements("packageSource").Elements("package")
            .Select(p => p.Attribute("pattern")!.Value)
            .Where(pattern => !builtIds.Contains(pattern))
            .Distinct(StringComparer.OrdinalIgnoreCase);
        mappings.Add(new XElement("packageSource", new XAttribute("key", cacheSource),
            cachePatterns.Select(pattern => new XElement("package", new XAttribute("pattern", pattern)))));
    }
    // Exact mappings take precedence over the external feed/cache patterns, so packages
    // produced by this build cannot be satisfied by another build's package cache.
    const string localSourceName = "template-built-local";
    sources.Add(new XElement("add",
        new XAttribute("key", localSourceName),
        new XAttribute("value", localPackageFeed)));
    mappings.Add(new XElement("packageSource",
        new XAttribute("key", localSourceName),
        builtIds.Order(StringComparer.OrdinalIgnoreCase).Select(id => new XElement("package", new XAttribute("pattern", id)))));
    var restoreConfigPath = Path.Combine(restoreDirectory, "nuget.config");
    config.Save(restoreConfigPath);

    // A sibling graph can repopulate an evicted -dev package before NuGet checks another graph
    // for no-op restore. Reevaluate every graph, even when all package IDs/versions are unchanged.
    foreach (var argument in new[] { "restore", solutionPath, "--configfile", restoreConfigPath, "--force", "--disable-build-servers", "--verbosity", "quiet" })
    {
        startInfo.ArgumentList.Add(argument);
    }

    var restoreTime = Stopwatch.StartNew();
    using (var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Failed to start dotnet restore"))
    {
        // Drain both streams concurrently so the shared restore cannot block on a full pipe.
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        process.WaitForExit();
        Console.Write(stdout.GetAwaiter().GetResult());
        Console.Error.Write(stderr.GetAwaiter().GetResult());
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"Template dependency restore failed (exit code {process.ExitCode})");
        }
        restoreTime.Stop();
        Console.WriteLine($"Independent graph batch restored in {restoreTime.Elapsed.TotalSeconds:F2}s.");
    }

    foreach (var project in plan.Projects)
    {
        // JavaScript SDK projects have no NuGet restore graph. Their npm graph is read from
        // the lockfile; the SDK itself is registered above. Do not silently skip new NuGet inputs.
        if (project.Frameworks.Count == 0 &&
            project.Project.Attribute("Sdk")!.Value.StartsWith("Microsoft.VisualStudio.JavaScript.Sdk/", StringComparison.Ordinal))
        {
            if (project.Project.Descendants().Any(e => e.Name.LocalName is "PackageReference" or "PackageDownload" or "ProjectReference"))
            {
                throw new InvalidDataException($"JavaScript project {project.Name} contains unsupported NuGet dependencies.");
            }
            continue;
        }
        var assetsPath = Path.Combine(restoreDirectory, project.Name, "obj", "project.assets.json");
        using var assets = JsonDocument.Parse(File.ReadAllText(assetsPath));
        manifest.RegisterNuGetAssets(assets.RootElement, assetsPath);
    }

    Directory.CreateDirectory(Path.GetDirectoryName(manifestPath)!);
    File.WriteAllText(manifestPath, manifest.Serialize());
    Console.WriteLine($"Wrote {manifest.Count} registrations from {plan.Projects.Count} restore projects and {lockCount} npm lockfiles to {manifestPath}");
    return 0;
});

return await command.Parse(args).InvokeAsync().ConfigureAwait(false);
