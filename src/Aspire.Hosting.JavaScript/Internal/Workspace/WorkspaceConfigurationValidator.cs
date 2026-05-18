// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#pragma warning disable ASPIREDOCKERFILEBUILDER001

//
// Validates the JavaScript workspace state for a resource. Runs at publish-mode
// Dockerfile generation time and at run-mode installer startup, NOT at
// AppHost construction. This means errors in the workspace declaration,
// lockfiles, or member layout surface as `aspire publish` / `aspire do build`
// / run-mode resource errors — not as exceptions thrown from the user's
// AppHost code. The user experience matches "my repo configuration is wrong"
// rather than "my .NET code is wrong".
//
// The validator collects ALL discovered issues into a single
// DistributedApplicationException so the user sees every problem at once,
// rather than one per round-trip through the publish pipeline.
//
// On success, the rich JavaScriptWorkspaceAnnotation is attached to the
// resource and downstream consumers (the Dockerfile generator, AddInstaller)
// read it as before.

using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Aspire.Hosting.ApplicationModel;
using YamlDotNet.Serialization;

namespace Aspire.Hosting.JavaScript.Internal.Workspace;

internal static class WorkspaceConfigurationValidator
{
    private static readonly IDeserializer s_yamlDeserializer = new DeserializerBuilder()
        .IgnoreUnmatchedProperties()
        .Build();

    /// <summary>
    /// Validates the workspace configuration for the resource and, on success,
    /// attaches a fully-populated <see cref="JavaScriptWorkspaceAnnotation"/>.
    /// </summary>
    /// <returns>
    /// The validated annotation when configuration is valid, or <see langword="null"/>
    /// when the resource has no <see cref="JavaScriptWorkspaceConfigAnnotation"/>
    /// (i.e. the user did not opt into workspace mode).
    /// </returns>
    /// <exception cref="DistributedApplicationException">
    /// Thrown when one or more problems are detected. All problems are reported
    /// in a single message; the message format is line-oriented so users can
    /// fix everything in one pass.
    /// </exception>
    public static JavaScriptWorkspaceAnnotation? ValidateAndAttach(IResource resource)
    {
        ArgumentNullException.ThrowIfNull(resource);

        // If validation already ran (idempotency), return the existing annotation.
        if (resource.TryGetLastAnnotation<JavaScriptWorkspaceAnnotation>(out var existing))
        {
            return existing;
        }

        if (!resource.TryGetLastAnnotation<JavaScriptWorkspaceConfigAnnotation>(out var config))
        {
            return null;
        }

        var appDir = config.AppDirectory;

        var diagnostics = new List<string>();
        var validated = TryValidate(resource, config.RootPath, appDir, diagnostics);

        if (diagnostics.Count > 0)
        {
            throw new DistributedApplicationException(BuildErrorMessage(resource.Name, diagnostics));
        }

        if (validated is null)
        {
            // Should not happen — TryValidate either returns the annotation or fills diagnostics.
            throw new DistributedApplicationException($"Internal: workspace validation produced no annotation and no diagnostics for resource '{resource.Name}'.");
        }
        resource.Annotations.Add(validated);
        // Now that we know the workspace layout, fix up any ContainerFilesSourceAnnotation entries
        // captured by an earlier Publish* call so they live under /app/<AppRelativePath> instead of
        // bare /app.
        ContainerFilesPaths.RewriteForWorkspace(resource, validated);
        return validated;
    }

    /// <summary>
    /// Public entry point used by tests and other callers that want to drive
    /// validation against an explicit root + app directory pair without going
    /// through resource annotations.
    /// </summary>
    internal static JavaScriptWorkspaceAnnotation Validate(
        string resourceName,
        string resolvedRoot,
        string appDir,
        IReadOnlyList<string>? buildScriptNames = null,
        string? configuredPackageManager = null,
        string? startScriptName = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(resourceName);
        ArgumentException.ThrowIfNullOrEmpty(resolvedRoot);
        ArgumentException.ThrowIfNullOrEmpty(appDir);

        var diagnostics = new List<string>();
        var ann = TryValidate(
            resourceName,
            resolvedRoot,
            appDir,
            diagnostics,
            buildScriptNames,
            configuredPackageManager,
            startScriptName);

        if (diagnostics.Count > 0)
        {
            throw new DistributedApplicationException(BuildErrorMessage(resourceName, diagnostics));
        }

        return ann!;
    }

