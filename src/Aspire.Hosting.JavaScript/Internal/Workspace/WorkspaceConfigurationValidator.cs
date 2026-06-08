// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// Validates the JavaScript workspace state for a workspace and its member apps.
// Runs at publish-mode Dockerfile generation time and at run-mode installer
// startup (BeforeStartEvent), NOT at AppHost construction. Errors therefore
// surface as `aspire publish` / run-mode resource errors that read like "my repo
// configuration is wrong" rather than "my .NET code threw".
//
// The validator collects ALL discovered problems into a SINGLE
// DistributedApplicationException (line-oriented message) so the user can fix
// everything in one pass instead of one problem per publish round-trip.
//
// It reuses the cached WorkspaceInfo from WorkspaceMemberDiscovery (computed once
// per workspace) so it does not re-walk the filesystem that the cache-stable
// install layer already walked.

using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Aspire.Hosting.ApplicationModel;

namespace Aspire.Hosting.JavaScript.Internal.Workspace;

internal static class WorkspaceConfigurationValidator
{
    /// <summary>
    /// Validates the workspace configuration for <paramref name="workspace"/> and every member app in
    /// <paramref name="memberApps"/>, throwing a single aggregated <see cref="DistributedApplicationException"/>
    /// when any problem is found. Validation is idempotent: it runs at most once per workspace (multiple
    /// member apps share one workspace installer / Docker context, so they would otherwise re-validate).
    /// </summary>
    /// <param name="workspace">The workspace resource.</param>
    /// <param name="memberApps">The member apps belonging to this workspace.</param>
    /// <param name="isPublishMode">
    /// <see langword="true"/> when running in publish/deploy mode. The pnpm-10 <c>injectWorkspacePackages</c>
    /// check only fires in this mode because it concerns the generated <c>pnpm deploy</c> Dockerfile.
    /// </param>
    public static void Validate(JavaScriptWorkspaceResource workspace, IReadOnlyList<JavaScriptAppResource> memberApps, bool isPublishMode)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(memberApps);

        // Idempotency: a single workspace is shared by all its member apps (one installer, one Docker
        // context). Each member's publish callback / the single BeforeStart handler would otherwise
        // re-run the same checks; mark the workspace validated after the first *successful* pass (see
        // the end of this method) so a pass that threw still re-validates if the host is retried in-process.
        if (workspace.TryGetLastAnnotation<WorkspaceValidatedAnnotation>(out _))
        {
            return;
        }

        var root = workspace.WorkingDirectory;
        var configuredPm = workspace.TryGetLastAnnotation<JavaScriptPackageManagerAnnotation>(out var pm)
            ? pm.ExecutableName
            : ConfiguredPackageManagerFromType(workspace);

        var diagnostics = new List<string>();

        // 1. Root directory exists. Without it, nothing else can be checked.
        if (!Directory.Exists(root))
        {
            diagnostics.Add($"Workspace root '{root}' does not exist.");
            ThrowIfAny(workspace.Name, diagnostics);
            return;
        }

        // 2. Root manifest / lockfile presence.
        var manifests = WorkspaceManifestDiscovery.Discover(root);
        if (!manifests.HasPackageJson)
        {
            diagnostics.Add($"Workspace root '{root}' is missing package.json.");
        }
        if (!manifests.HasLockfile)
        {
            diagnostics.Add(
                $"Workspace root '{root}' has no recognized lockfile " +
                $"(expected one of: {string.Join(", ", WorkspaceManifestDiscovery.RecognizedLockfileNames)}). " +
                "Run the package manager's install at the workspace root before publishing.");
        }

        // 3. Pattern shape validation. Parse first, then run the shape validator inside a try/catch so a
        //    bad glob joins the aggregated diagnostics instead of throwing before the other checks run.
        var rawPatterns = ReadDeclaredPatterns(root, configuredPm);
        try
        {
            WorkspacePatternValidator.Validate(rawPatterns, root);
        }
        catch (DistributedApplicationException ex)
        {
            diagnostics.Add(ex.Message);
        }

        // 4. Member discovery (reuses the cached WorkspaceInfo). Build the declared-name set and detect
        //    patterns that resolved to zero members.
        var workspaceInfo = workspace.GetWorkspaceInfo(configuredPm);
        if (rawPatterns.Count > 0 && workspaceInfo.WorkspaceDirs.Count == 0)
        {
            diagnostics.Add(
                $"Workspace pattern(s) {string.Join(", ", rawPatterns.Select(p => $"'{p}'"))} in '{root}' " +
                "did not match any directory containing a package.json.");
        }

        // 5. Package-manager <-> lockfile / pnpm-workspace.yaml / packageManager-field consistency.
        if (!string.IsNullOrEmpty(configuredPm))
        {
            ValidatePackageManagerConsistency(root, configuredPm, diagnostics);
        }