    private static JavaScriptWorkspaceAnnotation? TryValidate(
        IResource resource,
        string resolvedRoot,
        string appDir,
        List<string> diagnostics)
    {
        var buildScripts = resource.Annotations.OfType<JavaScriptBuildScriptAnnotation>()
            .Select(a => a.ScriptName)
            .ToList();
        var configuredPm = resource.TryGetLastAnnotation<JavaScriptPackageManagerAnnotation>(out var pm)
            ? pm.ExecutableName
            : null;
        var startScript = resource.TryGetLastAnnotation<JavaScriptPublishModeAnnotation>(out var publishMode) &&
                          publishMode.Mode == JavaScriptPublishMode.NpmScript
            ? publishMode.StartScriptName
            : null;

        return TryValidate(resource.Name, resolvedRoot, appDir, diagnostics, buildScripts, configuredPm, startScript);
    }

    private static JavaScriptWorkspaceAnnotation? TryValidate(
        string resourceName,
        string resolvedRoot,
        string appDir,
        List<string> diagnostics,
        IReadOnlyList<string>? buildScriptNames,
        string? configuredPackageManager,
        string? startScriptName)
    {
        // 1. Root directory exists.
        if (!Directory.Exists(resolvedRoot))
        {
            diagnostics.Add($"Workspace root '{resolvedRoot}' does not exist.");
            return null;
        }

        // 2. App directory is a strict descendant of the root.
        var rootFull = PathWithSeparator(resolvedRoot);
        var appFull = PathWithSeparator(appDir);
        if (!appFull.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase))
        {
            diagnostics.Add($"Application directory '{appDir}' is not a descendant of workspace root '{resolvedRoot}'.");
            return null;
        }

        if (appFull.Length == rootFull.Length)
        {
            diagnostics.Add($"Application directory '{appDir}' is the workspace root; expected a workspace member subdirectory.");
            return null;
        }

        var appRelative = appFull[rootFull.Length..]
            .TrimEnd(Path.DirectorySeparatorChar)
            .Replace(Path.DirectorySeparatorChar, '/');

        // 3. App package.json must exist and have a name.
        var appName = TryReadAppPackageName(appDir, resourceName, diagnostics);

        // 4. Workspace declaration: read & parse manifests, surfacing parse errors.
        var rawPatterns = ReadWorkspacePatterns(resolvedRoot, diagnostics);

        if (diagnostics.Count > 0)
        {
            // If any of the above produced errors, there's no point continuing pattern expansion.
            return null;
        }

        if (rawPatterns.Count == 0)
        {
            diagnostics.Add(
                $"No workspace patterns declared at '{resolvedRoot}'. " +
                "Expected a 'workspaces' field in package.json or a 'packages' list in pnpm-workspace.yaml.");
            return null;
        }

        // 5. Pattern shape validation (negated, recursive, non-trailing star).
        try
        {
            WorkspacePatternValidator.Validate(rawPatterns, resolvedRoot);
        }
        catch (DistributedApplicationException ex)
        {
            diagnostics.Add(ex.Message);
            return null;
        }

        // 6. Pattern expansion. Track per-pattern resolution to detect "matched dirs but
        //    none had package.json" cases.
        var allDirs = new SortedSet<string>(StringComparer.Ordinal);
        var unresolvedPatterns = new List<string>();
        foreach (var pattern in rawPatterns)
        {
            var resolved = WorkspacePatternExpander.Expand(resolvedRoot, [pattern]);
            if (resolved.Count == 0 && !pattern.StartsWith('!'))
            {
                unresolvedPatterns.Add(pattern);
            }
            foreach (var dir in resolved)
            {
                allDirs.Add(dir);
            }
        }

        if (unresolvedPatterns.Count > 0)
        {
            diagnostics.Add(
                $"Workspace pattern{(unresolvedPatterns.Count > 1 ? "s" : "")} " +
                $"{string.Join(", ", unresolvedPatterns.Select(p => $"'{p}'"))} " +
                $"in '{resolvedRoot}' did not match any directory containing a package.json. " +
                "Aspire only treats directories with a package.json as workspace members.");
            return null;
        }

        var workspaceDirs = allDirs.ToList();

        // 7. Duplicate package names across resolved members.
        var nameToDir = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var dir in workspaceDirs)
        {
            var packageName = JavaScriptWorkspaceReader.TryReadPackageName(Path.Combine(resolvedRoot, dir.Replace('/', Path.DirectorySeparatorChar)));
            if (string.IsNullOrEmpty(packageName))
            {
                continue;
            }
            if (nameToDir.TryGetValue(packageName, out var existingDir))
            {
                diagnostics.Add(
                    $"Workspace package name '{packageName}' is declared by both '{existingDir}/package.json' and '{dir}/package.json'. " +
                    "Workspace package names must be unique because Aspire invokes scripts by package name.");
            }
            else
            {
                nameToDir[packageName] = dir;
            }
        }

        // 8. App must be in member set.
        if (!workspaceDirs.Contains(appRelative, StringComparer.OrdinalIgnoreCase))
        {
            diagnostics.Add(
                $"Application directory '{appRelative}' is not a declared workspace member of '{resolvedRoot}'. " +
                $"Declared members: {string.Join(", ", workspaceDirs)}");
        }

        // 9. Manifests / lockfile detection.
        var manifests = WorkspaceManifestDiscovery.Discover(resolvedRoot);
        if (!manifests.HasPackageJson)
        {
            diagnostics.Add($"Workspace root '{resolvedRoot}' is missing package.json.");
        }
        if (!manifests.HasLockfile)
        {
            diagnostics.Add(
                $"Workspace root '{resolvedRoot}' has no recognized lockfile " +
                $"(expected one of: {string.Join(", ", WorkspaceManifestDiscovery.RecognizedLockfileNames)}). " +
                "Run 'npm install' / 'yarn install' / 'pnpm install' / 'bun install' at the workspace root before publishing.");
        }

        // 10. Package manager vs lockfile / packageManager field cross-check.
        if (!string.IsNullOrEmpty(configuredPackageManager))
        {
            ValidatePackageManagerConsistency(resolvedRoot, configuredPackageManager, diagnostics);
        }

        // 11. Explicit pnpm 10+ workspace PublishAsNpmScript uses pnpm 10's non-legacy deploy implementation.
        if (string.Equals(configuredPackageManager, "pnpm", StringComparison.Ordinal) &&
            !string.IsNullOrEmpty(startScriptName) &&
            PnpmPackageManagerVersion.TryReadMajorVersion(resolvedRoot) >= 10)
        {
            ValidatePnpmDeployConfiguration(resolvedRoot, diagnostics);
        }

        // 12. Configured build / start scripts must exist in app's package.json.
        if (!string.IsNullOrEmpty(appName))
        {
            ValidateAppScripts(appDir, appName, buildScriptNames, startScriptName, diagnostics);
        }

        if (diagnostics.Count > 0 || string.IsNullOrEmpty(appName))
        {
            return null;
        }

        return new JavaScriptWorkspaceAnnotation(
            resolvedRoot,
            appName!,
            appRelative,
            workspaceDirs,
            manifests.RootFiles,
            manifests.RootDirs);
    }

    private static string? TryReadAppPackageName(string appDir, string resourceName, List<string> diagnostics)
    {
        var packageJsonPath = Path.Combine(appDir, "package.json");
        if (!File.Exists(packageJsonPath))
        {
            diagnostics.Add(
                $"Application '{resourceName}' is missing '{packageJsonPath}'. " +
                "Workspace mode requires a package.json with a 'name' field at the application directory.");
            return null;
        }

        try
        {
            using var stream = File.OpenRead(packageJsonPath);
            var packageJson = JsonSerializer.Deserialize<PackageJsonNameInfo>(stream);
            if (packageJson?.Name is { Length: > 0 } name)
            {
                return name;
            }

            diagnostics.Add(
                $"'{packageJsonPath}' has no non-empty 'name' field. " +
                "Workspace mode invokes scripts by package name; add a unique 'name' value.");
            return null;
        }
        catch (JsonException ex)
        {
            diagnostics.Add($"Failed to parse '{packageJsonPath}': {ex.Message}");
            return null;
        }
        catch (IOException ex)
        {
            diagnostics.Add($"Failed to read '{packageJsonPath}': {ex.Message}");
            return null;
        }
    }

    private static List<string> ReadWorkspacePatterns(string rootPath, List<string> diagnostics)
    {
        var patterns = new List<string>();

        var packageJsonPath = Path.Combine(rootPath, "package.json");
        if (File.Exists(packageJsonPath))
        {
            try
            {
                var content = File.ReadAllText(packageJsonPath);
                var (parsed, shapeError) = ParsePackageJsonWorkspacesStrict(content, packageJsonPath);
                if (shapeError is not null)
                {
                    diagnostics.Add(shapeError);
                }
                patterns.AddRange(parsed);
            }
            catch (JsonException ex)
            {
                diagnostics.Add($"Failed to parse '{packageJsonPath}': {ex.Message}");
            }
            catch (IOException ex)
            {
                diagnostics.Add($"Failed to read '{packageJsonPath}': {ex.Message}");
            }
        }

        var pnpmYamlPath = Path.Combine(rootPath, "pnpm-workspace.yaml");
        if (File.Exists(pnpmYamlPath))
        {
            try
            {
                var content = File.ReadAllText(pnpmYamlPath);
                var (parsed, shapeError) = ParsePnpmPackagesStrict(content, pnpmYamlPath);
                if (shapeError is not null)
                {
                    diagnostics.Add(shapeError);
                }
                patterns.AddRange(parsed);
            }
            catch (YamlDotNet.Core.YamlException ex)
            {
                diagnostics.Add($"Failed to parse '{pnpmYamlPath}': {ex.Message}");
            }
            catch (IOException ex)
            {
                diagnostics.Add($"Failed to read '{pnpmYamlPath}': {ex.Message}");
            }
        }

        return patterns;
    }

    private static (IReadOnlyList<string> Patterns, string? ShapeError) ParsePackageJsonWorkspacesStrict(string content, string filePath)
    {
        try
        {
            var arrayDeclaration = JsonSerializer.Deserialize<PackageJsonWorkspaceArrayDeclaration>(content);
            if (arrayDeclaration?.Workspaces is null)
            {
                return ([], $"'{filePath}#workspaces' is null. Expected a string array or an object with 'packages: string[]'.");
            }

            return (FilterPatterns(arrayDeclaration.Workspaces), null);
        }
        catch (JsonException)
        {
            // The package.json workspaces contract is a JSON union:
            //   "workspaces": ["packages/*"]
            //   "workspaces": { "packages": ["packages/*"], "nohoist": ["**/react"] }
            // System.Text.Json intentionally binds DTOs to one shape at a time, so an
            // object-form declaration fails the array DTO above and is retried with the
            // object DTO below. If both fail, the original caller reports the JSON parse
            // error with the file path.
        }

        var objectDeclaration = JsonSerializer.Deserialize<PackageJsonWorkspaceObjectDeclaration>(content);
        if (objectDeclaration?.Workspaces is null)
        {
            return ([], null);
        }

        if (objectDeclaration.Workspaces.Packages is null)
        {
            return ([], $"'{filePath}#workspaces.packages' is null. Expected a string array.");
        }

        var keys = objectDeclaration.Workspaces.ExtensionData?.Keys.ToArray() ?? [];
        return objectDeclaration.Workspaces.Packages.Count == 0 && keys.Length > 0
            ? ([], $"'{filePath}#workspaces' object must contain a 'packages' array; found keys: {string.Join(", ", keys)}.")
            : (FilterPatterns(objectDeclaration.Workspaces.Packages), null);
    }

    private static (IReadOnlyList<string> Patterns, string? ShapeError) ParsePnpmPackagesStrict(string content, string filePath)
    {
        if (content.Length == 0)
        {
            return ([], null);
        }

        using var reader = new StringReader(content);
        var workspace = s_yamlDeserializer.Deserialize<PnpmWorkspacePackagesDeclaration>(reader);

        if (workspace?.Packages is null)
        {
            return ([], $"'{filePath}#packages' is null. Expected a YAML sequence of strings.");
        }

        return (FilterPatterns(workspace.Packages), null);
    }

    private static void ValidatePackageManagerConsistency(string resolvedRoot, string configuredPm, List<string> diagnostics)
    {
        var hasNpmLock = File.Exists(Path.Combine(resolvedRoot, "package-lock.json")) ||
                         File.Exists(Path.Combine(resolvedRoot, "npm-shrinkwrap.json"));
        var hasYarnLock = File.Exists(Path.Combine(resolvedRoot, "yarn.lock"));
        var hasPnpmLock = File.Exists(Path.Combine(resolvedRoot, "pnpm-lock.yaml"));
        var hasBunLock = File.Exists(Path.Combine(resolvedRoot, "bun.lock")) ||
                         File.Exists(Path.Combine(resolvedRoot, "bun.lockb"));
        var hasPnpmWorkspaceYaml = File.Exists(Path.Combine(resolvedRoot, "pnpm-workspace.yaml"));

        var lockfilePm = (hasNpmLock, hasYarnLock, hasPnpmLock, hasBunLock) switch
        {
            (true, false, false, false) => "npm",
            (false, true, false, false) => "yarn",
            (false, false, true, false) => "pnpm",
            (false, false, false, true) => "bun",
            _ => null,
        };

        if (lockfilePm is not null && !string.Equals(lockfilePm, configuredPm, StringComparison.Ordinal))
        {
            diagnostics.Add(
                $"Workspace root '{resolvedRoot}' has a {lockfilePm} lockfile, but the resource is configured to use {configuredPm}. " +
                $"Either call .With{Capitalize(lockfilePm)}() instead of .With{Capitalize(configuredPm)}(), or remove the {lockfilePm} lockfile.");
        }

        if (hasPnpmWorkspaceYaml && !string.Equals(configuredPm, "pnpm", StringComparison.Ordinal))
        {
            diagnostics.Add(
                $"Workspace root '{resolvedRoot}' contains 'pnpm-workspace.yaml' but the resource is configured to use {configuredPm}. " +
                "pnpm-workspace.yaml is pnpm-specific; call .WithPnpm() or remove the file.");
        }

        var packageManagerField = TryReadPackageManagerField(resolvedRoot);
        if (packageManagerField is not null && !packageManagerField.StartsWith(configuredPm + "@", StringComparison.Ordinal) && packageManagerField != configuredPm)
        {
            var declaredPm = packageManagerField.Split('@')[0];
            if (!string.Equals(declaredPm, configuredPm, StringComparison.Ordinal))
            {
                diagnostics.Add(
                    $"Workspace root 'package.json#packageManager' is '{packageManagerField}' but the resource is configured to use {configuredPm}. " +
                    $"Either call .With{Capitalize(declaredPm)}() instead of .With{Capitalize(configuredPm)}(), or update 'packageManager' in package.json.");
            }
        }
    }

    private static void ValidatePnpmDeployConfiguration(string resolvedRoot, List<string> diagnostics)
    {
        var pnpmYamlPath = Path.Combine(resolvedRoot, "pnpm-workspace.yaml");
        if (!File.Exists(pnpmYamlPath))
        {
            return;
        }

        try
        {
            var injectWorkspacePackages = PnpmWorkspaceYamlParser.ParseInjectWorkspacePackages(File.ReadAllText(pnpmYamlPath));
            if (injectWorkspacePackages != true)
            {
                diagnostics.Add(
                    $"Workspace root '{resolvedRoot}' uses pnpm with PublishAsNpmScript, which generates a pnpm 10 Dockerfile using 'pnpm deploy'. " +
                    $"Set 'injectWorkspacePackages: true' in '{pnpmYamlPath}' so pnpm 10 can deploy workspace dependencies without legacy deploy mode.");
            }
        }
        catch (YamlDotNet.Core.YamlException)
        {
            // Malformed pnpm-workspace.yaml is already reported while reading workspace patterns.
        }
        catch (IOException ex)
        {
            diagnostics.Add($"Failed to read '{pnpmYamlPath}': {ex.Message}");
        }
    }

    private static string? TryReadPackageManagerField(string rootPath)
    {
        var path = Path.Combine(rootPath, "package.json");
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            using var stream = File.OpenRead(path);
            return JsonSerializer.Deserialize<PackageJsonPackageManagerInfo>(stream)?.PackageManager;
        }
        catch (JsonException) { }
        catch (IOException) { }

        return null;
    }

    private static void ValidateAppScripts(
        string appDir,
        string appName,
        IReadOnlyList<string>? buildScriptNames,
        string? startScriptName,
        List<string> diagnostics)
    {
        var allScripts = new List<string>();
        if (buildScriptNames is not null)
        {
            allScripts.AddRange(buildScriptNames);
        }
        if (!string.IsNullOrEmpty(startScriptName))
        {
            allScripts.Add(startScriptName);
        }

        if (allScripts.Count == 0)
        {
            return;
        }

        var packageJsonPath = Path.Combine(appDir, "package.json");
        HashSet<string>? declaredScripts;
        try
        {
            using var stream = File.OpenRead(packageJsonPath);
            declaredScripts = ReadScriptNames(JsonSerializer.Deserialize<PackageJsonScriptsInfo>(stream));
        }
        catch (JsonException)
        {
            return; // already diagnosed by app-name reader
        }
        catch (IOException)
        {
            return;
        }

        if (declaredScripts is null)
        {
            return;
        }

        foreach (var script in allScripts.Distinct(StringComparer.Ordinal))
        {
            if (!declaredScripts.Contains(script))
            {
                diagnostics.Add(
                    $"Application '{appName}' references script '{script}' but '{packageJsonPath}' does not declare 'scripts.{script}'. " +
                    $"Declared scripts: {(declaredScripts.Count == 0 ? "(none)" : string.Join(", ", declaredScripts.OrderBy(s => s, StringComparer.Ordinal)))}.");
            }
        }
    }

    /// <summary>
    /// Returns the set of declared script names, or <see langword="null"/> when the package.json
    /// has no <c>scripts</c> field at all (in which case we don't validate — the user has not
    /// declared any scripts at all, so there's nothing to cross-check against).
    /// </summary>
    private static HashSet<string>? ReadScriptNames(PackageJsonScriptsInfo? packageJson)
    {
        if (packageJson?.Scripts is null)
        {
            return null;
        }

        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (var scriptName in packageJson.Scripts.Keys)
        {
            set.Add(scriptName);
        }
        return set;
    }

    private static IReadOnlyList<string> FilterPatterns(IEnumerable<string?> patterns) =>
        patterns.Where(static p => !string.IsNullOrEmpty(p)).Select(static p => p!).ToArray();

    private sealed class PackageJsonNameInfo
    {
        [JsonPropertyName("name")]
        public string? Name { get; set; }
    }

    private sealed class PackageJsonWorkspaceArrayDeclaration
    {
        [JsonPropertyName("workspaces")]
        public List<string?>? Workspaces { get; set; } = [];
    }

    private sealed class PackageJsonWorkspaceObjectDeclaration
    {
        [JsonPropertyName("workspaces")]
        public PackageJsonWorkspaceObject? Workspaces { get; set; }
    }

    private sealed class PackageJsonWorkspaceObject
    {
        [JsonPropertyName("packages")]
        public List<string?>? Packages { get; set; } = [];

        [JsonExtensionData]
        public Dictionary<string, object?>? ExtensionData { get; set; }
    }

    private sealed class PnpmWorkspacePackagesDeclaration
    {
        [YamlMember(Alias = "packages")]
        public List<string?>? Packages { get; set; } = [];
    }

    private sealed class PackageJsonPackageManagerInfo
    {
        [JsonPropertyName("packageManager")]
        public string? PackageManager { get; set; }
    }

    private sealed class PackageJsonScriptsInfo
    {
        [JsonPropertyName("scripts")]
        public Dictionary<string, object?>? Scripts { get; set; }
    }

    private static string PathWithSeparator(string path) =>
        path.EndsWith(Path.DirectorySeparatorChar) ? path : path + Path.DirectorySeparatorChar;

    private static string Capitalize(string value) =>
        string.IsNullOrEmpty(value) ? value : char.ToUpperInvariant(value[0]) + value[1..];

    private static string BuildErrorMessage(string resourceName, List<string> diagnostics)
    {
        var sb = new StringBuilder();
        sb.Append("JavaScript workspace configuration for resource '").Append(resourceName).Append("' is invalid:");
        foreach (var diag in diagnostics)
        {
            sb.AppendLine();
            sb.Append("  - ").Append(diag);
        }
        return sb.ToString();
    }
}