        // 6. pnpm 10 deploy: PublishAsPackageScript generates a `pnpm deploy` Dockerfile that needs
        //    injectWorkspacePackages. Only relevant in publish/deploy mode.
        if (isPublishMode &&
            string.Equals(configuredPm, "pnpm", StringComparison.Ordinal) &&
            memberApps.Any(IsPackageScriptPublish) &&
            PnpmPackageManagerVersion.TryReadMajorVersion(root) >= 10)
        {
            ValidatePnpmDeployConfiguration(root, diagnostics);
        }

        // 7. Per-app member-name typo + script existence. WHY name vs dir: the user supplies the package
        //    NAME (used by --filter/--workspace/workspace <name>) but the expander resolves DIRECTORIES,
        //    so we compare against the dir->name map built by WorkspaceMemberDiscovery.
        var memberNames = workspaceInfo.Members
            .Select(m => m.PackageName)
            .ToHashSet(StringComparer.Ordinal);
        var nameToDir = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var member in workspaceInfo.Members)
        {
            nameToDir[member.PackageName] = member.RelativeDir;
        }

        foreach (var app in memberApps)
        {
            if (!app.TryGetLastAnnotation<JavaScriptWorkspaceContextAnnotation>(out var ctx))
            {
                continue;
            }

            var projectName = ctx.WorkspaceProjectName;
            if (!memberNames.Contains(projectName))
            {
                diagnostics.Add(
                    $"'{projectName}' is not a declared workspace member of '{root}'. " +
                    $"Declared members: {(memberNames.Count == 0 ? "(none)" : string.Join(", ", memberNames.OrderBy(n => n, StringComparer.Ordinal)))}.");
                continue;
            }

            ValidateAppScripts(app, root, nameToDir[projectName], projectName, isPublishMode, diagnostics);
        }

        ThrowIfAny(workspace.Name, diagnostics);

        // Reaching here means no diagnostics were raised (ThrowIfAny aborts otherwise), so the workspace
        // is validated. Mark it only now — on success — so a failed pass does not suppress a later retry.
        workspace.Annotations.Add(new WorkspaceValidatedAnnotation());
    }

    private static IReadOnlyList<string> ReadDeclaredPatterns(string root, string packageManagerExecutable)
    {
        if (string.Equals(packageManagerExecutable, "pnpm", StringComparison.Ordinal))
        {
            var yamlPath = Path.Combine(root, "pnpm-workspace.yaml");
            return File.Exists(yamlPath) ? PnpmWorkspaceYamlParser.Parse(File.ReadAllText(yamlPath)) : [];
        }

        var packageJsonPath = Path.Combine(root, "package.json");
        return File.Exists(packageJsonPath) ? PackageJsonWorkspacesParser.Parse(File.ReadAllText(packageJsonPath)) : [];
    }

    private static void ValidatePackageManagerConsistency(string root, string configuredPm, List<string> diagnostics)
    {
        var hasNpmLock = File.Exists(Path.Combine(root, "package-lock.json")) ||
                         File.Exists(Path.Combine(root, "npm-shrinkwrap.json"));
        var hasYarnLock = File.Exists(Path.Combine(root, "yarn.lock"));
        var hasPnpmLock = File.Exists(Path.Combine(root, "pnpm-lock.yaml"));
        var hasBunLock = File.Exists(Path.Combine(root, "bun.lock")) ||
                         File.Exists(Path.Combine(root, "bun.lockb"));

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
                $"Workspace root '{root}' has a {lockfilePm} lockfile, but the workspace is configured to use {configuredPm}. " +
                $"Either call Add{Capitalize(lockfilePm)}Workspace instead of Add{Capitalize(configuredPm)}Workspace, or remove the {lockfilePm} lockfile.");
        }

        // pnpm-workspace.yaml is pnpm-specific; its presence under a non-pnpm workspace is a misconfiguration.
        if (File.Exists(Path.Combine(root, "pnpm-workspace.yaml")) && !string.Equals(configuredPm, "pnpm", StringComparison.Ordinal))
        {
            diagnostics.Add(
                $"Workspace root '{root}' contains 'pnpm-workspace.yaml' but the workspace is configured to use {configuredPm}. " +
                "pnpm-workspace.yaml is pnpm-specific; call AddPnpmWorkspace or remove the file.");
        }

        // The corepack packageManager field pins the package manager; honor it as the source of truth.
        var packageManagerField = PnpmPackageManagerField(root);
        if (packageManagerField is not null)
        {
            var declaredPm = packageManagerField.Split('@')[0];
            if (!string.Equals(declaredPm, configuredPm, StringComparison.Ordinal))
            {
                diagnostics.Add(
                    $"Workspace root 'package.json#packageManager' is '{packageManagerField}' but the workspace is configured to use {configuredPm}. " +
                    $"Either call Add{Capitalize(declaredPm)}Workspace instead of Add{Capitalize(configuredPm)}Workspace, or update 'packageManager' in package.json.");
            }
        }
    }

    private static void ValidatePnpmDeployConfiguration(string root, List<string> diagnostics)
    {
        var pnpmYamlPath = Path.Combine(root, "pnpm-workspace.yaml");
        if (!File.Exists(pnpmYamlPath))
        {
            return;
        }

        var injectWorkspacePackages = PnpmWorkspaceYamlParser.ParseInjectWorkspacePackages(File.ReadAllText(pnpmYamlPath));
        if (injectWorkspacePackages != true)
        {
            diagnostics.Add(
                $"Workspace root '{root}' uses pnpm 10 with PublishAsPackageScript, which generates a Dockerfile using 'pnpm deploy'. " +
                $"Set 'injectWorkspacePackages: true' in '{pnpmYamlPath}' so pnpm can deploy workspace dependencies.");
        }
    }

    private static void ValidateAppScripts(JavaScriptAppResource app, string root, string memberDir, string projectName, bool isPublishMode, List<string> diagnostics)
    {
        // Only validate scripts that actually execute in the current mode: the run script is invoked by
        // `aspire run`, while the build/publish scripts are invoked by the generated Dockerfile. Checking
        // a run script during publish (or a build script during run) would be a false positive because
        // that script is never executed in that mode.
        var scriptNames = new List<string>();
        if (!isPublishMode &&
            app.TryGetLastAnnotation<JavaScriptRunScriptAnnotation>(out var runScript) && !string.IsNullOrEmpty(runScript.ScriptName))
        {
            scriptNames.Add(runScript.ScriptName);
        }
        if (isPublishMode)
        {
            if (app.TryGetLastAnnotation<JavaScriptBuildScriptAnnotation>(out var buildScript) && !string.IsNullOrEmpty(buildScript.ScriptName))
            {
                scriptNames.Add(buildScript.ScriptName);
            }
            if (app.TryGetLastAnnotation<JavaScriptPublishModeAnnotation>(out var publishMode) &&
                publishMode.Mode == JavaScriptPublishMode.PackageScript &&
                !string.IsNullOrEmpty(publishMode.ScriptName))
            {
                scriptNames.Add(publishMode.ScriptName);
            }
        }

        if (scriptNames.Count == 0)
        {
            return;
        }

        var packageJsonPath = Path.Combine(root, memberDir.Replace('/', Path.DirectorySeparatorChar), "package.json");
        var declaredScripts = ReadScriptNames(packageJsonPath);
        if (declaredScripts is null)
        {
            // The member declares no "scripts" block at all; there is nothing to cross-check against.
            return;
        }

        foreach (var script in scriptNames.Distinct(StringComparer.Ordinal))
        {
            if (!declaredScripts.Contains(script))
            {
                diagnostics.Add(
                    $"Workspace member '{projectName}' references script '{script}' but '{packageJsonPath}' does not declare 'scripts.{script}'. " +
                    $"Declared scripts: {(declaredScripts.Count == 0 ? "(none)" : string.Join(", ", declaredScripts.OrderBy(s => s, StringComparer.Ordinal)))}.");
            }
        }
    }

    private static HashSet<string>? ReadScriptNames(string packageJsonPath)
    {
        if (!File.Exists(packageJsonPath))
        {
            return null;
        }
        try
        {
            using var stream = File.OpenRead(packageJsonPath);
            var info = JsonSerializer.Deserialize<PackageJsonScriptsInfo>(stream);
            if (info?.Scripts is null)
            {
                return null;
            }
            return [.. info.Scripts.Keys];
        }
        catch (JsonException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    // The configured package manager normally comes from JavaScriptPackageManagerAnnotation, but fall back
    // to the workspace resource type so validation still works if the annotation is not yet attached.
    private static string ConfiguredPackageManagerFromType(JavaScriptWorkspaceResource workspace) => workspace switch
    {
        PnpmWorkspaceResource => "pnpm",
        YarnWorkspaceResource => "yarn",
        NpmWorkspaceResource => "npm",
        BunWorkspaceResource => "bun",
        _ => string.Empty,
    };

    private static string? PnpmPackageManagerField(string root)
    {
        var path = Path.Combine(root, "package.json");
        if (!File.Exists(path))
        {
            return null;
        }
        try
        {
            using var stream = File.OpenRead(path);
            return JsonSerializer.Deserialize<PackageJsonPackageManagerInfo>(stream)?.PackageManager;
        }
        catch (JsonException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    private static bool IsPackageScriptPublish(JavaScriptAppResource app) =>
        app.TryGetLastAnnotation<JavaScriptPublishModeAnnotation>(out var publishMode) &&
        publishMode.Mode == JavaScriptPublishMode.PackageScript;

    private static void ThrowIfAny(string resourceName, List<string> diagnostics)
    {
        if (diagnostics.Count > 0)
        {
            throw new DistributedApplicationException(BuildErrorMessage(resourceName, diagnostics));
        }
    }

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

    private sealed class WorkspaceValidatedAnnotation : IResourceAnnotation;

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
}
